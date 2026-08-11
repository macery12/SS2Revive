using System;
using System.Collections.Generic;
using Data;
using HarmonyLib;
using Services;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Redirects every <see cref="UGCService2"/> call into <see cref="UgcBackend"/>.
    ///
    /// One prefix per public method, all returning false so the original never runs. Patching the
    /// service rather than the HTTP layer beneath it is what makes this tractable: the request
    /// scheduler, the hand-written JSON serialiser and the S3 client all drop out of the picture,
    /// and each prefix is a straight translation of typed arguments into a local answer.
    ///
    /// Harmony binds a prefix's parameters by name, so the names below have to match the game's
    /// exactly - including <c>suceedCallback</c>, which is spelled that way in the original.
    /// Parameters a prefix does not need are simply left out.
    /// </summary>
    internal static class UgcPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var service = typeof(UGCService2);

            Patch(harmony, service, "UploadUGCItemAsync", nameof(Upload_Prefix));
            Patch(harmony, service, "GetLatestContentByLevelIdAsync", nameof(LatestContent_Prefix));
            Patch(harmony, service, "GetAllContentsByLevelIdAsync", nameof(AllContents_Prefix));
            Patch(harmony, service, "GetAllImagesByLevelIdAsync", nameof(AllImages_Prefix));
            Patch(harmony, service, "GetLevelValidationsByLevelIdAsync", nameof(Validations_Prefix));
            Patch(harmony, service, "UploadLevelConfiguration", nameof(Configurations_Prefix));
            Patch(harmony, service, "UploadValidationSteps", nameof(ValidationSteps_Prefix));
            Patch(harmony, service, "DeleteLevel", nameof(Delete_Prefix));
            Patch(harmony, service, "UploadImage", nameof(UploadImage_Prefix));
            Patch(harmony, service, "SetImageAsThumbnail", nameof(SetThumbnail_Prefix));
            Patch(harmony, service, "RateLevel", nameof(Rate_Prefix));
            Patch(harmony, service, "ChangeDescription", nameof(ChangeDescription_Prefix));
            Patch(harmony, service, "ChangeTitle", nameof(ChangeTitle_Prefix));
            Patch(harmony, service, "ChangMetaData", nameof(ChangeMetadata_Prefix));
            Patch(harmony, service, "AddTagsToLevel", nameof(AddTags_Prefix));
            Patch(harmony, service, "RemoveTagsFromLevel", nameof(RemoveTags_Prefix));
            Patch(harmony, service, "SubmitToValidationStatus", nameof(Submit_Prefix));
            Patch(harmony, service, "CancelValidationStatus", nameof(CancelValidation_Prefix));
            Patch(harmony, service, "ConfirmValidated", nameof(ConfirmValidated_Prefix));
            Patch(harmony, service, "GetAllTags", nameof(AllTags_Prefix));
            Patch(harmony, service, "AddCreators", nameof(AddCreators_Prefix));
            Patch(harmony, service, "RemoveCreator", nameof(RemoveCreator_Prefix));
            Patch(harmony, service, "LockSaveContent", nameof(LockContent_Prefix));
            Patch(harmony, service, "PlayedLevel", nameof(Played_Prefix));

            Patch(harmony, service, "SearchCachedDataAsync", nameof(SearchCached_Prefix));
            Patch(harmony, service, "SearchUGCDatabase", nameof(SearchDatabase_Prefix));
            Patch(harmony, service, "GetMyDraftLevelsAsync", nameof(MyDrafts_Prefix));
            Patch(harmony, service, "GetMyValidatingLevelsAsync", nameof(MyValidating_Prefix));
            Patch(harmony, service, "GetMyPublishedLevelsAsync", nameof(MyPublished_Prefix));
            Patch(harmony, service, "GetPublishedLevelByIdsAsync", nameof(PublishedByIds_Prefix));
            Patch(harmony, service, "GetAllValidatingLevelsAsync", nameof(AllValidating_Prefix));
            Patch(harmony, service, "GetLevelsByTagAsync", nameof(ByTag_Prefix));
            Patch(harmony, service, "GetLevelsByTitleAsync", nameof(ByTitle_Prefix));
            Patch(harmony, service, "GetLevelsByLevelIds", nameof(ByIds_Prefix));

            // The last mile. Level bytes and thumbnails are fetched by key, and the game sends any
            // key it does not recognise to S3 - so the keys this backend mints have to be caught
            // here or the load that follows a successful save still goes to a dead bucket.
            PatchSet.Try("CacheService.GetResourceAsync -> local level library", () =>
            {
                var target = PatchSet.Method(typeof(CacheService), "GetResourceAsync");
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(UgcPatches), nameof(GetResource_Prefix))));
            });
        }

        private static void Patch(Harmony harmony, Type type, string methodName, string prefixName)
        {
            PatchSet.Try("UGCService2." + methodName + " -> local level library", () =>
            {
                var target = PatchSet.Method(type, methodName);
                var prefix = AccessTools.Method(typeof(UgcPatches), prefixName);
                if (prefix == null) throw new MissingMethodException("UgcPatches." + prefixName);
                harmony.Patch(target, new HarmonyMethod(prefix));
            });
        }

        // ------------------------------------------------------------------ save

        private static bool Upload_Prefix(UGCService2 __instance, PlayerId playerId,
                                          LevelSummaryData summaryData, LevelData levelData,
                                          byte[] imageData, bool isAutoSave,
                                          Action<LevelSummaryData> suceedCallback, Action failedCallback)
        {
            UgcBackend.Upload(__instance, playerId, summaryData, levelData, imageData, isAutoSave,
                              suceedCallback, failedCallback);
            return false;
        }

        // ------------------------------------------------------------------ read

        private static bool LatestContent_Prefix(string serverLevelId, UGCApi.EStatus status,
                                                 Action<UGCApi.AssetContentResponse_SurgeonUgc> succeedCallback,
                                                 Action failedDelegate)
        {
            UgcBackend.LatestContent(serverLevelId, status, succeedCallback, failedDelegate);
            return false;
        }

        private static bool AllContents_Prefix(string serverLevelId, UGCApi.EStatus status,
                                               Action<List<UGCApi.AssetContentResponse_SurgeonUgc>> succeedCallback,
                                               Action failedDelegate)
        {
            UgcBackend.AllContents(serverLevelId, status, succeedCallback, failedDelegate);
            return false;
        }

        private static bool AllImages_Prefix(string serverLevelId,
                                             Action<List<UGCApi.AssetImageResponse_SurgeonUgc>> succeedCallback,
                                             Action failedCallback)
        {
            UgcBackend.AllImages(serverLevelId, succeedCallback, failedCallback);
            return false;
        }

        private static bool Validations_Prefix(string serverLevelId, UGCApi.EStatus status,
                                               Action<string, UGCApi.EStatus, List<LevelValidationStep>> succeedCallback,
                                               Action failedDelegate)
        {
            UgcBackend.Validations(serverLevelId, status, succeedCallback, failedDelegate);
            return false;
        }

        // ----------------------------------------------------------------- edits

        private static bool Configurations_Prefix(UGCService2 __instance, string serverLevelId,
                                                  List<LevelConfiguration> levelConfigurations,
                                                  Action succeedCallback, Action failedCallback)
        {
            UgcBackend.SaveConfigurations(__instance, serverLevelId, levelConfigurations,
                                          succeedCallback, failedCallback);
            return false;
        }

        private static bool ValidationSteps_Prefix(string serverLevelId,
                                                   List<LevelValidationStep> validationSteps,
                                                   Action succeedCallback, Action failedCallback)
        {
            UgcBackend.SaveValidationSteps(serverLevelId, validationSteps, succeedCallback, failedCallback);
            return false;
        }

        private static bool Delete_Prefix(UGCService2 __instance, string serverLevelId,
                                          Action successCallback, Action failedCallback)
        {
            UgcBackend.Delete(__instance, serverLevelId, successCallback, failedCallback);
            return false;
        }

        private static bool UploadImage_Prefix(UGCService2 __instance, PlayerId playerId,
                                               string serverLevelId, byte[] imageData, bool isThumbnail,
                                               Action<UGCApi.AssetImageResponse_SurgeonUgc> succeedCallback,
                                               Action failedCallback)
        {
            UgcBackend.UploadImage(__instance, serverLevelId, imageData, isThumbnail, playerId,
                                   succeedCallback, failedCallback);
            return false;
        }

        private static bool SetThumbnail_Prefix(UGCService2 __instance, string serverLevelId,
                                                string imageId, string imageUrl,
                                                Action succeedCallback, Action failedCallback)
        {
            UgcBackend.SetThumbnail(__instance, serverLevelId, imageId, imageUrl,
                                    succeedCallback, failedCallback);
            return false;
        }

        private static bool Rate_Prefix(string serverLevelId, UGCApi.ERating rating,
                                        Action succeedCallback, Action failedCallback)
        {
            UgcBackend.Rate(serverLevelId, rating, succeedCallback, failedCallback);
            return false;
        }

        private static bool ChangeDescription_Prefix(UGCService2 __instance, string serverLevelId,
                                                     string newDescription,
                                                     Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeMetadata(__instance, serverLevelId, null, newDescription,
                                      succeedCallback, failedCallback);
            return false;
        }

        private static bool ChangeTitle_Prefix(UGCService2 __instance, string serverLevelId,
                                               string newTitle,
                                               Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeMetadata(__instance, serverLevelId, newTitle, null,
                                      succeedCallback, failedCallback);
            return false;
        }

        private static bool ChangeMetadata_Prefix(UGCService2 __instance, string serverLevelId,
                                                  string newTitle, string newDescription,
                                                  Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeMetadata(__instance, serverLevelId, newTitle, newDescription,
                                      succeedCallback, failedCallback);
            return false;
        }

        private static bool AddTags_Prefix(string serverLevelId, List<string> tags,
                                           Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeTags(serverLevelId, tags, true, succeedCallback, failedCallback);
            return false;
        }

        private static bool RemoveTags_Prefix(string serverLevelId, List<string> tags,
                                              Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeTags(serverLevelId, tags, false, succeedCallback, failedCallback);
            return false;
        }

        private static bool Submit_Prefix(UGCService2 __instance, string serverLevelId,
                                          Action<string> succeedCallback, Action failedCallback)
        {
            UgcBackend.SubmitForValidation(__instance, serverLevelId, succeedCallback, failedCallback);
            return false;
        }

        private static bool CancelValidation_Prefix(UGCService2 __instance, string serverLevelId,
                                                    Action succeedCallback, Action failedCallback)
        {
            UgcBackend.CancelValidation(__instance, serverLevelId, succeedCallback, failedCallback);
            return false;
        }

        private static bool ConfirmValidated_Prefix(string serverLevelId,
                                                    Action<string> succeedCallback, Action failedCallback)
        {
            UgcBackend.ConfirmValidated(serverLevelId, succeedCallback, failedCallback);
            return false;
        }

        private static bool AllTags_Prefix(UGCService2 __instance,
                                           Action succeedCallback, Action failedCallback)
        {
            UgcBackend.AllTags(__instance, succeedCallback, failedCallback);
            return false;
        }

        private static bool AddCreators_Prefix(string levelServerId, List<PlayerId> playerIDsToAdd,
                                               Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeCreators(levelServerId, playerIDsToAdd, true, succeedCallback, failedCallback);
            return false;
        }

        private static bool RemoveCreator_Prefix(PlayerId targetPlayerID, string levelServerId,
                                                 Action succeedCallback, Action failedCallback)
        {
            UgcBackend.ChangeCreators(levelServerId, new List<PlayerId> { targetPlayerID }, false,
                                      succeedCallback, failedCallback);
            return false;
        }

        private static bool LockContent_Prefix(PlayerId playerId, string levelServerId, string contentID,
                                               bool islocked, Action<string, bool> succeedCallback,
                                               Action failedCallback)
        {
            UgcBackend.LockContent(levelServerId, contentID, islocked, playerId,
                                   succeedCallback, failedCallback);
            return false;
        }

        private static bool Played_Prefix(UGCService2 __instance, string serverLevelId,
                                          Action succeedCallback, Action failedCallback)
        {
            UgcBackend.Played(__instance, serverLevelId, succeedCallback, failedCallback);
            return false;
        }

        // ---------------------------------------------------------------- search

        private static bool SearchCached_Prefix(int pageIndex,
                                                Action<List<LevelSummaryData>, int> succeedCallback,
                                                Action failedCallback, int resultsPerPage)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                PageIndex = pageIndex,
                ResultsPerPage = resultsPerPage,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool SearchDatabase_Prefix(UGCApi.EStatus status, int pageIndex,
                                                  Action<List<LevelSummaryData>, int> succeedCallback,
                                                  Action failedCallback, string[] creatorId, string tag,
                                                  string levelName, int partySize, int resultsPerPage,
                                                  List<string> levelIDS)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcBackend.ToStatusString(status),
                CreatorId = creatorId != null && creatorId.Length > 0 ? creatorId[0] : null,
                Tag = tag,
                TitleContains = levelName,
                PartySize = partySize,
                Ids = levelIDS,
                PageIndex = pageIndex,
                ResultsPerPage = resultsPerPage,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool MyDrafts_Prefix(PlayerId playerId, int pageIndex,
                                            Action<List<LevelSummaryData>, int> succeedCallback,
                                            Action failedCallback, bool ignoreStatusLimitation)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusDraft,
                AnyStatus = ignoreStatusLimitation,
                CreatorId = playerId.ToString(),
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool MyValidating_Prefix(PlayerId playerId, int pageIndex,
                                                Action<List<LevelSummaryData>, int> succeedCallback,
                                                Action failedCallback)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusValidating,
                CreatorId = playerId.ToString(),
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool MyPublished_Prefix(PlayerId playerId, int pageIndex,
                                               Action<List<LevelSummaryData>, int> succeedCallback,
                                               Action failedCallback, int partySize)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                CreatorId = playerId.ToString(),
                PartySize = partySize,
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool PublishedByIds_Prefix(int pageIndex, List<string> levelIds,
                                                  Action<List<LevelSummaryData>, int> succeedCallback,
                                                  Action failedCallback)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                Ids = levelIds,
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool AllValidating_Prefix(int pageIndex,
                                                 Action<List<LevelSummaryData>, int> succeedCallback,
                                                 Action failedCallback)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusValidating,
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool ByTag_Prefix(string tag, int pageIndex, int partySize,
                                         Action<List<LevelSummaryData>, int> succeedCallback,
                                         Action failedCallback)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                Tag = tag,
                PartySize = partySize,
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool ByTitle_Prefix(string title, int pageIndex,
                                           Action<List<LevelSummaryData>, int> succeedCallback,
                                           Action failedCallback)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                TitleContains = title,
                PageIndex = pageIndex,
            }, succeedCallback, failedCallback);
            return false;
        }

        private static bool ByIds_Prefix(List<string> levelIDS,
                                         Action<List<LevelSummaryData>, int> succeedCallback,
                                         Action failedCallback, int partySize)
        {
            UgcBackend.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                Ids = levelIDS,
                PartySize = partySize,
            }, succeedCallback, failedCallback);
            return false;
        }

        // ------------------------------------------------------------ blob reads

        /// <summary>
        /// Serves a level's bytes or thumbnail from disk when the key is one of ours, and leaves
        /// every other key on the original path.
        ///
        /// The original already had a local branch, but it only triggers on a key containing
        /// <c>file:</c> and then requires that same string to be a path that exists - which no key
        /// we could mint would satisfy on Windows. Catching our own prefix here is both narrower
        /// and safer: <see cref="UgcStore.TryResolveKey"/> refuses anything outside the library.
        /// </summary>
        private static bool GetResource_Prefix(string resourceUrl, Action<byte[]> succeedDelegate,
                                               Action failedDelegate)
        {
            if (CommunityCatalogClient.OwnsKey(resourceUrl))
            {
                CommunityCatalogClient.ReadKey(resourceUrl, succeedDelegate, failedDelegate);
                return false;
            }

            if (!UgcBackend.OwnsKey(resourceUrl)) return true;

            var bytes = UgcBackend.ReadKey(resourceUrl);
            if (bytes == null)
            {
                Plugin.Log.LogWarning("Level library has no blob for " + resourceUrl + ".");
                if (failedDelegate != null) failedDelegate();
            }
            else if (succeedDelegate != null)
            {
                succeedDelegate(bytes);
            }

            return false;
        }
    }
}
