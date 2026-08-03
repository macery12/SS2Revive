using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SS2ReviveData
{
    /// <summary>One saved revision of a level's geometry, props and circuits.</summary>
    public sealed class UgcContentRecord
    {
        public string Id;
        public string Key;
        public string ImageKey;
        public long CreatedAtMs;
        public int ClientVersion;
        public int ContentVersion;
        public bool IsAutoSave;
        public string LockedBy = string.Empty;

        public Json ToStorage() => Json.Object()
            .Add("id", Id)
            .Add("key", Key)
            .Add("imageKey", ImageKey)
            .Add("createdAt", CreatedAtMs)
            .Add("clientVersion", ClientVersion)
            .Add("contentVersion", ContentVersion)
            .Add("autoSave", IsAutoSave)
            .Add("lockedBy", LockedBy);

        public static UgcContentRecord FromStorage(Json value) => new UgcContentRecord
        {
            Id = value["id"].AsStringOr(string.Empty),
            Key = value["key"].AsStringOr(string.Empty),
            ImageKey = value["imageKey"].AsStringOr(string.Empty),
            CreatedAtMs = value["createdAt"].AsLongOr(0),
            ClientVersion = value["clientVersion"].AsIntOr(0),
            ContentVersion = value["contentVersion"].AsIntOr(0),
            IsAutoSave = value["autoSave"].AsBoolOr(false),
            LockedBy = value["lockedBy"].AsStringOr(string.Empty),
        };
    }

    /// <summary>A thumbnail or screenshot attached to a level.</summary>
    public sealed class UgcImageRecord
    {
        public string Id;
        public string Key;
        public long CreatedAtMs;
        public bool IsThumbnail;
        public string CreatorId = string.Empty;

        public Json ToStorage() => Json.Object()
            .Add("id", Id)
            .Add("key", Key)
            .Add("createdAt", CreatedAtMs)
            .Add("thumbnail", IsThumbnail)
            .Add("creatorId", CreatorId);

        public static UgcImageRecord FromStorage(Json value) => new UgcImageRecord
        {
            Id = value["id"].AsStringOr(string.Empty),
            Key = value["key"].AsStringOr(string.Empty),
            CreatedAtMs = value["createdAt"].AsLongOr(0),
            IsThumbnail = value["thumbnail"].AsBoolOr(false),
            CreatorId = value["creatorId"].AsStringOr(string.Empty),
        };
    }

    /// <summary>
    /// One level, as the UGC service saw it: an asset with metadata, a history of content
    /// revisions and a set of images.
    ///
    /// <see cref="Configurations"/> and <see cref="Validations"/> are held as opaque JSON. They
    /// describe team sizes, objectives and the level's validation checklist, all of which are game
    /// types this assembly deliberately cannot reference; the plugin converts them at the boundary
    /// and this store only has to keep them intact.
    /// </summary>
    public sealed class UgcLevelRecord
    {
        public string Id;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string Status = UgcStore.StatusDraft;
        public List<string> CreatorIds = new List<string>();
        public List<string> Tags = new List<string>();
        public long CreatedAtMs;
        public long UpdatedAtMs;
        public long UsedTimes;
        public int Rating;
        public int RatingCount;
        public string ThumbnailKey = string.Empty;
        public Json Configurations;
        public Json Validations;
        public List<UgcContentRecord> Contents = new List<UgcContentRecord>();
        public List<UgcImageRecord> Images = new List<UgcImageRecord>();

        /// <summary>The newest revision, autosaves included. Null for a level with no content.</summary>
        public UgcContentRecord LatestContent()
        {
            UgcContentRecord newest = null;
            for (var i = 0; i < Contents.Count; i++)
            {
                if (newest == null || Contents[i].CreatedAtMs >= newest.CreatedAtMs) newest = Contents[i];
            }
            return newest;
        }

        public int NextContentVersion()
        {
            var highest = 0;
            for (var i = 0; i < Contents.Count; i++)
            {
                if (Contents[i].ContentVersion > highest) highest = Contents[i].ContentVersion;
            }
            return highest + 1;
        }

        public Json ToStorage()
        {
            var creators = Json.Array();
            for (var i = 0; i < CreatorIds.Count; i++) creators.Add(Json.Str(CreatorIds[i]));

            var tags = Json.Array();
            for (var i = 0; i < Tags.Count; i++) tags.Add(Json.Str(Tags[i]));

            var contents = Json.Array();
            for (var i = 0; i < Contents.Count; i++) contents.Add(Contents[i].ToStorage());

            var images = Json.Array();
            for (var i = 0; i < Images.Count; i++) images.Add(Images[i].ToStorage());

            return Json.Object()
                .Add("version", UgcStore.CurrentFormatVersion)
                .Add("id", Id)
                .Add("title", Title)
                .Add("description", Description)
                .Add("status", Status)
                .Add("creators", creators)
                .Add("tags", tags)
                .Add("createdAt", CreatedAtMs)
                .Add("updatedAt", UpdatedAtMs)
                .Add("usedTimes", UsedTimes)
                .Add("rating", Rating)
                .Add("ratingCount", RatingCount)
                .Add("thumbnailKey", ThumbnailKey)
                .Add("configurations", Configurations ?? Json.Array())
                .Add("validations", Validations ?? Json.Array())
                .Add("contents", contents)
                .Add("images", images);
        }

        public static UgcLevelRecord FromStorage(Json value)
        {
            var record = new UgcLevelRecord
            {
                Id = value["id"].AsStringOr(string.Empty),
                Title = value["title"].AsStringOr(string.Empty),
                Description = value["description"].AsStringOr(string.Empty),
                Status = value["status"].AsStringOr(UgcStore.StatusDraft),
                CreatedAtMs = value["createdAt"].AsLongOr(0),
                UpdatedAtMs = value["updatedAt"].AsLongOr(0),
                UsedTimes = value["usedTimes"].AsLongOr(0),
                Rating = value["rating"].AsIntOr(0),
                RatingCount = value["ratingCount"].AsIntOr(0),
                ThumbnailKey = value["thumbnailKey"].AsStringOr(string.Empty),
                Configurations = value["configurations"],
                Validations = value["validations"],
            };

            var creators = value["creators"];
            if (creators != null)
            {
                foreach (var item in creators.Items) record.CreatorIds.Add(item.AsString());
            }

            var tags = value["tags"];
            if (tags != null)
            {
                foreach (var item in tags.Items) record.Tags.Add(item.AsString());
            }

            var contents = value["contents"];
            if (contents != null)
            {
                foreach (var item in contents.Items) record.Contents.Add(UgcContentRecord.FromStorage(item));
            }

            var images = value["images"];
            if (images != null)
            {
                foreach (var item in images.Items) record.Images.Add(UgcImageRecord.FromStorage(item));
            }

            return record;
        }
    }

    /// <summary>What a caller is asking the store to return. All filters are optional.</summary>
    public sealed class UgcQuery
    {
        public string Status;
        public string CreatorId;
        public string Tag;
        public string TitleContains;
        public int PartySize = -1;
        public List<string> Ids;
        public int PageIndex;
        public int ResultsPerPage = UgcStore.DefaultResultsPerPage;
        public bool AnyStatus;
    }

    /// <summary>
    /// The level library, on disk, in place of Bossa's UGC service and its S3 buckets.
    ///
    /// Layout under the store root:
    /// <code>
    ///   levels/&lt;assetId&gt;/asset.json
    ///   levels/&lt;assetId&gt;/content/&lt;contentId&gt;.bin
    ///   levels/&lt;assetId&gt;/images/&lt;imageId&gt;.png
    /// </code>
    ///
    /// One folder per level rather than one index file, because the blobs are the bulk of the data
    /// and a level should be movable, backed up, or deleted by dragging its folder. A corrupt
    /// asset.json then costs its own level and nothing else, which a single index could not promise.
    ///
    /// Blobs are addressed by <em>key</em>, the same indirection the real service used, so the rest
    /// of the game keeps treating a level's content as an opaque handle it fetches later. Keys are
    /// store-relative paths behind a <c>ss2revive/</c> marker; <see cref="TryResolveKey"/> is what
    /// turns one back into a file, and it refuses anything that climbs out of the root.
    /// </summary>
    public sealed class UgcStore
    {
        public const string StatusDraft = "DRAFT";
        public const string StatusValidating = "VALIDATING";
        public const string StatusPublished = "PUBLISHED";
        public const string StatusArchived = "ARCHIVED";

        public const int DefaultResultsPerPage = 20;

        /// <summary>
        /// The asset.json layout this build writes and can read.
        ///
        /// A level written by a newer build is skipped rather than half-read, for the same reason
        /// the progress file refuses one: a level loaded with fields this build does not know about
        /// would be silently rewritten without them on the very next save, which is how a level
        /// quietly loses its configurations. Files predating this field read as 1, which is what
        /// they are.
        /// </summary>
        public const int CurrentFormatVersion = 1;

        /// <summary>Marks a key as ours. The game hands keys it does not understand straight to
        /// S3, so anything we mint has to be recognisable on sight.</summary>
        public const string KeyPrefix = "ss2revive/";

        /// <summary>
        /// How many revisions of one level to keep. Every manual save and every five-minute
        /// autosave adds one, so an afternoon of building would otherwise leave hundreds of copies
        /// of a level that only the newest of which anyone will open. The newest is never pruned.
        /// </summary>
        private const int MaxContentsPerLevel = 24;

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly Dictionary<string, UgcLevelRecord> _levels =
            new Dictionary<string, UgcLevelRecord>(StringComparer.OrdinalIgnoreCase);

        private readonly string _root;
        private readonly Action<string> _warn;
        private readonly object _gate = new object();

        public string Root => _root;

        public UgcStore(string rootDirectory, Action<string> warn = null)
        {
            if (string.IsNullOrEmpty(rootDirectory))
                throw new ArgumentException("A store root is required.", "rootDirectory");

            _root = Path.Combine(rootDirectory, "levels");
            _warn = warn ?? delegate { };

            Directory.CreateDirectory(_root);
            Load();
        }

        // ------------------------------------------------------------------ time

        public static long NowMs() => (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;

        // ------------------------------------------------------------------ read

        public int Count
        {
            get { lock (_gate) return _levels.Count; }
        }

        public UgcLevelRecord Get(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            lock (_gate)
            {
                UgcLevelRecord level;
                return _levels.TryGetValue(assetId, out level) ? level : null;
            }
        }

        public List<UgcLevelRecord> All()
        {
            lock (_gate) return new List<UgcLevelRecord>(_levels.Values);
        }

        /// <summary>
        /// Filters, orders newest-first and pages, mirroring what the search endpoint did.
        /// <paramref name="pageCount"/> is always at least 1, because the terminal divides by it.
        /// </summary>
        public List<UgcLevelRecord> Search(UgcQuery query, out int pageCount)
        {
            if (query == null) query = new UgcQuery();

            var matches = new List<UgcLevelRecord>();
            lock (_gate)
            {
                foreach (var level in _levels.Values)
                {
                    if (Matches(level, query)) matches.Add(level);
                }
            }

            matches.Sort(delegate(UgcLevelRecord a, UgcLevelRecord b)
            {
                return b.UpdatedAtMs.CompareTo(a.UpdatedAtMs);
            });

            var perPage = query.ResultsPerPage > 0 ? query.ResultsPerPage : DefaultResultsPerPage;
            pageCount = matches.Count == 0 ? 1 : (matches.Count + perPage - 1) / perPage;

            var start = query.PageIndex * perPage;
            if (start >= matches.Count) return new List<UgcLevelRecord>();

            var take = Math.Min(perPage, matches.Count - start);
            return matches.GetRange(start, take);
        }

        private static bool Matches(UgcLevelRecord level, UgcQuery query)
        {
            if (!query.AnyStatus && !string.IsNullOrEmpty(query.Status)
                && !string.Equals(level.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(level.Status, StatusArchived, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(query.CreatorId) && !level.CreatorIds.Contains(query.CreatorId))
                return false;

            if (!string.IsNullOrEmpty(query.Tag) && !level.Tags.Contains(query.Tag))
                return false;

            if (!string.IsNullOrEmpty(query.TitleContains)
                && (level.Title == null
                    || level.Title.IndexOf(query.TitleContains, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            if (query.Ids != null && query.Ids.Count > 0 && !query.Ids.Contains(level.Id))
                return false;

            if (query.PartySize > 0 && !SupportsPartySize(level, query.PartySize))
                return false;

            return true;
        }

        /// <summary>
        /// A level advertises the party sizes it was configured for. An unconfigured level answers
        /// yes to everything rather than disappearing from every filtered search - it is far more
        /// likely to be a level saved before its configuration was written than one that genuinely
        /// supports no party at all.
        /// </summary>
        private static bool SupportsPartySize(UgcLevelRecord level, int partySize)
        {
            if (level.Configurations == null || level.Configurations.Count == 0) return true;

            foreach (var configuration in level.Configurations.Items)
            {
                if (configuration["numberPlayers"].AsIntOr(-1) == partySize) return true;
            }
            return false;
        }

        // ----------------------------------------------------------------- write

        public UgcLevelRecord Create(string title, string description, List<string> creatorIds,
                                     List<string> tags, int clientVersion,
                                     byte[] content, byte[] image)
        {
            var now = NowMs();
            var level = new UgcLevelRecord
            {
                Id = System.Guid.NewGuid().ToString(),
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                Status = StatusDraft,
                CreatedAtMs = now,
                UpdatedAtMs = now,
            };

            if (creatorIds != null) level.CreatorIds.AddRange(creatorIds);
            if (tags != null) level.Tags.AddRange(tags);

            lock (_gate)
            {
                Directory.CreateDirectory(LevelDirectory(level.Id));
                _levels[level.Id] = level;

                if (content != null) AddContentLocked(level, clientVersion, content, image, false);
                if (image != null) AddImageLocked(level, image, FirstOr(level.CreatorIds), true);

                SaveLocked(level);
            }

            return level;
        }

        public UgcContentRecord AddContent(UgcLevelRecord level, int clientVersion,
                                           byte[] content, byte[] image, bool isAutoSave)
        {
            if (level == null || content == null) return null;
            lock (_gate)
            {
                var record = AddContentLocked(level, clientVersion, content, image, isAutoSave);
                SaveLocked(level);
                return record;
            }
        }

        public UgcImageRecord AddImage(UgcLevelRecord level, byte[] image, string creatorId, bool isThumbnail)
        {
            if (level == null || image == null) return null;
            lock (_gate)
            {
                var record = AddImageLocked(level, image, creatorId, isThumbnail);
                SaveLocked(level);
                return record;
            }
        }

        public bool SetThumbnail(UgcLevelRecord level, string imageId)
        {
            if (level == null) return false;
            lock (_gate)
            {
                UgcImageRecord target = null;
                for (var i = 0; i < level.Images.Count; i++)
                {
                    var image = level.Images[i];
                    if (string.Equals(image.Id, imageId, StringComparison.Ordinal)) target = image;
                    else image.IsThumbnail = false;
                }

                if (target == null) return false;

                target.IsThumbnail = true;
                level.ThumbnailKey = target.Key;
                Touch(level);
                SaveLocked(level);
                return true;
            }
        }

        public void Update(UgcLevelRecord level)
        {
            if (level == null) return;
            lock (_gate)
            {
                Touch(level);
                SaveLocked(level);
            }
        }

        public bool Delete(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            lock (_gate)
            {
                if (!_levels.Remove(assetId)) return false;

                try
                {
                    var directory = LevelDirectory(assetId);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch (Exception ex)
                {
                    _warn("Could not delete level folder for " + assetId + ": " + ex.Message);
                }
                return true;
            }
        }

        // ------------------------------------------------------------------ keys

        public static bool OwnsKey(string key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Turns a key back into a file path, refusing anything that is not ours or that escapes
        /// the store root. The containment check is the point: a key travels through level metadata
        /// that can be edited by hand or arrive from a peer, and without it a crafted key would
        /// address any file the process can read.
        /// </summary>
        public bool TryResolveKey(string key, out string path)
        {
            path = null;
            if (!OwnsKey(key)) return false;

            var relative = key.Substring(KeyPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0) return false;

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(_root, relative));
            }
            catch (Exception)
            {
                return false;
            }

            var root = Path.GetFullPath(_root);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            path = candidate;
            return true;
        }

        public byte[] ReadKey(string key)
        {
            string path;
            if (!TryResolveKey(key, out path) || !File.Exists(path)) return null;

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                _warn("Could not read " + key + ": " + ex.Message);
                return null;
            }
        }

        // -------------------------------------------------------------- internals

        private UgcContentRecord AddContentLocked(UgcLevelRecord level, int clientVersion,
                                                  byte[] content, byte[] image, bool isAutoSave)
        {
            var id = System.Guid.NewGuid().ToString();
            var record = new UgcContentRecord
            {
                Id = id,
                Key = KeyPrefix + level.Id + "/content/" + id + ".bin",
                CreatedAtMs = NowMs(),
                ClientVersion = clientVersion,
                ContentVersion = level.NextContentVersion(),
                IsAutoSave = isAutoSave,
            };

            if (!WriteBlob(record.Key, content)) return null;

            if (image != null)
            {
                record.ImageKey = KeyPrefix + level.Id + "/content/" + id + ".png";
                if (!WriteBlob(record.ImageKey, image)) record.ImageKey = string.Empty;
            }
            else
            {
                record.ImageKey = string.Empty;
            }

            level.Contents.Add(record);
            PruneContentsLocked(level);
            Touch(level);
            return record;
        }

        private UgcImageRecord AddImageLocked(UgcLevelRecord level, byte[] image, string creatorId, bool isThumbnail)
        {
            var id = System.Guid.NewGuid().ToString();
            var record = new UgcImageRecord
            {
                Id = id,
                Key = KeyPrefix + level.Id + "/images/" + id + ".png",
                CreatedAtMs = NowMs(),
                IsThumbnail = isThumbnail,
                CreatorId = creatorId ?? string.Empty,
            };

            if (!WriteBlob(record.Key, image)) return null;

            if (isThumbnail)
            {
                for (var i = 0; i < level.Images.Count; i++) level.Images[i].IsThumbnail = false;
                level.ThumbnailKey = record.Key;
            }

            level.Images.Add(record);
            Touch(level);
            return record;
        }

        /// <summary>
        /// Drops the oldest revisions once a level has more than <see cref="MaxContentsPerLevel"/>,
        /// keeping the newest and the newest non-autosave. Both are protected because "undo my last
        /// half hour of autosaves" means going back to the last time the player pressed save, and a
        /// prune that took that away would be the one deletion nobody could recover from.
        /// </summary>
        private void PruneContentsLocked(UgcLevelRecord level)
        {
            if (level.Contents.Count <= MaxContentsPerLevel) return;

            var newest = level.LatestContent();

            UgcContentRecord newestManual = null;
            for (var i = 0; i < level.Contents.Count; i++)
            {
                var content = level.Contents[i];
                if (content.IsAutoSave) continue;
                if (newestManual == null || content.CreatedAtMs >= newestManual.CreatedAtMs) newestManual = content;
            }

            var ordered = new List<UgcContentRecord>(level.Contents);
            ordered.Sort(delegate(UgcContentRecord a, UgcContentRecord b)
            {
                return a.CreatedAtMs.CompareTo(b.CreatedAtMs);
            });

            for (var i = 0; i < ordered.Count && level.Contents.Count > MaxContentsPerLevel; i++)
            {
                var candidate = ordered[i];
                if (candidate == newest || candidate == newestManual) continue;

                DeleteBlob(candidate.Key);
                DeleteBlob(candidate.ImageKey);
                level.Contents.Remove(candidate);
            }
        }

        private static void Touch(UgcLevelRecord level) => level.UpdatedAtMs = NowMs();

        private static string FirstOr(List<string> values) =>
            values != null && values.Count > 0 ? values[0] : string.Empty;

        private string LevelDirectory(string assetId) => Path.Combine(_root, assetId);

        private bool WriteBlob(string key, byte[] data)
        {
            string path;
            if (!TryResolveKey(key, out path))
            {
                _warn("Refusing to write blob for key " + key);
                return false;
            }

            try
            {
                AtomicFile.WriteAllBytes(path, data);
                return true;
            }
            catch (Exception ex)
            {
                _warn("Could not write " + key + ": " + ex.Message);
                return false;
            }
        }

        private void DeleteBlob(string key)
        {
            string path;
            if (!TryResolveKey(key, out path)) return;

            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _warn("Could not delete " + key + ": " + ex.Message);
            }
        }

        private void SaveLocked(UgcLevelRecord level)
        {
            var file = Path.Combine(LevelDirectory(level.Id), "asset.json");
            try
            {
                var builder = new StringBuilder(2048);
                level.ToStorage().Write(builder);

                AtomicFile.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _warn("Could not save level " + level.Id + ": " + ex.Message);
            }
        }

        private void Load()
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(_root);
            }
            catch (Exception ex)
            {
                _warn("Could not list the level store: " + ex.Message);
                return;
            }

            for (var i = 0; i < directories.Length; i++)
            {
                var file = Path.Combine(directories[i], "asset.json");
                if (!File.Exists(file)) continue;

                try
                {
                    var parsed = Json.TryParse(File.ReadAllText(file, Encoding.UTF8));
                    if (parsed == null)
                    {
                        _warn("Skipping unreadable level metadata at " + file);
                        continue;
                    }

                    // Skipped, not half-read. A level this build cannot fully understand would be
                    // rewritten without the parts it dropped on the next autosave.
                    var version = parsed["version"].AsIntOr(1);
                    if (version > CurrentFormatVersion)
                    {
                        _warn("Skipping the level at " + directories[i] + ": it was written by a "
                              + "newer version of SS2Revive (format " + version + ", this build "
                              + "reads " + CurrentFormatVersion + "). It has been left untouched.");
                        continue;
                    }

                    var level = UgcLevelRecord.FromStorage(parsed);
                    if (string.IsNullOrEmpty(level.Id))
                    {
                        // Recoverable: the folder is named after the asset, so the id is not lost.
                        level.Id = Path.GetFileName(directories[i]);
                    }

                    _levels[level.Id] = level;
                }
                catch (Exception ex)
                {
                    _warn("Skipping level at " + directories[i] + ": " + ex.Message);
                }
            }
        }

        /// <summary>Formats a count for the log without pulling in a culture-sensitive path.</summary>
        public string Describe() =>
            _levels.Count.ToString(CultureInfo.InvariantCulture) + " level(s) in " + _root;
    }
}
