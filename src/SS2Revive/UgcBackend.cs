using System;
using System.Collections.Generic;
using Data;
using HarmonyLib;
using Services;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Creation Mode, served from the player's own disk.
    ///
    /// Bossa's level editor was never offline. Every save went to <c>/surgeon-ugc/assets</c> and the
    /// level bytes themselves to an S3 bucket, and the client treats that round trip as mandatory:
    /// <c>LevelService.SwitchToLevel</c> uploads a brand-new level before it will load it, and only
    /// continues from the upload's success callback. With the service gone the request failed inside
    /// <c>CrappyHttpsRequestService.Update</c> before either callback could run, so nothing loaded
    /// and nothing gave up either - the camera stayed faded out and the player sat on a black screen
    /// with a working pause menu behind it.
    ///
    /// So this replaces the service rather than the transport. Patching <see cref="UGCService2"/>'s
    /// own methods, instead of the HTTP layer underneath, means no JSON is hand-rolled and no
    /// response has to survive Bossa's two layers of escaping; the level library is
    /// <see cref="UgcStore"/> and everything here is translation between it and the game's types.
    ///
    /// Two things are deliberately local-only. Published levels are visible to the player who made
    /// them and nobody else, because there is no shared index to publish to; and validation, which
    /// on Bossa's side was a queue other players worked through, completes on the spot. Both keep
    /// the surrounding UI honest instead of leaving buttons that can only fail.
    /// </summary>
    internal static class UgcBackend
    {
        private static UgcStore _store;

        internal static bool Available => _store != null;

        /// <summary>
        /// The library itself, for the one caller that is not translating a game call into a local
        /// answer. <see cref="LevelSharing"/> moves whole levels in and out of it as files, which is
        /// a level-library operation with no <see cref="UGCService2"/> method behind it.
        /// </summary>
        internal static UgcStore Store => _store;

        internal static void Initialise(string saveDirectory)
        {
            try
            {
                var root = SaveLocation.ResolveDirectory(saveDirectory);
                _store = new UgcStore(root, message => Plugin.Log.LogWarning("UGC store: " + message));
                Plugin.Log.LogInfo("Local level library ready: " + _store.Describe());
                LegacyLevelCatalog.Initialise();
                CommunityCatalogClient.Initialise(_store, Plugin.CommunityCatalogUrl.Value);
                CommunityPublishClient.Initialise(_store.Root, Plugin.CommunityCatalogUrl.Value);
            }
            catch (Exception ex)
            {
                _store = null;
                Plugin.Log.LogError("Could not open the local level library; Creation Mode will not "
                                    + "be able to save. " + ex);
            }
        }

        // ------------------------------------------------------------------ save

        /// <summary>
        /// Creates the asset on first save and appends a revision on every save after that.
        ///
        /// The summary object is mutated in place and handed back, which is what the original did -
        /// <c>LevelService</c> keeps the same instance and reads <c>serverLevelId</c> and
        /// <c>levelDataS3Url</c> off it immediately afterwards.
        /// </summary>
        internal static void Upload(UGCService2 service, PlayerId playerId, LevelSummaryData summary,
                                    LevelData levelData, byte[] imageData, bool isAutoSave,
                                    Action<LevelSummaryData> succeeded, Action failed)
        {
            if (_store == null) { Defer(failed); return; }

            try
            {
                var content = LevelDataService.WriteLevelData(levelData);
                var clientVersion = (int)levelData._levelVersion;

                var level = string.IsNullOrEmpty(summary.serverLevelId)
                    ? null
                    : _store.Get(summary.serverLevelId);

                var isNew = level == null;
                if (isNew)
                {
                    level = _store.Create(summary.levelName, summary.levelDescription,
                                          PlayerIdsOf(summary.creatorPlayerIds), summary.tags,
                                          clientVersion, content, imageData);
                    if (level == null) { Defer(failed); return; }

                    summary.serverLevelId = level.Id;
                    summary.createdAt = FromUnixMs(level.CreatedAtMs);
                }
                else
                {
                    _store.AddContent(level, clientVersion, content, imageData, isAutoSave);
                }

                // Metadata is written when the level is created and on every manual save, but not
                // on an autosave: an autosave is a snapshot of the geometry, not an edit to the
                // level's identity, and Bossa's version skipped the same writes for one.
                if (isNew || !isAutoSave)
                {
                    level.Title = summary.levelName ?? string.Empty;
                    level.Description = summary.levelDescription ?? string.Empty;
                    level.Tags = new List<string>(summary.tags ?? new List<string>());
                    level.Configurations = ToStoredConfigurations(summary.levelConfigurations);

                    if (summary.levelValidationSteps == null || summary.levelValidationSteps.Count == 0)
                    {
                        summary.levelValidationSteps = new List<LevelValidationStep>
                        {
                            LevelSummaryFormat.GetDefaultLevelValidationStep(),
                        };
                    }
                    level.Validations = ToStoredValidations(summary.levelValidationSteps);
                    _store.Update(level);
                }

                var latest = level.LatestContent();
                summary.LevelStatus = ToStatus(level.Status);
                summary.levelDataS3Url = latest == null ? null : latest.Key;
                summary.levelContentImage3Url = latest == null ? null : latest.ImageKey;
                summary.levelThumbnailS3Url = level.ThumbnailKey;
                summary.updatedAt = FromUnixMs(level.UpdatedAtMs);
                summary.levelContentVersion = latest == null ? 0 : latest.ContentVersion;

                Plugin.Log.LogInfo("Saved level '" + level.Title + "' (" + level.Id + ")"
                                   + (isAutoSave ? " as an autosave" : string.Empty)
                                   + ", revision " + (latest == null ? 0 : latest.ContentVersion) + ".");

                Defer(() =>
                {
                    if (succeeded != null) succeeded(summary);
                    Raise(service, "OnUploadLevelSucceed", summary, isAutoSave);
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Saving level failed: " + ex);
                Defer(failed);
            }
        }

        // ------------------------------------------------------------------ read

        internal static void LatestContent(string serverLevelId, UGCApi.EStatus status,
                                           Action<UGCApi.AssetContentResponse_SurgeonUgc> succeeded,
                                           Action failed)
        {
            var level = Find(serverLevelId, status);
            var latest = level == null ? null : level.LatestContent();
            if (latest == null) { Defer(failed); return; }

            var response = ToContentResponse(latest);
            Defer(() => { if (succeeded != null) succeeded(response); });
        }

        internal static void AllContents(string serverLevelId, UGCApi.EStatus status,
                                         Action<List<UGCApi.AssetContentResponse_SurgeonUgc>> succeeded,
                                         Action failed)
        {
            var level = Find(serverLevelId, status);
            if (level == null) { Defer(failed); return; }

            var responses = new List<UGCApi.AssetContentResponse_SurgeonUgc>();
            for (var i = 0; i < level.Contents.Count; i++) responses.Add(ToContentResponse(level.Contents[i]));
            responses.Sort((a, b) => b.createdAt.CompareTo(a.createdAt));

            Defer(() => { if (succeeded != null) succeeded(responses); });
        }

        internal static void AllImages(string serverLevelId,
                                       Action<List<UGCApi.AssetImageResponse_SurgeonUgc>> succeeded,
                                       Action failed)
        {
            if (LegacyLevelCatalog.IsLegacy(serverLevelId))
            {
                Defer(() =>
                {
                    if (succeeded != null)
                        succeeded(new List<UGCApi.AssetImageResponse_SurgeonUgc>());
                });
                return;
            }

            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            var responses = new List<UGCApi.AssetImageResponse_SurgeonUgc>();
            for (var i = 0; i < level.Images.Count; i++) responses.Add(ToImageResponse(level.Images[i]));

            Defer(() => { if (succeeded != null) succeeded(responses); });
        }

        internal static void Validations(string serverLevelId, UGCApi.EStatus status,
                                         Action<string, UGCApi.EStatus, List<LevelValidationStep>> succeeded,
                                         Action failed)
        {
            var level = Find(serverLevelId, status);
            if (level == null) { Defer(failed); return; }

            var steps = FromStoredValidations(level.Validations);
            Defer(() => { if (succeeded != null) succeeded(serverLevelId, status, steps); });
        }

        // ---------------------------------------------------------------- search

        internal static void Search(UgcQuery query, Action<List<LevelSummaryData>, int> succeeded, Action failed)
        {
            if (_store == null) { Defer(failed); return; }

            try
            {
                int pageCount;
                var matches = SearchLocalAndCommunity(query, out pageCount);

                var summaries = new List<LevelSummaryData>(matches.Count);
                for (var i = 0; i < matches.Count; i++) summaries.Add(ToSummary(matches[i]));

                Defer(() => { if (succeeded != null) succeeded(summaries, pageCount); });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Level search failed: " + ex);
                Defer(failed);
            }
        }

        /// <summary>
        /// Every published level, answered on the spot rather than a frame later.
        ///
        /// <see cref="Search"/> defers because its callers were written around an HTTP round trip.
        /// The free-for-all queue is the one caller that is not: it runs inside a Harmony prefix
        /// that has to decide what the queue contains before it returns, so it needs the list now.
        /// </summary>
        internal static List<LevelSummaryData> PublishedSummaries()
        {
            var summaries = new List<LevelSummaryData>();
            if (_store == null) return summaries;

            try
            {
                int pageCount;
                var matches = SearchLocalAndCommunity(new UgcQuery
                {
                    Status = UgcStore.StatusPublished,
                    ResultsPerPage = int.MaxValue,
                }, out pageCount);

                for (var i = 0; i < matches.Count; i++) summaries.Add(ToSummary(matches[i]));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading the published level list failed: " + ex);
            }

            return summaries;
        }

        internal static bool ConfirmCommunityPublished(string serverLevelId, int revision)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            var latest = level == null ? null : level.LatestContent();
            if (level == null || level.IsImported || latest == null
                || latest.ContentVersion != revision) return false;
            if (string.Equals(level.Status, UgcStore.StatusPublished,
                              StringComparison.OrdinalIgnoreCase)) return true;

            level.Status = UgcStore.StatusPublished;
            MarkValidationsComplete(level);
            _store.Update(level);
            Plugin.Log.LogInfo("Synchronized community publication for '" + level.Title + "' ("
                               + level.Id + "), revision " + revision + ".");
            return true;
        }

        internal static void ReconcileCommunityPublications(List<CommunityCatalogEntry> entries)
        {
            if (entries == null) return;
            for (var i = 0; i < entries.Count; i++)
                ConfirmCommunityPublished(entries[i].Id, entries[i].Revision);
        }

        /// <summary>
        /// Merges before paging so the terminal sees one coherent Discover library. Authored local
        /// levels always win an id collision; an installed community map wins while current, and a
        /// newer catalogue revision replaces its summary so selecting it performs an in-place update.
        /// </summary>
        private static List<UgcLevelRecord> SearchLocalAndCommunity(UgcQuery query, out int pageCount)
        {
            if (query == null) query = new UgcQuery();
            var legacyOnly = LegacyLevelCatalog.ApplyBrowseFilter(query);
            var allQuery = new UgcQuery
            {
                Status = query.Status,
                CreatorId = query.CreatorId,
                Tag = query.Tag,
                TitleContains = query.TitleContains,
                PartySize = query.PartySize,
                Ids = query.Ids,
                PageIndex = 0,
                ResultsPerPage = int.MaxValue,
                AnyStatus = query.AnyStatus,
            };

            List<UgcLevelRecord> combined;
            if (legacyOnly)
            {
                combined = LegacyLevelCatalog.Search(allQuery);
            }
            else
            {
                int ignoredPages;
                combined = _store.Search(allQuery, out ignoredPages);
                var remote = CommunityCatalogClient.Search(allQuery);
                for (var i = 0; i < remote.Count; i++)
                {
                    var entry = remote[i];
                    var installed = _store.Get(entry.Id);
                    if (installed != null)
                    {
                        if (!installed.IsImported) continue;
                        var latest = installed.LatestContent();
                        if (latest != null && latest.ContentVersion >= entry.Revision) continue;
                        for (var localIndex = combined.Count - 1; localIndex >= 0; localIndex--)
                        {
                            if (string.Equals(combined[localIndex].Id, entry.Id,
                                              StringComparison.OrdinalIgnoreCase))
                                combined.RemoveAt(localIndex);
                        }
                    }

                    combined.Add(entry.ToRecord(CommunityCatalogClient.ContentKey(entry),
                                                CommunityCatalogClient.ThumbnailKey(entry)));
                }
            }

            combined.Sort((a, b) => b.UpdatedAtMs.CompareTo(a.UpdatedAtMs));
            var perPage = query.ResultsPerPage > 0 ? query.ResultsPerPage : UgcStore.DefaultResultsPerPage;
            pageCount = combined.Count == 0 ? 1 : (int)(((long)combined.Count + perPage - 1) / perPage);
            var page = query.PageIndex < 0 ? 0 : query.PageIndex;
            var start = (long)page * perPage;
            if (start >= combined.Count) return new List<UgcLevelRecord>();
            var take = Math.Min(perPage, combined.Count - (int)start);
            return combined.GetRange((int)start, take);
        }

        // ----------------------------------------------------------------- edits

        internal static void Delete(UGCService2 service, string serverLevelId, Action succeeded, Action failed)
        {
            if (_store == null || !_store.Delete(serverLevelId)) { Defer(failed); return; }

            Plugin.Log.LogInfo("Deleted level " + serverLevelId + ".");
            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnUGCDataLevelDeleted", serverLevelId);
            });
        }

        internal static void UploadImage(UGCService2 service, string serverLevelId, byte[] imageData,
                                         bool isThumbnail, PlayerId creator,
                                         Action<UGCApi.AssetImageResponse_SurgeonUgc> succeeded,
                                         Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            var image = _store.AddImage(level, imageData, creator.ToString(), isThumbnail);
            if (image == null) { Defer(failed); return; }

            var response = ToImageResponse(image);
            Defer(() =>
            {
                if (succeeded != null) succeeded(response);
                if (isThumbnail) Raise(service, "OnLevelThumbnailChanged", serverLevelId, image.Key);
            });
        }

        internal static void SetThumbnail(UGCService2 service, string serverLevelId, string imageId,
                                          string imageUrl, Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null || !_store.SetThumbnail(level, imageId)) { Defer(failed); return; }

            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnLevelThumbnailChanged", serverLevelId, imageUrl ?? level.ThumbnailKey);
            });
        }

        internal static void SaveConfigurations(UGCService2 service, string serverLevelId,
                                                List<LevelConfiguration> configurations,
                                                Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.Configurations = ToStoredConfigurations(configurations);
            _store.Update(level);

            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnUGCConfigurationUploaded", serverLevelId, configurations);
            });
        }

        internal static void SaveValidationSteps(string serverLevelId, List<LevelValidationStep> steps,
                                                 Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.Validations = ToStoredValidations(steps);
            _store.Update(level);
            Defer(succeeded);
        }

        internal static void ChangeMetadata(UGCService2 service, string serverLevelId,
                                            string title, string description,
                                            Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            if (title != null) level.Title = title;
            if (description != null) level.Description = description;
            _store.Update(level);

            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnUGCSummaryDataChanged", serverLevelId, level.Title, level.Description);
            });
        }

        internal static void ChangeTags(string serverLevelId, List<string> tags, bool add,
                                        Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            if (tags != null)
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    if (add)
                    {
                        if (!level.Tags.Contains(tags[i])) level.Tags.Add(tags[i]);
                    }
                    else
                    {
                        level.Tags.Remove(tags[i]);
                    }
                }
            }

            _store.Update(level);
            Defer(succeeded);
        }

        internal static void ChangeCreators(string serverLevelId, List<PlayerId> creators, bool add,
                                            Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            if (creators != null)
            {
                for (var i = 0; i < creators.Count; i++)
                {
                    var id = creators[i].ToString();
                    if (add)
                    {
                        if (!level.CreatorIds.Contains(id)) level.CreatorIds.Add(id);
                    }
                    else
                    {
                        level.CreatorIds.Remove(id);
                    }
                }
            }

            _store.Update(level);
            Defer(succeeded);
        }

        /// <summary>
        /// Publishing, without a service to publish to.
        ///
        /// On Bossa's side submitting a level put it in a queue for other players to play through
        /// and tick off its validation steps. Nobody else can see this library, so the level is
        /// marked validated and published in one step. That is a deliberate difference from the
        /// original rather than a stub: the alternative is a level stuck in VALIDATING that nothing
        /// will ever move on, and a Publish button that silently does nothing.
        /// </summary>
        internal static void SubmitForValidation(UGCService2 service, string serverLevelId,
                                                 Action<string> succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.Status = UgcStore.StatusPublished;
            MarkValidationsComplete(level);
            _store.Update(level);

            Plugin.Log.LogInfo("Published level '" + level.Title + "' (" + level.Id + ") locally. "
                               + "It is playable from Quick Play on this machine; there is no shared "
                               + "level browser to submit it to.");

            Defer(() =>
            {
                if (succeeded != null) succeeded(serverLevelId);
                Raise(service, "OnLevelUploadedToValidationQueue", serverLevelId, true);
            });
        }

        internal static void CancelValidation(UGCService2 service, string serverLevelId,
                                              Action succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.Status = UgcStore.StatusDraft;
            _store.Update(level);

            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnLevelCancelledValidationSucceeded", serverLevelId, true);
            });
        }

        internal static void ConfirmValidated(string serverLevelId, Action<string> succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.Status = UgcStore.StatusPublished;
            MarkValidationsComplete(level);
            _store.Update(level);

            Defer(() => { if (succeeded != null) succeeded(serverLevelId); });
        }

        internal static void Played(UGCService2 service, string serverLevelId, Action succeeded, Action failed)
        {
            if (LegacyLevelCatalog.IsLegacy(serverLevelId))
            {
                Defer(() =>
                {
                    if (succeeded != null) succeeded();
                    Raise(service, "OnUGCLevelPlayed", serverLevelId);
                });
                return;
            }

            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            level.UsedTimes++;
            _store.Update(level);

            Defer(() =>
            {
                if (succeeded != null) succeeded();
                Raise(service, "OnUGCLevelPlayed", serverLevelId);
            });
        }

        internal static void Rate(string serverLevelId, UGCApi.ERating rating, Action succeeded, Action failed)
        {
            if (LegacyLevelCatalog.IsLegacy(serverLevelId)) { Defer(succeeded); return; }

            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            // One rater, so the score is simply the last opinion given rather than an average.
            level.RatingCount = 1;
            level.Rating = rating == UGCApi.ERating.Up ? 1 : rating == UGCApi.ERating.Down ? -1 : 0;
            _store.Update(level);
            Defer(succeeded);
        }

        internal static void LockContent(string serverLevelId, string contentId, bool locked,
                                         PlayerId playerId, Action<string, bool> succeeded, Action failed)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            if (level == null) { Defer(failed); return; }

            for (var i = 0; i < level.Contents.Count; i++)
            {
                if (!string.Equals(level.Contents[i].Id, contentId, StringComparison.Ordinal)) continue;
                level.Contents[i].LockedBy = locked ? playerId.ToString() : string.Empty;
                break;
            }

            _store.Update(level);
            Defer(() => { if (succeeded != null) succeeded(contentId, locked); });
        }

        /// <summary>
        /// The set of tags any level in the library carries, plus the ones the game filters by.
        /// The originals were a server-wide vocabulary; here the library is the vocabulary.
        /// </summary>
        internal static void AllTags(UGCService2 service, Action succeeded, Action failed)
        {
            if (_store == null) { Defer(failed); return; }

            var tags = new List<string>
            {
                UGCService2.TAG_TEAM_COOP,
                UGCService2.TAG_TEAM_COMPETITIVE,
                UGCService2.TAG_TEAM_FREEPLAY,
                UGCService2.TAG_FREEFORALL,
            };

            var levels = _store.All();
            for (var i = 0; i < levels.Count; i++)
            {
                var levelTags = levels[i].Tags;
                for (var t = 0; t < levelTags.Count; t++)
                {
                    if (!tags.Contains(levelTags[t])) tags.Add(levelTags[t]);
                }
            }

            LegacyLevelCatalog.AddTags(tags);

            service.Tags = tags;
            Defer(succeeded);
        }

        // ------------------------------------------------------------- blob reads

        internal static bool OwnsKey(string key) =>
            LegacyLevelCatalog.OwnsKey(key) || UgcStore.OwnsKey(key);

        internal static byte[] ReadKey(string key)
        {
            if (LegacyLevelCatalog.OwnsKey(key)) return LegacyLevelCatalog.ReadKey(key);
            return _store == null ? null : _store.ReadKey(key);
        }

        internal static bool IsImported(string serverLevelId)
        {
            var level = _store == null ? null : _store.Get(serverLevelId);
            return level != null && level.IsImported;
        }

        internal static bool IsLegacy(string serverLevelId) =>
            LegacyLevelCatalog.IsLegacy(serverLevelId);

        // ------------------------------------------------------------ conversions

        private static UgcLevelRecord Find(string serverLevelId, UGCApi.EStatus status)
        {
            var level = LegacyLevelCatalog.Get(serverLevelId)
                        ?? (_store == null ? null : _store.Get(serverLevelId));
            if (level == null) return null;

            // The caller asks for a level in a particular state. Answering with a level in a
            // different one would hand back content the caller is not looking at - the editor asks
            // for DRAFT while the terminal browses PUBLISHED, and they are different revisions.
            return ToStatus(level.Status) == status ? level : null;
        }

        private static LevelSummaryData ToSummary(UgcLevelRecord level)
        {
            var latest = level.LatestContent();

            var summary = new LevelSummaryData
            {
                serverLevelId = level.Id,
                clientLevelId = Bossa.Framework.Utils.Guid.CreateRandom(),
                LevelStatus = ToStatus(level.Status),
                levelName = level.Title,
                levelDescription = level.Description,
                levelDataS3Url = latest == null ? null : latest.Key,
                levelContentImage3Url = latest == null ? null : latest.ImageKey,
                levelThumbnailS3Url = level.ThumbnailKey,
                otherLevelImageS3Urls = new List<string>(),
                createdAt = FromUnixMs(level.CreatedAtMs),
                updatedAt = FromUnixMs(level.UpdatedAtMs),
                creatorPlayerIds = ToPlayerIds(level.CreatorIds),
                tags = new List<string>(level.Tags),
                rating = level.Rating,
                usedTimes = level.UsedTimes,
                levelContentVersion = latest == null ? 0 : latest.ContentVersion,
                levelConfigurations = FromStoredConfigurations(level.Configurations),
                levelValidationSteps = FromStoredValidations(level.Validations),
            };

            summary.levelValidationFlags = LevelRulesHelper.ConvertValidationStepsToFlag(summary.levelValidationSteps);
            LegacyLevelCatalog.Decorate(summary);
            return summary;
        }

        private static UGCApi.AssetContentResponse_SurgeonUgc ToContentResponse(UgcContentRecord content)
        {
            return new UGCApi.AssetContentResponse_SurgeonUgc
            {
                id = content.Id,
                contentKey = content.Key,
                imageKey = content.ImageKey,
                clientVersion = content.ClientVersion,
                createdAt = FromUnixMs(content.CreatedAtMs),
                isAutoSave = content.IsAutoSave,
                lockedBy = string.IsNullOrEmpty(content.LockedBy)
                    ? PlayerId.Invalid
                    : new PlayerId(content.LockedBy),
            };
        }

        private static UGCApi.AssetImageResponse_SurgeonUgc ToImageResponse(UgcImageRecord image)
        {
            return new UGCApi.AssetImageResponse_SurgeonUgc
            {
                id = image.Id,
                imageKey = image.Key,
                createdAt = FromUnixMs(image.CreatedAtMs),
                isThumbnail = image.IsThumbnail,
                creatorId = string.IsNullOrEmpty(image.CreatorId)
                    ? PlayerId.Invalid
                    : new PlayerId(image.CreatorId),
            };
        }

        internal static Json ToStoredConfigurations(List<LevelConfiguration> configurations)
        {
            var array = Json.Array();
            if (configurations == null) return array;

            for (var i = 0; i < configurations.Count; i++)
            {
                var configuration = configurations[i];
                var teams = Json.Array();

                if (configuration.levelTeamConfigurations != null)
                {
                    for (var t = 0; t < configuration.levelTeamConfigurations.Count; t++)
                    {
                        var team = configuration.levelTeamConfigurations[t];

                        var objectives = Json.Array();
                        if (team.objectives != null)
                        {
                            for (var o = 0; o < team.objectives.Count; o++)
                                objectives.Add(Json.Str(team.objectives[o]));
                        }

                        var players = Json.Array();
                        if (team.playerIndiciesInTeam != null)
                        {
                            for (var p = 0; p < team.playerIndiciesInTeam.Count; p++)
                                players.Add(Json.Num(team.playerIndiciesInTeam[p]));
                        }

                        teams.Add(Json.Object()
                            .Add("objectives", objectives)
                            .Add("playersInTeam", players));
                    }
                }

                array.Add(Json.Object()
                    .Add("id", configuration.id ?? System.Guid.NewGuid().ToString())
                    .Add("numberPlayers", configuration.numberOfPlayers)
                    .Add("numberTeams", configuration.numberOfTeams)
                    .Add("teamMode", ToTeamMode(configuration.levelType))
                    .Add("levelTeamConfigurations", teams));
            }

            return array;
        }

        private static List<LevelConfiguration> FromStoredConfigurations(Json stored)
        {
            var configurations = new List<LevelConfiguration>();
            if (stored == null || stored.Kind != JsonKind.Array) return configurations;

            foreach (var item in stored.Items)
            {
                var configuration = new LevelConfiguration
                {
                    id = item["id"] == null ? null : item["id"].AsString(),
                    numberOfPlayers = item["numberPlayers"] == null ? 0 : item["numberPlayers"].AsInt(),
                    numberOfTeams = item["numberTeams"] == null ? 0 : item["numberTeams"].AsInt(),
                    levelType = FromTeamMode(item["teamMode"] == null ? null : item["teamMode"].AsString()),
                    levelTeamConfigurations = new List<LevelTeamConfiguration>(),
                };

                var teams = item["levelTeamConfigurations"];
                if (teams != null)
                {
                    foreach (var teamItem in teams.Items)
                    {
                        var team = new LevelTeamConfiguration
                        {
                            objectives = new List<string>(),
                            playerIndiciesInTeam = new List<int>(),
                        };

                        var objectives = teamItem["objectives"];
                        if (objectives != null)
                        {
                            foreach (var objective in objectives.Items) team.objectives.Add(objective.AsString());
                        }

                        var players = teamItem["playersInTeam"];
                        if (players != null)
                        {
                            foreach (var player in players.Items) team.playerIndiciesInTeam.Add(player.AsInt());
                        }

                        configuration.levelTeamConfigurations.Add(team);
                    }
                }

                configurations.Add(configuration);
            }

            // A level with no configuration at all cannot be started: the terminal reads party
            // sizes off this list and would offer none. Fall back to what a new level is given.
            if (configurations.Count == 0)
            {
                for (var players = 1; players <= 4; players++)
                    configurations.Add(LevelSummaryFormat.GetDefaultLevelConfiguration(players));
            }

            return configurations;
        }

        internal static Json ToStoredValidations(List<LevelValidationStep> steps)
        {
            var array = Json.Array();
            if (steps == null) return array;

            for (var i = 0; i < steps.Count; i++)
            {
                array.Add(Json.Object()
                    .Add("id", steps[i].id ?? System.Guid.NewGuid().ToString())
                    .Add("description", steps[i].description ?? string.Empty)
                    .Add("validated", steps[i].validated));
            }
            return array;
        }

        private static List<LevelValidationStep> FromStoredValidations(Json stored)
        {
            var steps = new List<LevelValidationStep>();
            if (stored == null || stored.Kind != JsonKind.Array) return steps;

            foreach (var item in stored.Items)
            {
                steps.Add(new LevelValidationStep(item["description"] == null
                              ? string.Empty
                              : item["description"].AsString())
                {
                    id = item["id"] == null ? null : item["id"].AsString(),
                    validated = item["validated"] != null && item["validated"].AsBool(),
                });
            }
            return steps;
        }

        private static void MarkValidationsComplete(UgcLevelRecord level)
        {
            var steps = FromStoredValidations(level.Validations);
            if (steps.Count == 0) steps.Add(LevelSummaryFormat.GetDefaultLevelValidationStep());

            for (var i = 0; i < steps.Count; i++) steps[i].validated = true;
            level.Validations = ToStoredValidations(steps);
        }

        private static string ToTeamMode(LevelType type)
        {
            switch (type)
            {
                case LevelType.CoOp: return "COOP";
                case LevelType.FreeForAll: return "FREE_FOR_ALL";
                case LevelType.TeamBased: return "TEAM_BASED";
                case LevelType.Freeplay: return "FREE_PLAY";
                default: return string.Empty;
            }
        }

        private static LevelType FromTeamMode(string mode)
        {
            switch (mode)
            {
                case "COOP": return LevelType.CoOp;
                case "FREE_FOR_ALL": return LevelType.FreeForAll;
                case "TEAM_BASED": return LevelType.TeamBased;
                case "FREE_PLAY": return LevelType.Freeplay;
                default: return LevelType.None;
            }
        }

        internal static string ToStatusString(UGCApi.EStatus status)
        {
            switch (status)
            {
                case UGCApi.EStatus.Published: return UgcStore.StatusPublished;
                case UGCApi.EStatus.Validating: return UgcStore.StatusValidating;
                case UGCApi.EStatus.Archived: return UgcStore.StatusArchived;
                default: return UgcStore.StatusDraft;
            }
        }

        private static UGCApi.EStatus ToStatus(string status)
        {
            switch (status)
            {
                case UgcStore.StatusPublished: return UGCApi.EStatus.Published;
                case UgcStore.StatusValidating: return UGCApi.EStatus.Validating;
                case UgcStore.StatusArchived: return UGCApi.EStatus.Archived;
                default: return UGCApi.EStatus.Draft;
            }
        }

        private static List<string> PlayerIdsOf(List<PlayerId> playerIds)
        {
            var ids = new List<string>();
            if (playerIds == null) return ids;
            for (var i = 0; i < playerIds.Count; i++) ids.Add(playerIds[i].ToString());
            return ids;
        }

        private static List<PlayerId> ToPlayerIds(List<string> ids)
        {
            var playerIds = new List<PlayerId>();
            if (ids == null) return playerIds;
            for (var i = 0; i < ids.Count; i++)
            {
                if (!string.IsNullOrEmpty(ids[i])) playerIds.Add(new PlayerId(ids[i]));
            }
            return playerIds;
        }

        private static DateTime FromUnixMs(long milliseconds) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds).ToLocalTime();

        // ----------------------------------------------------------------- plumbing

        /// <summary>
        /// Answers on a later frame, never inside the caller's stack.
        ///
        /// Every one of these methods replaces an HTTP round trip, and the callers were written
        /// around that gap. <c>LevelService.SwitchToLevel</c> is the sharp case: its success
        /// callback calls <c>SwitchToLevel</c> again, so a synchronous answer would re-enter a
        /// method that has not finished setting up the state the second call reads.
        /// </summary>
        private static void Defer(Action action)
        {
            if (action == null) return;
            Dispatcher.NextFrame(action);
        }

        /// <summary>
        /// Fires one of <see cref="UGCService2"/>'s events. They are private delegate fields, and
        /// the terminal UI is subscribed to most of them - a save that does not raise
        /// <c>OnUploadLevelSucceed</c> saves correctly and still leaves the level list stale.
        /// </summary>
        private static void Raise(UGCService2 service, string eventName, params object[] args)
        {
            try
            {
                var field = AccessTools.Field(typeof(UGCService2), eventName);
                if (field == null)
                {
                    Plugin.Log.LogWarning("UGCService2 event field not found: " + eventName);
                    return;
                }

                if (!(field.GetValue(service) is Delegate handler)) return;

                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        subscriber.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                        Plugin.Log.LogError(eventName + " subscriber threw: " + inner);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Raising " + eventName + " failed: " + ex);
            }
        }
    }
}
