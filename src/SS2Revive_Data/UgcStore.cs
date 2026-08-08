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
        /// <summary>
        /// True when this level was installed from a bundle rather than authored in this library.
        /// The game has no equivalent field, but the distinction matters to the client UI: an
        /// installed level may be updated or removed, while an authored level must never be
        /// overwritten just because a file arrives with the same id.
        /// </summary>
        public bool IsImported;
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
                .Add("imported", IsImported)
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
                Title = UgcStore.BoundText(value["title"].AsStringOr(string.Empty),
                                  LevelBundle.MaxTitleCharacters),
                Description = UgcStore.BoundText(value["description"].AsStringOr(string.Empty),
                                        LevelBundle.MaxDescriptionCharacters),
                Status = value["status"].AsStringOr(UgcStore.StatusDraft),
                CreatedAtMs = value["createdAt"].AsLongOr(0),
                UpdatedAtMs = value["updatedAt"].AsLongOr(0),
                UsedTimes = value["usedTimes"].AsLongOr(0),
                Rating = value["rating"].AsIntOr(0),
                RatingCount = value["ratingCount"].AsIntOr(0),
                IsImported = value["imported"].AsBoolOr(false),
                ThumbnailKey = value["thumbnailKey"].AsStringOr(string.Empty),
                Configurations = value["configurations"],
                Validations = value["validations"],
            };

            var creators = value["creators"];
            if (creators != null)
            {
                foreach (var item in creators.Items)
                {
                    if (record.CreatorIds.Count >= LevelBundle.MaxCreators) break;
                    record.CreatorIds.Add(UgcStore.BoundText(item.AsString(),
                        LevelBundle.MaxMetadataItemCharacters));
                }
            }

            var tags = value["tags"];
            if (tags != null)
            {
                foreach (var item in tags.Items)
                {
                    if (record.Tags.Count >= LevelBundle.MaxTags) break;
                    record.Tags.Add(UgcStore.BoundText(item.AsString(),
                        LevelBundle.MaxMetadataItemCharacters));
                }
            }

            var contents = value["contents"];
            if (contents != null)
            {
                foreach (var item in contents.Items)
                {
                    if (record.Contents.Count >= 64) break;
                    record.Contents.Add(UgcContentRecord.FromStorage(item));
                }
            }

            var images = value["images"];
            if (images != null)
            {
                foreach (var item in images.Items)
                {
                    if (record.Images.Count >= 64) break;
                    record.Images.Add(UgcImageRecord.FromStorage(item));
                }
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

    /// <summary>The result of installing a portable level bundle into a library.</summary>
    public enum UgcInstallOutcome
    {
        Added,
        Updated,
        Current,
        Older,
        Conflict,
        Failed,
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
        public const int CurrentFormatVersion = 2;

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

            // Claimed attribution in an imported bundle is display-only. It must never make a
            // downloaded map appear in the local player's authoring list, even if a hostile
            // manifest names that player's id as a creator.
            if (!string.IsNullOrEmpty(query.CreatorId)
                && (level.IsImported || !level.CreatorIds.Contains(query.CreatorId)))
            {
                return false;
            }

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

        /// <summary>
        /// Takes in a level that was built on somebody else's machine, keeping the id it arrived
        /// with.
        ///
        /// The id is the whole reason this is not just <see cref="Create"/>. A level's id is what
        /// its share code encodes, so a level that came in under a fresh id would answer to a
        /// different code than the one its author posted - and the code would then mean something
        /// different on every machine that imported it, which is the one thing it cannot do.
        ///
        /// It arrives published, and credited to whoever made it. Neither is cosmetic: published is
        /// what puts it in Discover and in the free-for-all pool, and the original creator id is
        /// what keeps it out of the importer's own Create screen, where editing and re-publishing
        /// somebody else's level would fork the code.
        ///
        /// The id is required to be a GUID by the caller before it gets here, which is also what
        /// makes it safe to use as a directory name.
        /// </summary>
        public UgcLevelRecord Install(UgcLevelRecord prototype, int clientVersion,
                                      int contentVersion, long exportedAtMs, byte[] content,
                                      byte[] contentImage, byte[] thumbnail,
                                      out UgcInstallOutcome outcome, out string error)
        {
            outcome = UgcInstallOutcome.Failed;
            error = null;

            if (prototype == null || content == null || content.Length == 0)
            {
                error = "There is nothing to import.";
                return null;
            }

            System.Guid parsed;
            if (string.IsNullOrEmpty(prototype.Id) || !System.Guid.TryParse(prototype.Id, out parsed))
            {
                error = "The level does not carry a valid id.";
                return null;
            }

            prototype.Id = parsed.ToString();

            lock (_gate)
            {
                UgcLevelRecord existing;
                if (_levels.TryGetValue(prototype.Id, out existing))
                {
                    if (!existing.IsImported)
                    {
                        outcome = UgcInstallOutcome.Conflict;
                        error = "A level you authored already uses this id. It was left untouched.";
                        return existing;
                    }

                    var latest = existing.LatestContent();
                    var installedVersion = latest == null ? 0 : latest.ContentVersion;
                    var incomingVersion = contentVersion > 0 ? contentVersion : 1;

                    if (incomingVersion < installedVersion)
                    {
                        outcome = UgcInstallOutcome.Older;
                        return existing;
                    }

                    if (incomingVersion == installedVersion)
                    {
                        var installedBytes = latest == null ? null : ReadKey(latest.Key);
                        if (BytesEqual(installedBytes, content))
                        {
                            outcome = UgcInstallOutcome.Current;
                            return existing;
                        }

                        outcome = UgcInstallOutcome.Conflict;
                        error = "This file claims the same revision as the installed level but "
                                + "contains different data. It was left untouched.";
                        return existing;
                    }

                    // Stage the whole update on a deep copy. The live record and its persisted
                    // revision history are not changed until the replacement metadata is durable.
                    var replacement = UgcLevelRecord.FromStorage(existing.ToStorage());
                    replacement.Title = prototype.Title ?? string.Empty;
                    replacement.Description = prototype.Description ?? string.Empty;
                    replacement.CreatorIds = new List<string>(prototype.CreatorIds ?? new List<string>());
                    replacement.Tags = new List<string>(prototype.Tags ?? new List<string>());
                    replacement.Configurations = prototype.Configurations;
                    replacement.Validations = prototype.Validations;
                    replacement.Status = StatusPublished;
                    replacement.IsImported = true;

                    var updated = AddContentLocked(replacement, clientVersion, content, contentImage,
                                                   false, incomingVersion, exportedAtMs, false);
                    if (updated == null)
                    {
                        error = "The updated level data could not be written to disk.";
                        return null;
                    }

                    UgcImageRecord newThumbnail = null;
                    if (thumbnail != null)
                        newThumbnail = AddImageLocked(replacement, thumbnail,
                                                      FirstOr(replacement.CreatorIds), true);

                    // Remove old records from the staged metadata first, but leave their blobs in
                    // place until that metadata has been committed. A crash can leak an unreferenced
                    // blob; it must never leave durable metadata pointing at a blob already deleted.
                    var pruned = PruneContentsLocked(replacement, false);

                    if (!SaveLocked(replacement))
                    {
                        DeleteBlob(updated.Key);
                        DeleteBlob(updated.ImageKey);
                        if (newThumbnail != null) DeleteBlob(newThumbnail.Key);
                        outcome = UgcInstallOutcome.Failed;
                        error = "The updated level metadata could not be saved.";
                        return null;
                    }

                    _levels[replacement.Id] = replacement;
                    for (var i = 0; i < pruned.Count; i++)
                    {
                        DeleteBlob(pruned[i].Key);
                        DeleteBlob(pruned[i].ImageKey);
                    }
                    outcome = UgcInstallOutcome.Updated;
                    return replacement;
                }

                prototype.Status = StatusPublished;
                prototype.IsImported = true;
                if (prototype.CreatedAtMs <= 0) prototype.CreatedAtMs = NowMs();
                prototype.UpdatedAtMs = NowMs();

                // Whatever the sending machine recorded about how often it was played, and how its
                // author rated their own level, is theirs and not a fact about this library.
                prototype.UsedTimes = 0;
                prototype.Rating = 0;
                prototype.RatingCount = 0;
                prototype.ThumbnailKey = string.Empty;
                prototype.Contents = new List<UgcContentRecord>();
                prototype.Images = new List<UgcImageRecord>();

                try
                {
                    Directory.CreateDirectory(LevelDirectory(prototype.Id));
                }
                catch (Exception ex)
                {
                    error = "Could not create a folder for it: " + ex.Message;
                    return null;
                }

                // Registered before the blobs are written, because WriteBlob resolves keys through
                // the store root and the record has to be reachable for a later read either way.
                _levels[prototype.Id] = prototype;

                var incoming = contentVersion > 0 ? contentVersion : 1;
                if (AddContentLocked(prototype, clientVersion, content, contentImage, false,
                                     incoming) == null)
                {
                    _levels.Remove(prototype.Id);
                    error = "Its level data could not be written to disk.";
                    return null;
                }

                if (thumbnail != null)
                    AddImageLocked(prototype, thumbnail, FirstOr(prototype.CreatorIds), true);

                if (!SaveLocked(prototype))
                {
                    _levels.Remove(prototype.Id);
                    CleanupFailedInstall(prototype.Id);
                    outcome = UgcInstallOutcome.Failed;
                    error = "The level metadata could not be saved.";
                    return null;
                }
                outcome = UgcInstallOutcome.Added;
                return prototype;
            }
        }

        /// <summary>
        /// Compatibility wrapper for callers that predate version-aware bundle installation.
        /// New code should use <see cref="Install"/> so re-importing can update in place.
        /// </summary>
        public UgcLevelRecord Adopt(UgcLevelRecord prototype, int clientVersion, byte[] content,
                                    byte[] contentImage, byte[] thumbnail, out string error)
        {
            UgcInstallOutcome outcome;
            var level = Install(prototype, clientVersion, 1, 0, content, contentImage, thumbnail,
                                out outcome, out error);
            if (outcome == UgcInstallOutcome.Current)
            {
                error = "already-here";
                return null;
            }
            return outcome == UgcInstallOutcome.Added ? level : null;
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
                if (!_levels.ContainsKey(assetId)) return false;

                try
                {
                    var directory = LevelDirectory(assetId);
                    if (Directory.Exists(directory))
                    {
                        // Renaming inside the same parent is the durable delete. If cleanup is
                        // interrupted or a file is locked afterwards, Load ignores the tombstone
                        // and the map does not reappear on the next launch.
                        var tombstone = Path.Combine(_root, ".deleted-" + assetId + "-"
                                                    + System.Guid.NewGuid().ToString("N"));
                        Directory.Move(directory, tombstone);
                        _levels.Remove(assetId);

                        try { Directory.Delete(tombstone, true); }
                        catch (Exception cleanup)
                        {
                            _warn("Level " + assetId + " was removed, but its tombstone could not "
                                  + "be cleaned up yet: " + cleanup.Message);
                        }
                        return true;
                    }

                    _levels.Remove(assetId);
                    return true;
                }
                catch (Exception ex)
                {
                    _warn("Could not delete level folder for " + assetId + ": " + ex.Message);
                    return false;
                }
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
                                                   byte[] content, byte[] image, bool isAutoSave,
                                                   int contentVersion = 0, long createdAtMs = 0,
                                                   bool prune = true)
        {
            var id = System.Guid.NewGuid().ToString();
            var record = new UgcContentRecord
            {
                Id = id,
                Key = KeyPrefix + level.Id + "/content/" + id + ".bin",
                CreatedAtMs = createdAtMs > 0 ? createdAtMs : NowMs(),
                ClientVersion = clientVersion,
                ContentVersion = contentVersion > 0 ? contentVersion : level.NextContentVersion(),
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
            if (prune) PruneContentsLocked(level);
            Touch(level);
            return record;
        }

        internal static string BoundText(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null) return left == right;
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
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
        private List<UgcContentRecord> PruneContentsLocked(UgcLevelRecord level,
                                                            bool deleteBlobs = true)
        {
            var removed = new List<UgcContentRecord>();
            if (level.Contents.Count <= MaxContentsPerLevel) return removed;

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

                level.Contents.Remove(candidate);
                removed.Add(candidate);
                if (deleteBlobs)
                {
                    DeleteBlob(candidate.Key);
                    DeleteBlob(candidate.ImageKey);
                }
            }

            return removed;
        }

        private void CleanupFailedInstall(string levelId)
        {
            try
            {
                var directory = LevelDirectory(levelId);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception ex)
            {
                _warn("Could not clean up the failed install for " + levelId + ": " + ex.Message);
            }
        }

        private static void Touch(UgcLevelRecord level) => level.UpdatedAtMs = NowMs();

        private static string FirstOr(List<string> values) =>
            values != null && values.Count > 0 ? values[0] : string.Empty;

        private string LevelDirectory(string assetId)
        {
            System.Guid parsed;
            if (string.IsNullOrEmpty(assetId) || !System.Guid.TryParse(assetId, out parsed)
                || !string.Equals(parsed.ToString(), assetId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A level id must be a canonical GUID.", "assetId");
            }

            var root = Path.GetFullPath(_root);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            var candidate = Path.GetFullPath(Path.Combine(root, parsed.ToString()));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The level folder escapes the library root.");

            return candidate;
        }

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

        private bool SaveLocked(UgcLevelRecord level)
        {
            var file = Path.Combine(LevelDirectory(level.Id), "asset.json");
            try
            {
                var builder = new StringBuilder(2048);
                level.ToStorage().Write(builder);

                AtomicFile.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                _warn("Could not save level " + level.Id + ": " + ex.Message);
                return false;
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
                    if (new FileInfo(file).Length > 2 * 1024 * 1024)
                    {
                        _warn("Skipping oversized level metadata at " + file);
                        continue;
                    }

                    var jsonText = File.ReadAllText(file, Encoding.UTF8);
                    if (!HasReasonableJsonDepth(jsonText))
                    {
                        _warn("Skipping deeply nested level metadata at " + file);
                        continue;
                    }

                    var parsed = Json.TryParse(jsonText);
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
                    var folderId = Path.GetFileName(directories[i]);
                    if (string.IsNullOrEmpty(level.Id))
                    {
                        // Recoverable: the folder is named after the asset, so the id is not lost.
                        level.Id = folderId;
                    }

                    System.Guid parsedId, parsedFolder;
                    if (!System.Guid.TryParse(level.Id, out parsedId)
                        || !System.Guid.TryParse(folderId, out parsedFolder)
                        || parsedId != parsedFolder)
                    {
                        _warn("Skipping the level at " + directories[i]
                              + ": its id is not the canonical GUID naming its folder.");
                        continue;
                    }

                    level.Id = parsedId.ToString();
                    var now = NowMs();
                    if (level.CreatedAtMs < 0 || level.CreatedAtMs > now + 24L * 60 * 60 * 1000)
                        level.CreatedAtMs = now;
                    if (level.UpdatedAtMs < level.CreatedAtMs
                        || level.UpdatedAtMs > now + 24L * 60 * 60 * 1000)
                    {
                        level.UpdatedAtMs = now;
                    }
                    _levels[level.Id] = level;
                }
                catch (Exception ex)
                {
                    _warn("Skipping level at " + directories[i] + ": " + ex.Message);
                }
            }
        }

        private static bool HasReasonableJsonDepth(string text)
        {
            var depth = 0;
            var quoted = false;
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{' || c == '[')
                {
                    if (++depth > 32) return false;
                }
                else if (c == '}' || c == ']')
                {
                    if (--depth < 0) return false;
                }
            }
            return depth == 0 && !quoted;
        }

        /// <summary>Formats a count for the log without pulling in a culture-sensitive path.</summary>
        public string Describe() =>
            _levels.Count.ToString(CultureInfo.InvariantCulture) + " level(s) in " + _root;
    }
}
