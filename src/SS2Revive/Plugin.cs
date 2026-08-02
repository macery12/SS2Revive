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
    /// </summary>
    internal enum BackendMode
    {
        Off,
        Local,
        Remote,
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
        public const string PluginVersion = "0.2.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        internal static ConfigEntry<bool> BypassAuth;
        internal static ConfigEntry<bool> BypassVersionGate;
        internal static ConfigEntry<bool> DisableVoip;
        internal static ConfigEntry<bool> StubDeadBackends;
        internal static ConfigEntry<bool> LocalParty;
        internal static ConfigEntry<bool> HttpFailFast;
        internal static ConfigEntry<bool> SkipMatchmaking;
        internal static ConfigEntry<bool> CreationMode;
        internal static ConfigEntry<BackendMode> Backend;
        internal static ConfigEntry<bool> GrantAllCosmetics;
        internal static ConfigEntry<string> SaveDirectory;
        internal static ConfigEntry<string> BackendBaseUrl;
        internal static ConfigEntry<string> BackendSharedToken;
        internal static ConfigEntry<bool> BackendAcceptAnyCertificate;
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
            CreationMode = Config.Bind("CreationMode", "Enabled", true,
                "Keep the level editor working by saving levels to this machine instead of Bossa's "
                + "UGC service. Without it, loading into Creation Mode hangs on a black screen: the "
                + "game uploads a new level before it will open it, and that upload can no longer "
                + "complete or fail. Levels go to the SS2Revive folder beside your other saves, one "
                + "folder each. Publishing works, but only you can see the result - there is no "
                + "shared level browser left to publish to.");
            Backend = Config.Bind("Backend", "Mode", BackendMode.Local,
                "Where Bossa's dead HTTP calls are answered.\n"
                + "Local  - inside this DLL. No server, no port, nothing to maintain. Saves go to "
                + "%LOCALAPPDATA%\\Bossa Studios\\Surgeon Simulator 2\\SS2Revive.\n"
                + "Remote - forwarded to a self-hosted backend at BaseUrl, probed once at startup.\n"
                + "Off    - every call fails immediately, as it did before any backend existed.");
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
            BackendBaseUrl = Config.Bind("Backend", "BaseUrl", "http://127.0.0.1:8050",
                "Remote mode only. Scheme, host and port of the replacement backend. Plain http is "
                + "fine on loopback or a LAN address; use https only when something in front of it "
                + "terminates TLS.");
            BackendSharedToken = Config.Bind("Backend", "SharedToken", "ss2revive-local",
                "Sent in the game's 'Security' header on every backend call. Must match "
                + "SHARED_TOKEN on the server. The default is only safe for a backend bound to "
                + "127.0.0.1 - anyone who can reach the port and knows this value can read and "
                + "write any player's records.");
            BackendAcceptAnyCertificate = Config.Bind("Backend", "AcceptAnyCertificate", false,
                "Remote mode only. Accept an untrusted TLS certificate, but only from the host in "
                + "BaseUrl. Needed for a self-signed certificate; unnecessary with a real one.");

            NewsFeedEnabled = Config.Bind("NewsFeed", "Enabled", true,
                "Fill the three blank panels on the main menu. Bossa's feed host is gone, so "
                + "without this they stay white.");
            NewsFeedUrl = Config.Bind("NewsFeed", "Url", "",
                "Where to fetch NewsFeed.json and images/ from. Leave empty to read them out of "
                + "BepInEx/plugins/SS2Revive/newsfeed/, which needs no server. Set an http(s) URL "
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
                "Log network/session state on the probe key.");
            ProbeKey = Config.Bind("Diagnostics", "ProbeKey", KeyCode.F9,
                "Press to dump live networking state to the log.");

            Log.LogInfo($"{PluginName} {PluginVersion} starting.");
            Log.LogInfo($"Unity {Application.unityVersion} | product '{Application.productName}' | version '{Application.version}'");

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

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
