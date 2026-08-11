using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Where the game's dead HTTP calls get answered.
    ///
    /// <see cref="Local"/> is the default and needs nothing running. Bossa's services were
    /// per-player almost throughout - daily challenges are seeded from the player id and the date,
    /// campaign progress mirrors a local save, and the cosmetics catalogue ships inside the game -
    /// so there is very little a shared server was actually required for.
    ///
    /// There is deliberately no remote option. An earlier build could forward these calls to a
    /// self-hosted replacement server, and nothing was ever gained by it: every endpoint is
    /// per-player, so the hosted answer was identical to the local one, and a second machine in
    /// the path only added a port to secure, a process to keep running, and a class of failure
    /// (unreachable host, wrong shared token, expired certificate) that Local cannot have.
    /// </summary>
    internal enum BackendMode
    {
        Off,
        Local,
    }

    /// <summary>
    /// Entry point. Stage 1 only: get the game to a live, backend-free state where the
    /// networking stack is reachable. No transport work happens here yet.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Surgeon Simulator 2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.ss2revive.core";
        public const string PluginName = "SS2 Revive";

        /// <summary>Generated from SS2ReviveVersion in Directory.Build.props at build time, so
        /// this and the assembly version cannot disagree. See SS2Revive.csproj.</summary>
        public const string PluginVersion = GeneratedVersion.Value;

        /// <summary>
        /// The build every patch in here was derived from, and the last one that can work.
        ///
        /// 1.5.0 is the offline patch: it removed the netcode this mod restores, so there is
        /// nothing left for the party, transport or backend patches to attach to. Saying so at
        /// load is worth a great deal, because the failure otherwise looks like a mod that
        /// silently does nothing - the log fills with patch failures that mean nothing to a
        /// player, and the game itself runs fine.
        /// </summary>
        private const string TestedGameVersion = "1.3.7";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        internal static ConfigEntry<bool> BypassAuth;
        internal static ConfigEntry<bool> BypassVersionGate;
        internal static ConfigEntry<bool> BypassConnectionCheck;
        internal static ConfigEntry<bool> DisableVoip;
        internal static ConfigEntry<bool> StubDeadBackends;
        internal static ConfigEntry<bool> LocalParty;
        internal static ConfigEntry<bool> HttpFailFast;
        internal static ConfigEntry<bool> SkipMatchmaking;
        internal static ConfigEntry<bool> HardenLevelReader;
        internal static ConfigEntry<bool> CreationMode;
        internal static ConfigEntry<bool> LevelSharingEnabled;
        internal static ConfigEntry<string> CommunityCatalogUrl;
        internal static ConfigEntry<bool> FreeForAll;
        internal static ConfigEntry<bool> FreeForAllIncludeGameLevels;
        internal static ConfigEntry<BackendMode> Backend;
        internal static ConfigEntry<bool> GrantAllCosmetics;
        internal static ConfigEntry<string> SaveDirectory;
        internal static ConfigEntry<bool> NewsFeedEnabled;
        internal static ConfigEntry<string> NewsFeedUrl;
        internal static ConfigEntry<bool> SteamTransport;
        internal static ConfigEntry<KeyCode> InviteKey;
        internal static ConfigEntry<bool> ShareLevel;
        internal static ConfigEntry<bool> VerboseProbe;
        internal static ConfigEntry<KeyCode> ProbeKey;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            BypassAuth = Config.Bind("Bypass", "Auth", true,
                "Skip the dead /auth/authenticate call and authenticate locally using the Steam identity.");
            BypassVersionGate = Config.Bind("Bypass", "VersionAndMaintenance", true,
                "Treat the build as current and not in maintenance. The backing service is gone.");
            BypassConnectionCheck = Config.Bind("Bypass", "ConnectionCheck", true,
                "Stop asking a dead host for permission to start. Build 1.3.7 repointed the boot "
                + "connectivity check from example.com at Bossa's own ss2.bsprd.uk, which no longer "
                + "resolves, and the check gates the creation of the entire game shell. Turning "
                + "this off on 1.3.7 or later leaves the game stuck on the 'requires an active "
                + "internet connection' box, with nothing else in this plugin ever reached.");
            DisableVoip = Config.Bind("Bypass", "Voip", true,
                "Stop Vivox from initialising or logging in. Its backend is gone.");
            StubDeadBackends = Config.Bind("Bypass", "DeadBackendCalls", true,
                "No-op calls that can only reach servers Bossa has shut down (telemetry registration).");
            LocalParty = Config.Bind("Party", "SteamLobbyBackend", true,
                "Serve the party system from a Steam lobby instead of Bossa's party server.");
            HttpFailFast = Config.Bind("Bypass", "HttpFailFast", true,
                "Fail Bossa HTTP calls immediately instead of waiting out a DNS timeout each time.");
            SkipMatchmaking = Config.Bind("Bypass", "Matchmaking", true,
                "Start levels with whoever is already in the party instead of holding the vactube "
                + "screen open for strangers. Bossa's matchmaking server is gone, so the wait can "
                + "only ever time out.");
            HardenLevelReader = Config.Bind("Security", "HardenLevelReader", true,
                "Put bounds on the level file reader. The format lets a file declare its own voxel "
                + "dimensions and its own decompressed size with nothing checking either, so a "
                + "level built to do so can ask for an allocation no machine can satisfy. That "
                + "matters in a party, where the host's level is sent to everyone: one bad level "
                + "would take out the whole lobby rather than one player. Custom/shared maps must "
                + "use current format 29; older maps are accepted only when their SHA-256 exactly "
                + "matches a level in this installation's bundled catalogue. Leave this on.");
            CreationMode = Config.Bind("CreationMode", "Enabled", true,
                "Keep the level editor working by saving levels to this machine instead of Bossa's "
                + "UGC service. Without it, loading into Creation Mode hangs on a black screen: the "
                + "game uploads a new level before it will open it, and that upload can no longer "
                + "complete or fail. Levels go to the SS2Revive folder beside your other saves, one "
                + "folder each. Saved levels can be published to the restored community browser "
                + "after Steam authentication.");
            LevelSharingEnabled = Config.Bind("CreationMode", "LevelSharing", true,
                "Turn the terminal's Share button into an Export button, and add an Import button "
                + "to the Create screen. Export writes the level to one .ss2level file and copies "
                + "the game's own 22-character share code to the clipboard; import reads any "
                + ".ss2level file left in the import folder. Both folders sit beside your saves, in "
                + "the SS2Revive folder. Send the file however you like and post the code with it - "
                + "the terminal's search box has always accepted a code, so once somebody has "
                + "imported the file, the code finds the level on their machine too. Manually "
                + "imported levels are published only in the local library and stay credited to "
                + "whoever built them, so they can be played and browsed but not edited.");
            // This intentionally uses a new key. Older builds wrote an empty CommunityCatalogUrl
            // into existing config files; reusing it would silently keep the new public service
            // disabled for every upgrading player.
            CommunityCatalogUrl = Config.Bind("CommunityMaps", "ApiCatalogUrl",
                "https://community.m12labs.net/v1/catalog",
                "HTTPS URL of the public SS2Revive community-map catalogue. Published maps are "
                + "merged into Discover without requiring a login. Bundles and thumbnails are "
                + "checksum-verified, cached, and installed locally only when opened. Leave this "
                + "empty only when a completely local library is desired.");
            FreeForAll = Config.Bind("FreeForAll", "Enabled", true,
                "Draw the Free-for-all queue from the levels on this machine. Bossa served that "
                + "queue from a curated slice of what the community had published, so without this "
                + "the queue comes back empty and picking the mode drops you straight back into the "
                + "lobby the moment the vactube finishes.");
            FreeForAllIncludeGameLevels = Config.Bind("FreeForAll", "IncludeGameLevels", true,
                "Let Free-for-all fall back on the levels that ship with the game. No free-for-all "
                + "level ships with Surgeon Simulator 2, so these are the campaign levels, played "
                + "with Quick Play scoring rather than campaign grading. On by default because a "
                + "new install has no levels of its own and the mode would otherwise still be "
                + "empty. Turn it off once you have built or been sent some.");
            Backend = Config.Bind("Backend", "Mode", BackendMode.Local,
                "Where Bossa's dead HTTP calls are answered.\n"
                + "Local - inside this DLL. No server, no port, nothing to maintain. Saves go to "
                + "%LOCALAPPDATA%\\Bossa Studios\\Surgeon Simulator 2\\SS2Revive.\n"
                + "Off   - every call fails immediately, as it did before any backend existed. "
                + "Progression, challenges and cosmetics stop working; this is a diagnostic "
                + "setting, not a supported way to play.");
            GrantAllCosmetics = Config.Bind("Backend", "GrantAllCosmetics", true,
                "Report every catalogued item as owned. On by default because a partial inventory "
                + "is not a smaller answer, it is a destructive one: the client deletes any unlock "
                + "the backend omits. Turn this off to earn cosmetics along the season track "
                + "instead - safe in Local mode, where the recorded XP is copied from the client's "
                + "own save and cannot drift.");
            SaveDirectory = Config.Bind("Backend", "SaveDirectory", "",
                "Where Local mode keeps progress. Empty means next to the game's own saves, in "
                + "%LOCALAPPDATA%\\Bossa Studios\\Surgeon Simulator 2\\SS2Revive. Do not point "
                + "this inside the game folder or BepInEx - a Steam file verification or a mod "
                + "update would delete it.");

            NewsFeedEnabled = Config.Bind("NewsFeed", "Enabled", true,
                "Fill the three blank panels on the main menu. Bossa's feed host is gone, so "
                + "without this they stay white.");
            NewsFeedUrl = Config.Bind("NewsFeed", "Url", "",
                "Where to fetch NewsFeed.json and images/ from. Leave empty to read them out of "
                + "BepInEx/plugins/SS2Revive/newsfeed/, which needs no server. Set an https:// URL "
                + "with a trailing slash to serve the feed to several machines from one place.");

            SteamTransport = Config.Bind("Party", "SteamP2PTransport", true,
                "Carry peer-to-peer game traffic over Steam. Bossa's STUN and TURN servers are gone, "
                + "so direct UDP cannot get through NAT without them.");
            InviteKey = Config.Bind("Party", "InviteKey", KeyCode.F10,
                "Press to open the Steam overlay invite dialog for the current party lobby.");
            ShareLevel = Config.Bind("Party", "ShareLevelOverSteam", true,
                "Publish your season level through Steam so party members and friends see the real "
                + "number instead of nothing. This is the only thing left that genuinely crosses "
                + "between players - everything else is per-player and answered locally.");
            VerboseProbe = Config.Bind("Diagnostics", "Verbose", true,
                "Include live session and patient state in the probe dump, on top of the identity, "
                + "party and transport summary that is always printed. Leave this on when you are "
                + "going to attach the log to a bug report.");
            ProbeKey = Config.Bind("Diagnostics", "ProbeKey", KeyCode.F9,
                "Press to dump live networking state to the log.");

            Log.LogInfo($"{PluginName} {PluginVersion} starting.");
            Log.LogInfo($"Unity {Application.unityVersion} | product '{Application.productName}' | version '{Application.version}'");

            WarnAboutGameVersion(Application.version);

            _harmony = new Harmony(PluginGuid);

            // Must exist before any patch can defer work onto it.
            Dispatcher.Install(gameObject);

            try
            {
                PatchSet.ApplyAll(_harmony);
            }
            catch (Exception ex)
            {
                // A failed patch must never take the game down - we want the log.
                Log.LogError("Patch application threw: " + ex);
            }

            PatchSet.LogReport();
            gameObject.AddComponent<Probe>();
            Log.LogInfo("Awake complete. Press " + ProbeKey.Value + " in game to dump network state.");
        }

        /// <summary>
        /// Says out loud which builds this can and cannot work on, before the patch report makes
        /// it look like a hundred small unrelated failures.
        ///
        /// The comparison is on major.minor only. Bossa shipped several 1.3.x point builds and the
        /// patches hold across them; what matters is the 1.5 boundary, where the netcode this
        /// restores was taken out of the game entirely.
        /// </summary>
        private static void WarnAboutGameVersion(string gameVersion)
        {
            if (!TryReadMajorMinor(gameVersion, out var major, out var minor))
            {
                Log.LogWarning($"Could not read the game version from '{gameVersion}'. This mod was "
                               + $"built against {TestedGameVersion}; if things do not work, that "
                               + "mismatch is the first thing to check.");
                return;
            }

            if (!TryReadMajorMinor(TestedGameVersion, out var testedMajor, out var testedMinor))
                return;

            if (major == testedMajor && minor == testedMinor)
                return;

            if (major > testedMajor || (major == testedMajor && minor >= 5))
            {
                Log.LogError("=====================================================================");
                Log.LogError($"This game is version {gameVersion}. SS2 Revive cannot work on it.");
                Log.LogError("Build 1.5.0 was the offline patch, and it removed the netcode this "
                             + "mod exists to restore - there is nothing left for the party, "
                             + "transport and backend patches to attach to.");
                Log.LogError($"Install build {TestedGameVersion} instead. SS2Revive-Setup.exe "
                             + "from the latest release downloads it from Steam for an account "
                             + "that owns the game, then installs BepInEx and this mod.");
                Log.LogError("Everything below this line is a consequence of that, not a separate "
                             + "problem.");
                Log.LogError("=====================================================================");
                return;
            }

            Log.LogWarning($"This game is version {gameVersion}, and SS2 Revive was built against "
                           + $"{TestedGameVersion}. It may work; if patches below report FAIL, the "
                           + "build is the likely reason.");
        }

        private static bool TryReadMajorMinor(string version, out int major, out int minor)
        {
            major = 0;
            minor = 0;

            if (string.IsNullOrEmpty(version))
                return false;

            var parts = version.Split('.');
            return parts.Length >= 2
                   && int.TryParse(parts[0], out major)
                   && int.TryParse(parts[1], out minor);
        }

        private void OnDestroy()
        {
            // Before unpatching: the transport's delivery thread reaches into the game through a
            // captured MethodInfo, and letting it keep doing that while patches are being pulled
            // out is the one ordering here that could bite.
            // Qualified, because this class has a config field of the same name.
            SS2Revive.SteamTransport.Shutdown();
            _harmony?.UnpatchSelf();
        }
    }
}
