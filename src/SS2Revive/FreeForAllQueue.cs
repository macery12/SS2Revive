using System;
using System.Collections.Generic;
using Data;
using HarmonyLib;
using Services;

namespace SS2Revive
{
    /// <summary>
    /// Free-for-all, given something to play.
    ///
    /// The departure board's "Free-for-all" is <c>LevelQueueService.EMode.QuickPlay</c>, and its
    /// levels never came from the game. <c>LocalUGCLevelCache.DownloadAllLevels</c> asked Bossa's UGC
    /// service for a hundred levels tagged into the competitive queue - a curated slice of what the
    /// community had published - and everything downstream assumed that request answered. With the
    /// service gone the pool comes back empty, <c>LevelQueueService_QuickPlayMode</c> logs
    /// "No levels in Queue" and calls <c>SwitchToLobby</c>, which is why picking the mode drops you
    /// back in the lobby the moment the vactube finishes.
    ///
    /// So the pool is rebuilt from what this machine actually has: levels you published in Creation
    /// Mode, levels saved locally by the game, and - unless turned off - the levels that ship with
    /// the game. Nothing free-for-all ships with Surgeon Simulator 2, so that last group is the
    /// campaign; it is in by default because a queue with nothing in it is the bug being fixed here,
    /// and it is the only thing standing between a fresh install and an empty mode.
    ///
    /// The seam is <c>PickSmartLevel</c> rather than <c>LocalUGCLevelCache.GetQueue</c> one level up,
    /// and that is deliberate. <c>GetQueue</c>'s caller, <c>LevelsLoaded</c>, dedupes by
    /// <c>serverLevelId</c> against a list of ids it never clears while any candidate remains:
    /// <code>
    ///   while (_smartQueue.Count &lt; _smartLevelsNeededForQueue) {
    ///       ... if (!_recentlyPlayedCompetitiveLevelIds.Contains(serverLevelId)) { ... }
    ///   }
    /// </code>
    /// Levels on disk have no server id, so the second level in a session would match the first's
    /// null id, be skipped, never be removed from the candidate list, and spin that loop forever.
    /// Replacing <c>PickSmartLevel</c> steps over both it and the S3-url requirement in
    /// <c>FindSuitableLevels</c>, which no local level can satisfy either.
    /// </summary>
    internal static class FreeForAllQueue
    {
        /// <summary>
        /// Levels drawn recently, newest last, so the queue does not hand out the same level twice
        /// in a row. Cleared rather than obeyed when it would leave nothing to pick.
        /// </summary>
        private static readonly List<string> Recent = new List<string>();

