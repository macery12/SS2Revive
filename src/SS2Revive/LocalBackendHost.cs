using System;
using Services;
using SS2ReviveData;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Owns the in-process backend and connects it to the two things it cannot reach on its own:
    /// the game's install directory, and Steam.
    ///
    /// <see cref="SS2ReviveData.LocalBackend"/> deliberately knows nothing about Unity or
    /// Steamworks - it targets netstandard2.0 and has no package references, which is what lets it
    /// stay a plain, testable library rather than something only reachable through a running game.
    /// Everything platform-shaped lives here instead.
    /// </summary>
    internal static class LocalBackendHost
    {
        private static LocalBackend _backend;

        internal static bool Available => _backend != null;

        internal static LocalBackend Backend => _backend;

        private static bool _identityResolved;

        // A one-shot restore may race a stale offline ProgressionData write during startup. Keep
        // the floor for the rest of that process so the old copy cannot undo a restore that was
        // already durably written to the local backend.
        private static bool _level50FloorThisSession;
        private static long _level50Xp;
        private static bool _level50WarningLogged;

        internal static void Initialise()
        {
            var options = new LocalBackendOptions
            {
                // Application.dataPath is the "<game>_Data" folder, which is where
                // StreamingAssets/Content/Data lives. The catalogue is read from the player's own
                // installation at startup, so nothing has to be extracted, shipped, or kept in
                // step with a game update by hand.
                ContentDirectory = Application.dataPath,
                SaveDirectory = Plugin.SaveDirectory.Value,
                GrantAllCosmetics = Plugin.GrantAllCosmetics.Value,

                // LocalPlayerId is deliberately left unset - see EnsureLocalIdentity.
            };

            try
            {
                _backend = new LocalBackend(options,
                    message => Plugin.Log.LogInfo(message),
                    message => Plugin.Log.LogWarning(message));
            }
            catch (Exception ex)
            {
                _backend = null;
                Plugin.Log.LogError("Local backend failed to start: " + ex);
                return;
            }

            _backend.Peers = SteamPeers.Directory;

            Plugin.Log.LogInfo("Local backend ready: " + _backend.Describe());
            Plugin.Log.LogInfo("Progress is saved in " + _backend.SaveDirectory
                               + " - outside the game folder, so a Steam file verification or a "
                               + "BepInEx reinstall cannot remove it.");
        }

        /// <summary>
        /// Tells the backend who "self" is, once Steam can answer that.
        ///
        /// It cannot be captured at construction. BepInEx runs a plugin's Awake long before the
        /// game reaches <c>Shell.OnStart -> InitPlatforms</c>, so <c>SteamAPI.Init</c> has not run
        /// and <c>SteamIdentity</c> would hand back its offline placeholder.
        ///
        /// Getting this wrong is not cosmetic. The client asks for its own progression and a
        /// friend's through the same endpoint, distinguished only by the id in the path, so a
        /// backend holding a stale identity would treat the player's own progression as a
        /// stranger's and refuse it - which reads, from the game, as progression never loading.
        ///
        /// Polled rather than pushed because it has to be right regardless of which patches are
        /// enabled: with the auth bypass turned off there is no point in our code that observes
        /// sign-in at all. Once resolved this is a single bool test per frame.
        /// </summary>
        internal static void EnsureLocalIdentity()
        {
            if (_identityResolved) return;

            var backend = _backend;
            if (backend == null) return;

            // Polling stops on a real Steam account and only on a real one. The offline
            // placeholder is still installed in the meantime, because the game mints exactly the
            // same placeholder for itself when Steam is missing - so the two agree, and a session
            // without Steam still gets its progression rather than a permanent 404.
            var steamId = SteamIdentity.GetSteamId64();
            var playerId = steamId != 0UL
                ? SteamIdentity.BuildPlayerIdString(steamId)
                : SteamIdentity.GetLocalPlayerId().ToString();

            if (steamId != 0UL) _identityResolved = true;

            if (string.Equals(backend.LocalPlayerId, playerId, StringComparison.Ordinal)) return;

            backend.LocalPlayerId = playerId;

            var personaName = SteamIdentity.GetPersonaName();
            if (!string.IsNullOrEmpty(personaName))
                backend.SetUserName(playerId, personaName);

            Plugin.Log.LogInfo("Local backend is serving " + playerId + " as the local player.");

            // Do not consume the request against the temporary offline identity. Doing that would
            // raise a placeholder record and leave the real Steam account untouched when Steam
            // finishes initialising on a later frame.
            if (steamId != 0UL)
                TryApplyLevel50Restore(playerId);
        }

        /// <summary>
        /// Applies the opt-in level restore to the authoritative local record before the game asks
        /// the backend for progression. Level 50 begins at the level-49 completion threshold: the
        /// game's progression code increments the visible level after crossing each entry.
        /// </summary>
        private static void TryApplyLevel50Restore(string playerId)
        {
            if (Plugin.SetLevelTo50OnNextLaunch == null
                || !Plugin.SetLevelTo50OnNextLaunch.Value)
            {
                return;
            }

            var backend = _backend;
            var track = backend?.Catalogue?.RewardTrack;
            if (backend == null || track == null)
                return;

            long threshold = -1;
            for (var i = 0; i < track.Count; i++)
            {
                if (track[i].Level != 49) continue;
                threshold = track[i].CumulativeXp;
                break;
            }

            if (threshold < 0)
            {
                if (!_level50WarningLogged)
                {
                    _level50WarningLogged = true;
                    Plugin.Log.LogWarning("Level 50 restore is still armed, but the installed "
                                         + "progression catalogue has no level-49 XP threshold. "
                                         + "The setting was left enabled so a corrected install "
                                         + "can try again next launch.");
                }
                return;
            }

            var currentLevel = 1;
            long currentXp = 0;
            var currentGlobalLevel = 1;
            backend.TryGetLocalProgression(
                out currentLevel, out currentXp, out currentGlobalLevel);

            _level50Xp = Math.Max(currentXp, threshold);
            _level50FloorThisSession = true;

            if (currentLevel < 50 || currentGlobalLevel < 50)
                backend.MirrorProgression(playerId, _level50Xp,
                    Math.Max(currentLevel, 50), Math.Max(currentGlobalLevel, 50));

            Plugin.SetLevelTo50OnNextLaunch.Value = false;
            try
            {
                Plugin.Instance.Config.Save();
            }
            catch (Exception ex)
            {
                // The progression itself is already durable. Report the config failure so the
                // player knows the harmless one-shot may run again on their next launch.
                Plugin.Log.LogWarning("Level 50 was restored, but the one-shot setting could not "
                                     + "be reset on disk: " + ex.Message);
            }

            Plugin.Log.LogInfo(currentLevel < 50 || currentGlobalLevel < 50
                ? "Progression restore complete: the local Steam account is now level 50 ("
                  + _level50Xp + " XP). The one-shot setting has been reset to false."
                : "Progression restore found the local Steam account already at level 50 or "
                  + "higher. No progress was lowered; the one-shot setting has been reset to false.");
        }

        /// <summary>
        /// Makes the game's own PlayerProgression file agree with a restore and blocks a stale
        /// startup value from being mirrored back over the authoritative record.
        /// </summary>
        internal static void EnforceProgressionFloor(
            string playerId, ref PlayerProgressionService.ProgressionData progressionData)
        {
            if (!_level50FloorThisSession
                || (progressionData.Level.SeasonLevel >= 50
                    && progressionData.Level.GlobalLevel >= 50))
                return;

            var backend = _backend;
            if (backend == null
                || !string.Equals(backend.LocalPlayerId, playerId, StringComparison.Ordinal))
            {
                return;
            }

            progressionData.SeasonXp = (int)Math.Min(int.MaxValue,
                Math.Max(progressionData.SeasonXp, _level50Xp));
            progressionData.Level = new PlayerProgressionService.ProgressionLevel(
                Math.Max(progressionData.Level.SeasonLevel, 50),
                Math.Max(progressionData.Level.GlobalLevel, 50));
        }

        internal static LocalResponse Handle(string verb, string path, string body)
        {
            var backend = _backend;
            if (backend == null) return LocalResponse.Failed("LOCAL_BACKEND_UNAVAILABLE");
            return backend.Handle(verb, path, body);
        }

        /// <summary>Called from the progression-persist patch. Silently ignored when the local
        /// backend is not the one answering, so the patch does not have to know the mode.</summary>
        internal static void MirrorProgression(string playerId, long seasonXp, int seasonLevel,
                                               int globalLevel)
        {
            _backend?.MirrorProgression(playerId, seasonXp, seasonLevel, globalLevel);
        }
    }
}
