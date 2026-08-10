using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Data;
using Services;
using SS2ReviveData;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Read-only view over the levels Bossa left in StreamingAssets/LegacyLevels.
    ///
    /// These are part of the game install, but the final game only scans StreamingAssets/Levels.
    /// Treating the archive as another UGC source lets the existing Discover screen display and
    /// launch it without copying, converting or uploading anything. Content keys resolve only to
    /// files discovered beneath that exact directory, and a file is hashed again when opened so a
    /// changed file cannot inherit the trust granted to the installed one at startup.
    /// </summary>
    internal static class LegacyLevelCatalog
    {
        internal const string LegacyTag = "LEGACY_MAP";
        internal const string DisplayPrefix = "[LEGACY] ";

        private const string KeyPrefix = "ss2revive-legacy/";
        private const int MaxLevelBytes = 4096000;
        private const int MaxImageBytes = 1024 * 1024;

        private sealed class Entry
        {
            internal string Id;
            internal string ContentKey;
            internal string LevelPath;
            internal string Sha256;
            internal Bossa.Framework.Utils.Guid ClientLevelId;
            internal LevelSummaryImageData LegacyImage;
            internal UgcLevelRecord Record;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> ById =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> ByKey =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static string _root;
        private static bool _browseOnly;

        internal static int Count => Entries.Count;
        internal static bool BrowseActive => _browseOnly;

        internal static void Initialise()
        {
            Entries.Clear();
            ById.Clear();
            ByKey.Clear();

            _root = Path.Combine(Application.streamingAssetsPath, "LegacyLevels");
            if (!Directory.Exists(_root))
            {
                Plugin.Log.LogInfo("No bundled LegacyLevels archive was found at " + _root + ".");
                return;
            }

            var skipped = 0;
            var summaryFallbacks = 0;
            try
            {
                var files = Directory.GetFiles(_root, "*.lvl", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        var entry = ReadEntry(files[i], ref summaryFallbacks);
                        if (entry == null) { skipped++; continue; }

                        // The archive contains one byte-identical onboarding duplicate. Content
                        // hashes are the ids, so it naturally appears only once in the browser.
                        if (ById.ContainsKey(entry.Id)) continue;

                        Entries.Add(entry);
                        ById.Add(entry.Id, entry);
                        ByKey.Add(entry.ContentKey, entry);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Plugin.Log.LogWarning("Skipping legacy level " + files[i] + ": "
                                              + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not scan the bundled LegacyLevels archive: " + ex);
                Entries.Clear();
                ById.Clear();
                ByKey.Clear();
                return;
            }

            Entries.Sort((a, b) => string.Compare(a.Record.Title, b.Record.Title,
                                                   StringComparison.OrdinalIgnoreCase));
            Plugin.Log.LogInfo("Legacy map catalogue ready: " + Entries.Count
                               + " unique map(s), " + summaryFallbacks
                               + " reconstructed summary file(s), " + skipped + " skipped.");
        }

        private static Entry ReadEntry(string levelPath, ref int summaryFallbacks)
        {
            var info = new FileInfo(levelPath);
            if (info.Length < 10 || info.Length > MaxLevelBytes) return null;

            byte[] header;
            using (var input = new FileStream(levelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                header = new byte[10];
                if (input.Read(header, 0, header.Length) != header.Length) return null;
            }

            if (header[0] != 83 || header[1] != 117 || header[2] != 114 || header[3] != 103
                || header[4] != 101 || header[5] != 111 || header[6] != 110 || header[7] != 115)
                return null;

            var version = header[8] | (header[9] << 8);
            if (version < 1 || version >= 29 || version == 25) return null;

            string hash;
            using (var input = new FileStream(levelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                hash = Hex(sha.ComputeHash(input));

            var idBytes = new byte[16];
            for (var i = 0; i < idBytes.Length; i++) idBytes[i] = Convert.ToByte(hash.Substring(i * 2, 2), 16);
            var systemId = new System.Guid(idBytes).ToString();
            var clientId = new Bossa.Framework.Utils.Guid(systemId);

            LevelSummaryData source = null;
            var summaryPath = Path.ChangeExtension(levelPath, ".sdata");
            if (File.Exists(summaryPath))
            {
                try
                {
                    var summaryInfo = new FileInfo(summaryPath);
                    if (summaryInfo.Length > 0 && summaryInfo.Length <= MaxImageBytes)
                        source = LevelFormatGuard.ReadTrustedBundledSummary(
                            File.ReadAllBytes(summaryPath));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Legacy summary could not be read for " + levelPath
                                          + "; reconstructing it. " + ex.Message);
                }
            }

            if (source == null)
            {
                summaryFallbacks++;
                source = new LevelSummaryData(clientId, Path.GetFileNameWithoutExtension(levelPath),
                    string.Empty);
            }

            var originalTitle = string.IsNullOrWhiteSpace(source.levelName)
                ? Path.GetFileNameWithoutExtension(levelPath)
                : source.levelName.Trim();
            var title = Bound(DisplayPrefix + originalTitle, LevelBundle.MaxTitleCharacters);

            var archiveNote = "Local legacy map from the Surgeon Simulator 2 installation"
                              + " (level format " + version + ").";
            var description = string.IsNullOrWhiteSpace(source.levelDescription)
                ? archiveNote
                : archiveNote + "\n\n" + source.levelDescription.Trim();
            description = Bound(description, LevelBundle.MaxDescriptionCharacters);

            var configurations = ValidConfigurations(source.levelConfigurations);
            var tags = ValidTags(source.tags);
            if (!tags.Contains(LegacyTag))
            {
                while (tags.Count >= LevelBundle.MaxTags) tags.RemoveAt(tags.Count - 1);
                tags.Add(LegacyTag);
            }
            AddModeTag(tags, configurations);

            var created = PlausibleDate(source.createdAt)
                ? source.createdAt.ToUniversalTime()
                : new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var updated = PlausibleDate(source.updatedAt)
                ? source.updatedAt.ToUniversalTime()
                : created;

            var validation = new LevelValidationStep("LEGACY_ARCHIVE")
            {
                id = System.Guid.NewGuid().ToString(),
                validated = true,
            };

            var contentKey = KeyPrefix + systemId + "/level";
            var record = new UgcLevelRecord
            {
                Id = systemId,
                Title = title,
                Description = description,
                Status = UgcStore.StatusPublished,
                CreatedAtMs = UnixMilliseconds(created),
                UpdatedAtMs = UnixMilliseconds(updated),
                IsImported = true,
                Configurations = UgcBackend.ToStoredConfigurations(configurations),
                Validations = UgcBackend.ToStoredValidations(
                    new List<LevelValidationStep> { validation }),
            };
            record.Tags.AddRange(tags);
            record.Contents.Add(new UgcContentRecord
            {
                Id = systemId,
                Key = contentKey,
                ImageKey = string.Empty,
                CreatedAtMs = record.UpdatedAtMs,
                ClientVersion = version,
                ContentVersion = 1,
                IsAutoSave = false,
            });

            var image = source.legacyLevelImage;
            if (!ValidImage(image)) image = default(LevelSummaryImageData);

            return new Entry
            {
                Id = systemId,
                ContentKey = contentKey,
                LevelPath = Path.GetFullPath(levelPath),
                Sha256 = hash,
                ClientLevelId = clientId,
                LegacyImage = image,
                Record = record,
            };
        }

        internal static List<UgcLevelRecord> Search(UgcQuery query)
        {
            var result = new List<UgcLevelRecord>();
            if (!_browseOnly) return result;
            if (query == null) query = new UgcQuery();
            for (var i = 0; i < Entries.Count; i++)
            {
                var record = Entries[i].Record;
                if (Matches(record, query)) result.Add(record);
            }
            return result;
        }

        private static bool Matches(UgcLevelRecord level, UgcQuery query)
        {
            if (!query.AnyStatus && !string.IsNullOrEmpty(query.Status)
                && !string.Equals(query.Status, UgcStore.StatusPublished,
                                  StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(query.CreatorId)) return false;
            if (!string.IsNullOrEmpty(query.Tag) && !level.Tags.Contains(query.Tag)) return false;
            if (!string.IsNullOrEmpty(query.TitleContains)
                && level.Title.IndexOf(query.TitleContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (query.Ids != null && query.Ids.Count > 0 && !query.Ids.Contains(level.Id)) return false;
            if (query.PartySize > 0 && !SupportsPartySize(level, query.PartySize)) return false;
            return true;
        }

        private static bool SupportsPartySize(UgcLevelRecord level, int partySize)
        {
            if (level.Configurations == null || level.Configurations.Count == 0) return true;
            foreach (var configuration in level.Configurations.Items)
            {
                var players = configuration["numberPlayers"];
                if (players != null && players.AsInt() == partySize) return true;
            }
            return false;
        }

        internal static UgcLevelRecord Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return ById.TryGetValue(id, out var entry) ? entry.Record : null;
        }

        internal static bool IsLegacy(string id) =>
            !string.IsNullOrEmpty(id) && ById.ContainsKey(id);

        internal static bool OwnsKey(string key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && ByKey.ContainsKey(key);

        internal static byte[] ReadKey(string key)
        {
            if (!ByKey.TryGetValue(key ?? string.Empty, out var entry)) return null;
            try
            {
                var path = Path.GetFullPath(entry.LevelPath);
                var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 10 || info.Length > MaxLevelBytes) return null;
                var bytes = File.ReadAllBytes(path);
                using (var sha = SHA256.Create())
                {
                    if (!string.Equals(Hex(sha.ComputeHash(bytes)), entry.Sha256,
                                       StringComparison.Ordinal))
                    {
                        Plugin.Log.LogError("Refusing changed legacy map bytes at " + path + ".");
                        return null;
                    }
                }
                return bytes;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read legacy map " + entry.LevelPath + ": "
                                      + ex.Message);
                return null;
            }
        }

        internal static void Decorate(LevelSummaryData summary)
        {
            if (summary == null || !ById.TryGetValue(summary.serverLevelId ?? string.Empty,
                                                     out var entry)) return;
            summary.clientLevelId = entry.ClientLevelId;
            summary.legacyLevelImage = entry.LegacyImage;
            summary.levelThumbnailS3Url = null;
            summary.levelContentImage3Url = null;
            summary.otherLevelImageS3Urls = new List<string>();
        }

        internal static void AddTags(List<string> tags)
        {
            if (tags != null && Entries.Count > 0 && !tags.Contains(LegacyTag)) tags.Add(LegacyTag);
        }

        internal static void BeginBrowse() => _browseOnly = true;
        internal static void EndBrowse() => _browseOnly = false;

        internal static bool ApplyBrowseFilter(UgcQuery query)
        {
            if (_browseOnly && query != null) query.Tag = LegacyTag;
            return _browseOnly;
        }

        private static List<LevelConfiguration> ValidConfigurations(
            List<LevelConfiguration> source)
        {
            var result = new List<LevelConfiguration>();
            if (source != null)
            {
                for (var i = 0; i < source.Count; i++)
                {
                    var configuration = source[i];
                    if (configuration == null || configuration.numberOfPlayers < 1
                        || configuration.numberOfPlayers > 4 || configuration.numberOfTeams < 1
                        || configuration.numberOfTeams > 4) continue;
                    if (configuration.levelTeamConfigurations == null)
                        configuration.levelTeamConfigurations = new List<LevelTeamConfiguration>();
                    result.Add(configuration);
                }
            }

            if (result.Count == 0)
            {
                for (var players = 1; players <= 4; players++)
                    result.Add(LevelSummaryFormat.GetDefaultLevelConfiguration(players));
            }
            return result;
        }

        private static List<string> ValidTags(List<string> source)
        {
            var result = new List<string>();
            if (source == null) return result;
            for (var i = 0; i < source.Count && result.Count < LevelBundle.MaxTags; i++)
            {
                var tag = Bound(source[i], LevelBundle.MaxMetadataItemCharacters);
                if (!string.IsNullOrWhiteSpace(tag) && !result.Contains(tag)) result.Add(tag);
            }
            return result;
        }

        private static void AddModeTag(List<string> tags, List<LevelConfiguration> configurations)
        {
            if (configurations == null || configurations.Count == 0) return;
            string tag = null;
            switch (configurations[0].levelType)
            {
                case LevelType.CoOp: tag = UGCService2.TAG_TEAM_COOP; break;
                case LevelType.TeamBased: tag = UGCService2.TAG_TEAM_COMPETITIVE; break;
                case LevelType.FreeForAll: tag = UGCService2.TAG_FREEFORALL; break;
                case LevelType.Freeplay: tag = UGCService2.TAG_TEAM_FREEPLAY; break;
            }
            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag)
                && tags.Count < LevelBundle.MaxTags) tags.Add(tag);
        }

        private static bool ValidImage(LevelSummaryImageData image)
        {
            return image.data != null && image.data.Length <= MaxImageBytes
                   && LevelBundle.IsSafeRawGameImage(
                       (uint)Math.Max(0, image.width), (uint)Math.Max(0, image.height),
                       (uint)image.format, (uint)image.data.Length);
        }

        private static bool PlausibleDate(DateTime value) =>
            value.Year >= 2000 && value.Year <= 2035;

        private static long UnixMilliseconds(DateTime value) =>
            (long)(value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0,
                                                          DateTimeKind.Utc)).TotalMilliseconds;

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var clean = new StringBuilder(Math.Min(value.Length, maximum));
            for (var i = 0; i < value.Length && clean.Length < maximum; i++)
            {
                var c = value[i];
                if (c == '\r') continue;
                if (c == '\n' || c == '\t' || !char.IsControl(c)) clean.Append(c);
            }
            return clean.ToString();
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }
    }
}