        private const int RecentMemory = 8;

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("LevelQueueService.PickSmartLevel -> local free-for-all pool", () =>
            {
                var target = PatchSet.Method(
                    typeof(LevelQueueService.LevelQueueService_QuickPlayMode), "PickSmartLevel");

                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(FreeForAllQueue), nameof(PickSmartLevel_Prefix))));
            });
        }

        /// <summary>
        /// Fills the smart queue from the local library instead of from a dead search endpoint.
        ///
        /// Only the host ever gets here - <c>RefreshSmartQueue</c> returns early on clients - and the
        /// queue it builds is broadcast by <c>SmartQueueRefreshed</c>, so everyone plays the same
        /// level without each machine needing the same library.
        ///
        /// The callback is deferred a frame because the original's was: it ran out of an HTTP
        /// response, so no caller has ever been re-entered from inside <c>PickSmartLevel</c>'s own
        /// stack, and the callback ends in <c>OnQueueSetAndSyncedReady</c>, which starts loading a
        /// level.
        /// </summary>
        private static bool PickSmartLevel_Prefix(LevelQueueService.LevelQueueService_QuickPlayMode __instance,
                                                  int levelsNeeded, Action callback)
        {
            try
            {
                // SmartQueue is the backing list itself, not a copy, so adding to it here is what
                // the original's callback would have done.
                if (levelsNeeded > 0 && __instance.SmartQueue != null)
                    Fill(__instance.SmartQueue, levelsNeeded);
            }
            catch (Exception ex)
            {
                // A throw here would leave the queue half-built and the callback unfired, which
                // hangs the vactube. Log it and let the callback run on whatever we did manage.
                Plugin.Log.LogError("Free-for-all: building the level queue threw: " + ex);
            }

            Dispatcher.NextFrame(() => { if (callback != null) callback(); });
            return false;
        }

        private static void Fill(List<LevelSummaryData> queue, int levelsNeeded)
        {
            var partySize = PartySize();
            var pool = BuildPool(partySize, queue);

            if (pool.Count == 0)
            {
                Plugin.Log.LogWarning("Free-for-all has no level to offer " + partySize
                                      + (partySize == 1 ? " player" : " players")
                                      + ". Build one in Creation Mode and publish it, or turn on "
                                      + "FreeForAll.IncludeGameLevels to fall back on the levels "
                                      + "that ship with the game.");
                return;
            }

            var random = new System.Random();

            for (var i = 0; i < levelsNeeded && pool.Count > 0; i++)
            {
                var fresh = WithoutRecentlyPlayed(pool);

                // Every candidate has come up lately - a small library, which is the normal case.
                // Forgetting is better than refusing to start.
                if (fresh.Count == 0)
                {
                    Recent.Clear();
                    fresh = pool;
                }

                var chosen = fresh[random.Next(fresh.Count)];
                pool.Remove(chosen);
                queue.Add(chosen);
                Remember(KeyOf(chosen));

                Plugin.Log.LogInfo("Free-for-all queued '" + chosen.levelName + "' for "
                                   + partySize + (partySize == 1 ? " player." : " players."));
            }
        }

        // ------------------------------------------------------------------ pool

        /// <summary>
        /// Every level on this machine that the current party can actually start, minus the ones
        /// the game keeps out of Quick Play itself.
        /// </summary>
        private static List<LevelSummaryData> BuildPool(int partySize, List<LevelSummaryData> alreadyQueued)
        {
            var pool = new List<LevelSummaryData>();
            var seen = new List<string>();

            for (var i = 0; i < alreadyQueued.Count; i++) seen.Add(KeyOf(alreadyQueued[i]));

            // Levels you made and published come first, because they are what the mode was for.
            Consider(pool, seen, partySize, UgcBackend.PublishedSummaries());

            var summaries = Shell.Instance.GetLevelSummaryDataService();
            if (summaries != null)
            {
                Consider(pool, seen, partySize, summaries.GetLocalLevelSummarys());
                Consider(pool, seen, partySize, summaries.GetInternalLevelSummarys());

                if (Plugin.FreeForAllIncludeGameLevels.Value)
                    Consider(pool, seen, partySize, summaries.GetBundledLevelSummarys());
            }

            return pool;
        }

        private static void Consider(List<LevelSummaryData> pool, List<string> seen, int partySize,
                                     IEnumerable<LevelSummaryData> candidates)
        {
            if (candidates == null) return;

            foreach (var candidate in candidates)
            {
                if (!IsPlayable(candidate, partySize)) continue;

                var key = KeyOf(candidate);
                if (seen.Contains(key)) continue;

                seen.Add(key);
                pool.Add(candidate);
            }
        }

        private static bool IsPlayable(LevelSummaryData summary, int partySize)
        {
            if (summary == null || string.IsNullOrEmpty(summary.levelName)) return false;
            if (summary.levelConfigurations == null || summary.levelConfigurations.Count == 0) return false;

            // The level's own configurations are the authority on how many surgeons it was built
            // for; they are rebuilt from its spawners every time it is saved.
            if (partySize < summary.MinPlayersRequired() || partySize > summary.MaxPlayersSupported())
                return false;

            // A two-team level cannot split an odd party. The game applies the same rule in
            // LocalUGCLevelCache.FindSuitableLevels.
            if (summary.levelConfigurations[0].levelType == LevelType.TeamBased && partySize % 2 != 0)
                return false;

            return !IsReservedByTheGame(summary.clientLevelId);
        }

        /// <summary>
        /// The lobby, the onboarding tutorial, the empty editor templates, the tutorial levels, and
        /// anything listed in <c>StreamingAssets/Settings/QuickPlay_HiddenLevels.txt</c>. All of
        /// these live in the same bundled folder as the levels worth playing.
        /// </summary>
        private static bool IsReservedByTheGame(Bossa.Framework.Utils.Guid clientLevelId)
        {
            if (clientLevelId.Equals(Bossa.Framework.Utils.Guid.Zero())) return true;

            var core = Shell.Instance.GetCoreLevelService();
            if (core != null && core.IsLevelAnInternalCoreLevel(clientLevelId)) return true;

            var configuration = Shell.Instance.GetLevelConfigurationModel();
            if (configuration != null && configuration.IsLevelHiddenFromQuickPlay(clientLevelId)) return true;

            var tutorial = Shell.Instance.GetTutorialGameService();
            var tutorialLevels = tutorial == null ? null : tutorial.TutorialLevels;
            if (tutorialLevels != null)
            {
                for (var i = 0; i < tutorialLevels.Count; i++)
                {
                    if (tutorialLevels[i].Equals(clientLevelId)) return true;
                }
            }

            return false;
        }

        // -------------------------------------------------------------- bookkeeping

        private static List<LevelSummaryData> WithoutRecentlyPlayed(List<LevelSummaryData> pool)
        {
            var fresh = new List<LevelSummaryData>(pool.Count);
            for (var i = 0; i < pool.Count; i++)
            {
                if (!Recent.Contains(KeyOf(pool[i]))) fresh.Add(pool[i]);
            }
            return fresh;
        }

        private static void Remember(string key)
        {
            Recent.Remove(key);
            Recent.Add(key);
            while (Recent.Count > RecentMemory) Recent.RemoveAt(0);
        }

        /// <summary>
        /// A level's identity across the two libraries it can come from. Levels in the local UGC
        /// store are handed out with a fresh <c>clientLevelId</c> each time they are read, so only
        /// the server id identifies those; levels on disk have no server id at all.
        /// </summary>
        private static string KeyOf(LevelSummaryData summary)
        {
            return string.IsNullOrEmpty(summary.serverLevelId)
                ? summary.clientLevelId.ToString()
                : summary.serverLevelId;
        }

        private static int PartySize()
        {
            var network = Shell.Instance.GetNetworkService();
            var members = network == null ? null : network.GetValidGroupMembers();
            var count = members == null ? 1 : members.Count;
            return count < 1 ? 1 : count;
        }
    }
}
