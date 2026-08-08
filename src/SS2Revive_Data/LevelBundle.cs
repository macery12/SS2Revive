using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SS2ReviveData
{
    /// <summary>
    /// One level in one file, so a level can be handed to another player without a service in the
    /// middle.
    ///
    /// A bundle carries a single revision - the newest - rather than a level's whole history. The
    /// history is a working record for the person building the level; nobody receiving one has any
    /// use for the twenty autosaves that led to it, and shipping them would multiply the file size
    /// by the number of afternoons its author spent on it.
    ///
    /// The container is hand-rolled rather than a zip, for a reason that is not taste: the game's
    /// <c>Managed</c> folder ships no <c>System.IO.Compression.dll</c> and no
    /// <c>System.IO.Compression.FileSystem.dll</c>, so <c>ZipArchive</c> and <c>ZipFile</c> cannot be
    /// resolved at runtime at all. Compression would buy little in any case: the level blob is
    /// already deflated inside its own format and images are raw game texture envelopes. Storing everything
    /// uncompressed also means a bundle cannot be a decompression bomb, which matters when the whole
    /// point of the file is that it arrives from a stranger.
    ///
    /// Layout, all integers little-endian:
    /// <code>
    ///   "SS2REVIVE LEVEL\n"          16 bytes
    ///   uint16                       container version
    ///   repeated:
    ///     uint16                     entry name length in bytes; 0 ends the file
    ///     bytes                      entry name, UTF-8
    ///     int32                      payload length
    ///     bytes                      payload
    /// </code>
    ///
    /// <see cref="Unpack"/> treats every one of those numbers as hostile. Each is bounded before it
    /// is used to size an allocation, entries are counted, and the level blob is checked for the
    /// game's own file magic before it is ever handed to the game. That does not make a bundle safe
    /// to load - the level format itself has unbounded reads in it, which is a separate problem
    /// documented in UGCSERVER.md - but it does mean a malformed or malicious bundle is rejected by
    /// this reader rather than by an out-of-memory somewhere inside Unity.
    /// </summary>
    public sealed class LevelBundle
    {
        /// <summary>What an exported level is called on disk.</summary>
        public const string Extension = ".ss2level";

        /// <summary>The container layout this build writes. Bumped only if the layout changes.</summary>
        public const int CurrentFormatVersion = 2;

        // Limits. Generous against anything a person would really build, tight enough that a
        // crafted header cannot ask for an allocation that matters. For scale: a small hand-built
        // level's blob is around 3 KB and its screenshot around 800 KB.
        public const int MaxContentBytes = 4096000;
        public const int MaxImageBytes = 8 * 1024 * 1024;
        public const int MaxManifestBytes = 1024 * 1024;
        public const int MaxBundleBytes = 24 * 1024 * 1024;

        public const int MaxTitleCharacters = 128;
        public const int MaxDescriptionCharacters = 2048;
        public const int MaxCreators = 4;
        public const int MaxTags = 32;
        public const int MaxMetadataItemCharacters = 128;

        private const int MaxEntries = 16;
        private const int MaxNameBytes = 64;

        private const string ManifestEntry = "asset.json";
        private const string ContentEntry = "level.bin";
        private const string ContentImageEntry = "level.png";
        private const string ThumbnailEntry = "thumb.png";

        private static readonly byte[] Magic =
        {
            (byte)'S', (byte)'S', (byte)'2', (byte)'R', (byte)'E', (byte)'V', (byte)'I', (byte)'V',
            (byte)'E', (byte)' ', (byte)'L', (byte)'E', (byte)'V', (byte)'E', (byte)'L', (byte)'\n',
        };

        /// <summary>
        /// The first eight bytes of every level file the game has ever written, spelling "Surgeons".
        /// Copied from <c>LevelDataService.FILE_START_MAGIC</c>; checking it here rejects a bundle
        /// whose payload is not a level before the game's own reader is asked to make sense of it.
        /// </summary>
        private static readonly byte[] LevelMagic = { 83, 117, 114, 103, 101, 111, 110, 115 };

        // ------------------------------------------------------------------ state

        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public List<string> CreatorIds = new List<string>();
        public List<string> Tags = new List<string>();
        public long CreatedAtMs;
        public long ExportedAtMs;
        public int ClientVersion;
        public int ContentVersion;
        public string ReviveVersion = string.Empty;
        public string ContentSha256 = string.Empty;
        public Json Configurations;
        public Json Validations;

        public byte[] Content;
        public byte[] ContentImage;
        public byte[] Thumbnail;

        /// <summary>The share code for this level, which is what gets posted alongside the file.</summary>
        public string Code => LevelCode.FromLevelId(Id);

        /// <summary>
        /// What to call the exported file. The code is in the name on purpose: a file that has been
        /// through a chat client, a download folder and a rename still says which level it is, and
        /// the name is the thing someone reads back when they paste the code.
        /// </summary>
        public string SuggestedFileName()
        {
            var name = new StringBuilder(64);
            var invalid = Path.GetInvalidFileNameChars();

            var title = Title ?? string.Empty;
            for (var i = 0; i < title.Length && name.Length < 48; i++)
            {
                var character = title[i];
                if (Array.IndexOf(invalid, character) >= 0) continue;
                name.Append(character);
            }

            var stem = name.ToString().Trim();
            if (stem.Length == 0) stem = "level";

            var code = Code;
            return code.Length == 0 ? stem + Extension : stem + " [" + code + "]" + Extension;
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Reads a level out of the library into a bundle, newest revision only.
        ///
        /// <paramref name="readKey"/> is the store's own blob reader rather than a path, because a
        /// key is the only handle the rest of the system has on a level's bytes and resolving one
        /// is the store's job - it is the thing that refuses a key pointing outside the library.
        /// </summary>
        public static LevelBundle FromLevel(UgcLevelRecord level, Func<string, byte[]> readKey,
                                            out string error)
        {
            error = null;

            if (level == null) { error = "There is no such level."; return null; }
            if (readKey == null) { error = "No way to read the level's data."; return null; }

            var latest = level.LatestContent();
            if (latest == null)
            {
                error = "'" + level.Title + "' has never been saved, so there is nothing to export.";
                return null;
            }

            var content = readKey(latest.Key);
            if (content == null || content.Length == 0)
            {
                error = "'" + level.Title + "' is missing its level data on disk.";
                return null;
            }

            if (content.Length > MaxContentBytes)
            {
                error = "'" + level.Title + "' is " + (content.Length / (1024 * 1024))
                        + " MB, past the " + (MaxContentBytes / (1024 * 1024)) + " MB export limit.";
                return null;
            }

            var bundle = new LevelBundle
            {
                Id = level.Id,
                Title = level.Title ?? string.Empty,
                Description = level.Description ?? string.Empty,
                CreatorIds = new List<string>(level.CreatorIds),
                Tags = new List<string>(level.Tags),
                CreatedAtMs = level.CreatedAtMs,
                ExportedAtMs = UgcStore.NowMs(),
                ClientVersion = latest.ClientVersion,
                ContentVersion = latest.ContentVersion,
                ContentSha256 = Sha256Hex(content),
                Configurations = level.Configurations,
                Validations = level.Validations,
                Content = content,
            };

            // Both images are optional. A level with no screenshot is still a level; one that
            // refuses to export because a thumbnail went missing is a level nobody can share.
            bundle.ContentImage = ReadImage(readKey, latest.ImageKey);
            bundle.Thumbnail = ReadImage(readKey, level.ThumbnailKey);

            return bundle;
        }

        private static byte[] ReadImage(Func<string, byte[]> readKey, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var bytes = readKey(key);
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            return bytes;
        }

        /// <summary>Serialises this bundle into the bytes that get written to a <c>.ss2level</c>.</summary>
        public byte[] Pack()
        {
            var storedClientVersion = ClientVersion > 0 ? ClientVersion
                : Content != null && Content.Length >= 10 ? ReadLevelVersion(Content) : 0;
            var storedContentVersion = ContentVersion > 0 ? ContentVersion : 1;
            var manifest = Json.Object()
                .Add("bundleVersion", CurrentFormatVersion)
                .Add("id", Id ?? string.Empty)
                .Add("title", Title ?? string.Empty)
                .Add("description", Description ?? string.Empty)
                .Add("createdAt", CreatedAtMs)
                .Add("exportedAt", ExportedAtMs)
                .Add("clientVersion", storedClientVersion);
            manifest.Add("contentVersion", storedContentVersion);
            manifest.Add("reviveVersion", ReviveVersion ?? string.Empty);
            manifest.Add("contentSha256", string.IsNullOrEmpty(ContentSha256)
                    ? Sha256Hex(Content)
                    : ContentSha256);

            var creators = Json.Array();
            if (CreatorIds != null)
            {
                for (var i = 0; i < CreatorIds.Count; i++) creators.Add(Json.Str(CreatorIds[i]));
            }
            manifest.Add("creators", creators);

            var tags = Json.Array();
            if (Tags != null)
            {
                for (var i = 0; i < Tags.Count; i++) tags.Add(Json.Str(Tags[i]));
            }
            manifest.Add("tags", tags);

            manifest.Add("configurations", Configurations ?? Json.Array());
            manifest.Add("validations", Validations ?? Json.Array());

            var text = new StringBuilder(2048);
            manifest.Write(text);

            using (var stream = new MemoryStream())
            {
                stream.Write(Magic, 0, Magic.Length);
                WriteUInt16(stream, CurrentFormatVersion);

                WriteEntry(stream, ManifestEntry, new UTF8Encoding(false).GetBytes(text.ToString()));
                WriteEntry(stream, ContentEntry, Content);
                WriteEntry(stream, ContentImageEntry, ContentImage);
                WriteEntry(stream, ThumbnailEntry, Thumbnail);

                WriteUInt16(stream, 0);
                return stream.ToArray();
            }
        }

        // ------------------------------------------------------------------ import

        /// <summary>
        /// Reads a bundle that arrived from somewhere else. Every failure is a message fit to show
        /// a player, because every one of them is something they can act on - a truncated download,
        /// the wrong file, a level from a newer build.
        /// </summary>
        public static LevelBundle Unpack(byte[] bytes, out string error)
        {
            error = null;

            if (bytes == null || bytes.Length < Magic.Length + 2)
            {
                error = "The file is empty or truncated.";
                return null;
            }

            if (bytes.Length > MaxBundleBytes)
            {
                error = "The file is " + (bytes.Length / (1024 * 1024)) + " MB, past the "
                        + (MaxBundleBytes / (1024 * 1024)) + " MB limit.";
                return null;
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (bytes[i] == Magic[i]) continue;
                error = "This is not an SS2 Revive level bundle.";
                return null;
            }

            var offset = Magic.Length;
            var version = ReadUInt16(bytes, ref offset);
            if (version < 1 || version > CurrentFormatVersion)
            {
                error = version < 1
                    ? "The bundle has an invalid container version."
                    : "This bundle was written by a newer version of SS2 Revive (format "
                      + version + "; this build reads " + CurrentFormatVersion + ").";
                return null;
            }

            byte[] manifestBytes = null, content = null, contentImage = null, thumbnail = null;
            var knownEntries = new HashSet<string>(StringComparer.Ordinal);

            for (var entries = 0; ; entries++)
            {
                if (entries >= MaxEntries) { error = "The bundle has too many parts."; return null; }
                if (offset + 2 > bytes.Length) { error = "The bundle ends mid-way through."; return null; }

                var nameLength = ReadUInt16(bytes, ref offset);
                if (nameLength == 0) break;

                if (nameLength > MaxNameBytes || offset + nameLength + 4 > bytes.Length)
                {
                    error = "The bundle is malformed.";
                    return null;
                }

                var name = Encoding.UTF8.GetString(bytes, offset, nameLength);
                offset += nameLength;

                var length = ReadInt32(bytes, ref offset);

                // The one place a hostile file could ask for an allocation: bound it against what
                // is left in the buffer before believing it, not after.
                if (length < 0 || length > bytes.Length - offset)
                {
                    error = "The bundle is malformed or truncated.";
                    return null;
                }

                switch (name)
                {
                    case ManifestEntry:
                    case ContentEntry:
                    case ContentImageEntry:
                    case ThumbnailEntry:
                        if (!knownEntries.Add(name))
                        {
                            error = "The bundle contains the same part more than once.";
                            return null;
                        }
                        break;
                    // Unknown parts are skipped rather than refused, so a future build can add one
                    // without every older build rejecting the whole file.
                }

                var limit = name == ManifestEntry ? MaxManifestBytes
                    : name == ContentEntry ? MaxContentBytes
                    : name == ContentImageEntry || name == ThumbnailEntry ? MaxImageBytes
                    : MaxManifestBytes;

                if (length > limit)
                {
                    error = "The bundle contains an oversized " + name + " part.";
                    return null;
                }

                // Unknown future parts are skipped without allocating them. Known parts are copied
                // only after their own type-specific ceiling has been checked.
                if (name == ManifestEntry || name == ContentEntry
                    || name == ContentImageEntry || name == ThumbnailEntry)
                {
                    var payload = new byte[length];
                    Buffer.BlockCopy(bytes, offset, payload, 0, length);

                    switch (name)
                    {
                        case ManifestEntry: manifestBytes = payload; break;
                        case ContentEntry: content = payload; break;
                        case ContentImageEntry: contentImage = payload; break;
                        case ThumbnailEntry: thumbnail = payload; break;
                    }
                }

                offset += length;
            }

            if (offset != bytes.Length)
            {
                error = "The bundle has unexpected data after its final part.";
                return null;
            }

            if (manifestBytes == null || manifestBytes.Length == 0 || manifestBytes.Length > MaxManifestBytes)
            {
                error = "The bundle has no readable level details.";
                return null;
            }

            if (content == null || content.Length == 0)
            {
                error = "The bundle has no level data in it.";
                return null;
            }

            if (content.Length > MaxContentBytes)
            {
                error = "The level data is " + (content.Length / (1024 * 1024)) + " MB, past the "
                        + (MaxContentBytes / (1024 * 1024)) + " MB limit.";
                return null;
            }

            if (!HasLevelMagic(content))
            {
                error = "The bundle's level data is not a Surgeon Simulator 2 level.";
                return null;
            }

            var levelVersion = ReadLevelVersion(content);
            if (levelVersion != 29)
            {
                error = "This shared level uses map format " + levelVersion
                        + ". SS2 Revive accepts format 29 bundles so older unsafe readers are never "
                        + "reached by downloaded content.";
                return null;
            }

            if (!HasReasonableJsonDepth(manifestBytes))
            {
                error = "The bundle's level details are nested too deeply.";
                return null;
            }

            Json manifest;
            try
            {
                manifest = Json.TryParse(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (Exception)
            {
                manifest = null;
            }

            if (manifest == null)
            {
                error = "The bundle's level details could not be read.";
                return null;
            }

            var bundle = new LevelBundle
            {
                Id = manifest["id"].AsStringOr(string.Empty),
                Title = manifest["title"].AsStringOr(string.Empty),
                Description = manifest["description"].AsStringOr(string.Empty),
                CreatedAtMs = manifest["createdAt"].AsLongOr(0),
                ExportedAtMs = manifest["exportedAt"].AsLongOr(0),
                ClientVersion = manifest["clientVersion"].AsIntOr(0),
                ContentVersion = manifest["contentVersion"].AsIntOr(1),
                ReviveVersion = manifest["reviveVersion"].AsStringOr(string.Empty),
                ContentSha256 = manifest["contentSha256"].AsStringOr(string.Empty),
                Configurations = manifest["configurations"],
                Validations = manifest["validations"],
                Content = content,
                ContentImage = IsSafeGameImage(contentImage) ? contentImage : null,
                Thumbnail = IsSafeGameImage(thumbnail) ? thumbnail : null,
            };

            var creators = manifest["creators"];
            if (creators != null)
            {
                foreach (var item in creators.Items)
                {
                    if (bundle.CreatorIds.Count >= MaxCreators) break;
                    bundle.CreatorIds.Add(CleanText(item.AsString(), MaxMetadataItemCharacters));
                }
            }

            var tags = manifest["tags"];
            if (tags != null)
            {
                foreach (var item in tags.Items)
                {
                    if (bundle.Tags.Count >= MaxTags) break;
                    bundle.Tags.Add(CleanText(item.AsString(), MaxMetadataItemCharacters));
                }
            }

            // The id becomes a folder name and the share code, so it has to be a GUID and nothing
            // else. This is also the check that stops a crafted id from naming a path.
            Guid parsed;
            if (string.IsNullOrEmpty(bundle.Id) || !Guid.TryParse(bundle.Id, out parsed))
            {
                error = "The bundle does not carry a valid level id.";
                return null;
            }

            bundle.Id = parsed.ToString();

            bundle.Title = CleanText(bundle.Title, MaxTitleCharacters);
            bundle.Description = CleanText(bundle.Description, MaxDescriptionCharacters);
            if (string.IsNullOrEmpty(bundle.Title)) bundle.Title = "Untitled level";

            var now = UgcStore.NowMs();
            // DateTime.AddMilliseconds throws outside its supported range, and these values reach
            // the terminal's display conversion. Attribution dates are useful, but never trusted.
            if (bundle.CreatedAtMs < 0 || bundle.CreatedAtMs > now + 24L * 60 * 60 * 1000)
                bundle.CreatedAtMs = now;
            if (bundle.ExportedAtMs < bundle.CreatedAtMs
                || bundle.ExportedAtMs > now + 24L * 60 * 60 * 1000)
            {
                bundle.ExportedAtMs = now;
            }

            if (bundle.ClientVersion != levelVersion)
            {
                error = "The bundle's declared map format does not match its level data.";
                return null;
            }

            if (bundle.ContentVersion < 1)
            {
                error = "The bundle does not carry a valid map revision.";
                return null;
            }

            if (!HasSafeStructuredMetadata(bundle.Configurations, bundle.Validations))
            {
                error = "The bundle contains oversized or invalid level configuration metadata.";
                return null;
            }

            var actualHash = Sha256Hex(content);
            if (version >= 2 && !IsSha256(bundle.ContentSha256))
            {
                error = "The bundle does not carry a valid SHA-256 checksum.";
                return null;
            }
            if (!string.IsNullOrEmpty(bundle.ContentSha256)
                && !string.Equals(bundle.ContentSha256, actualHash,
                                  StringComparison.OrdinalIgnoreCase))
            {
                error = "The level data does not match the bundle's SHA-256 checksum.";
                return null;
            }
            bundle.ContentSha256 = actualHash;

            return bundle;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                      || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        private static bool HasSafeStructuredMetadata(Json configurations, Json validations)
        {
            if (configurations != null && configurations.Kind != JsonKind.Array) return false;
            if (validations != null && validations.Kind != JsonKind.Array) return false;
            if (configurations != null && configurations.Count > 8) return false;
            if (validations != null && validations.Count > 32) return false;

            if (configurations != null)
            {
                foreach (var configuration in configurations.Items)
                {
                    var players = configuration["numberPlayers"].AsIntOr(1);
                    var teamCount = configuration["numberTeams"].AsIntOr(1);
                    if (players < 1 || players > 4 || teamCount < 0 || teamCount > 4) return false;

                    var teams = configuration["levelTeamConfigurations"];
                    if (teams != null && (teams.Kind != JsonKind.Array || teams.Count > 4)) return false;
                    if (teams == null) continue;

                    foreach (var team in teams.Items)
                    {
                        var objectives = team["objectives"];
                        var indices = team["playersInTeam"];
                        if (objectives != null
                            && (objectives.Kind != JsonKind.Array || objectives.Count > 32)) return false;
                        if (indices != null
                            && (indices.Kind != JsonKind.Array || indices.Count > 4)) return false;

                        if (objectives != null)
                        {
                            foreach (var objective in objectives.Items)
                            {
                                if (objective.AsString().Length > MaxMetadataItemCharacters) return false;
                            }
                        }

                        if (indices != null)
                        {
                            foreach (var index in indices.Items)
                            {
                                var value = index.AsIntOr(-1);
                                if (value < 0 || value > 3) return false;
                            }
                        }
                    }
                }
            }

            if (validations != null)
            {
                foreach (var validation in validations.Items)
                {
                    if (validation["description"].AsStringOr(string.Empty).Length > 255) return false;
                }
            }

            return true;
        }

        private static bool HasLevelMagic(byte[] content)
        {
            if (content.Length < LevelMagic.Length + 2) return false;
            for (var i = 0; i < LevelMagic.Length; i++)
            {
                if (content[i] != LevelMagic[i]) return false;
            }
            return true;
        }

        private static int ReadLevelVersion(byte[] content) => content[8] | (content[9] << 8);

        /// <summary>
        /// Images use the game's bit-packed LevelSummaryImageData envelope, not PNG despite the
        /// historical <c>.png</c> key suffix. Its stock reader allocates both the declared byte
        /// count and a texture of the declared dimensions without bounds, so validate both here.
        /// </summary>
        public static bool IsSafeGameImage(byte[] image)
        {
            if (image == null || image.Length < 13 || image.Length > MaxImageBytes) return false;

            var bit = 0;
            uint width, height, format, dataLength;
            // Serializer.WriteInt(0, int.MaxValue) consumes 31 bits, not 32.
            if (!TryReadGameBits(image, ref bit, 31, out width)
                || !TryReadGameBits(image, ref bit, 31, out height)
                || !TryReadGameBits(image, ref bit, 4, out format)
                || !TryReadGameBits(image, ref bit, 31, out dataLength))
            {
                return false;
            }

            bit = (bit + 7) & ~7;
            if (!IsSafeRawGameImage(width, height, format, dataLength))
            {
                return false;
            }

            return (ulong)bit + (ulong)dataLength * 8 <= (ulong)image.Length * 8;
        }

        /// <summary>
        /// Validates the unwrapped fields before Unity allocates a Texture2D. The game serialiser
        /// reserves values 0..8, but only these six are real uncompressed TextureFormat values in
        /// this build. Require at least the base texture's byte count. The game can preserve extra
        /// platform texture bytes (its own 512x289 ARGB32 images do), so equality would reject
        /// files written by the stock serializer; the independent 8 MB ceiling bounds that tail.
        /// </summary>
        public static bool IsSafeRawGameImage(uint width, uint height, uint format, uint dataLength)
        {
            if (width == 0 || height == 0 || width > 2048 || height > 2048
                || (ulong)width * height > 4UL * 1024 * 1024
                || dataLength == 0 || dataLength > MaxImageBytes)
            {
                return false;
            }

            uint bytesPerPixel;
            switch (format)
            {
                case 1: // Alpha8
                    bytesPerPixel = 1;
                    break;
                case 2: // ARGB4444
                case 7: // RGB565
                    bytesPerPixel = 2;
                    break;
                case 3: // RGB24
                    bytesPerPixel = 3;
                    break;
                case 4: // RGBA32
                case 5: // ARGB32
                    bytesPerPixel = 4;
                    break;
                default:
                    return false;
            }

            return (ulong)width * height * bytesPerPixel <= dataLength;
        }

        private static bool TryReadGameBits(byte[] bytes, ref int bitOffset, int count,
                                            out uint value)
        {
            value = 0;
            if (count < 0 || count > 32 || bitOffset < 0
                || (long)bitOffset + count > (long)bytes.Length * 8)
            {
                return false;
            }

            var written = 0;
            while (written < count)
            {
                var wordIndex = bitOffset / 32;
                var withinWord = bitOffset % 32;
                var byteIndex = wordIndex * 4;

                uint word = 0;
                for (var i = 0; i < 4; i++)
                {
                    if (byteIndex + i < bytes.Length)
                        word |= (uint)bytes[byteIndex + i] << (i * 8);
                }

                var take = Math.Min(count - written, 32 - withinWord);
                var mask = take == 32 ? uint.MaxValue : (1u << take) - 1u;
                value |= ((word >> withinWord) & mask) << written;
                bitOffset += take;
                written += take;
            }
            return true;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        private static string CleanText(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var clean = new StringBuilder(Math.Min(value.Length, maximum));
            for (var i = 0; i < value.Length && clean.Length < maximum; i++)
            {
                var character = value[i];
                if (character == '\r' || character == '\n' || character == '\t')
                    character = ' ';
                else if (char.IsControl(character))
                    continue;
                clean.Append(character);
            }
            return clean.ToString().Trim();
        }

        /// <summary>
        /// The JSON parser is intentionally small and recursive. A one-megabyte manifest made only
        /// of '[' characters could otherwise exhaust the thread stack before the parser gets a
        /// chance to reject it. This preflight understands strings and escapes, which is enough to
        /// count structural nesting without trying to parse the document twice.
        /// </summary>
        private static bool HasReasonableJsonDepth(byte[] utf8)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < utf8.Length; i++)
            {
                var character = (char)utf8[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }

                if (character == '"') inString = true;
                else if (character == '{' || character == '[')
                {
                    if (++depth > 32) return false;
                }
                else if (character == '}' || character == ']')
                {
                    if (--depth < 0) return false;
                }
            }

            return depth == 0 && !inString;
        }

        // ------------------------------------------------------------------ bytes

        private static void WriteEntry(Stream stream, string name, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;

            var nameBytes = new UTF8Encoding(false).GetBytes(name);
            WriteUInt16(stream, nameBytes.Length);
            stream.Write(nameBytes, 0, nameBytes.Length);
            WriteInt32(stream, payload.Length);
            stream.Write(payload, 0, payload.Length);
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static int ReadUInt16(byte[] bytes, ref int offset)
        {
            var value = bytes[offset] | (bytes[offset + 1] << 8);
            offset += 2;
            return value;
        }

        private static int ReadInt32(byte[] bytes, ref int offset)
        {
            var value = bytes[offset]
                        | (bytes[offset + 1] << 8)
                        | (bytes[offset + 2] << 16)
                        | (bytes[offset + 3] << 24);
            offset += 4;
            return value;
        }
    }
}
