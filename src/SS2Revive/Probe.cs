using System;
using System.Text;
using Data;
using HarmonyLib;
using Services;
using Steamworks;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// On-demand snapshot of the live networking stack. This is how we confirm what the
    /// decompiled source implies - what the party cap actually is at runtime, whether the
    /// network service is reachable, and whether local auth took.
    /// </summary>
    internal sealed class Probe : MonoBehaviour
    {
        private void Update()
        {
            if (Input.GetKeyDown(Plugin.ProbeKey.Value))
                Dump();

            if (Input.GetKeyDown(Plugin.InviteKey.Value))
                Invite();
        }

        /// <summary>
        /// Opens Steam's own invite dialog for the party lobby. The in-game friends panel can only
        /// offer people it knows about, and it learned that from Bossa's friend service - so this
        /// is the path that does not depend on anything shut down. Requires the overlay, which
        /// means the game must have been launched by Steam.
        /// </summary>
        private static void Invite()
        {
            if (!PartyBackend.InLobby)
            {
                Plugin.Log.LogWarning("No party lobby yet - create a party first, then press "
                                      + Plugin.InviteKey.Value + ".");
                return;
            }

            if (!SteamUtils.IsOverlayEnabled())
            {
                Plugin.Log.LogWarning("Steam overlay is not available - the game was almost certainly "
                                      + "not launched by Steam. Invites and joins cannot be delivered.");
                return;
            }

            // Connect-string invite, not the lobby invite dialog. They look identical to the sender
            // and arrive at different callbacks: the lobby dialog raises GameLobbyJoinRequested_t,
            // and SteamPlatform only ever registered GameRichPresenceJoinRequested_t. This is the
            // one Bossa's own code is listening for.
            var connect = PartyBackend.ConnectString;
            Plugin.Log.LogInfo("Opening Steam invite dialog with connect string '" + connect + "'");
            SteamFriends.ActivateGameOverlayInviteDialogConnectString(connect);
        }

        private void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("==================== SS2Revive probe ====================");

            Line(sb, "Steam running", () => SteamIdentity.IsSteamReady().ToString());

            // The single best indicator of whether Steam launched us. The overlay is injected by
            // the Steam client into processes it started; without it, Steam will not deliver join
            // or invite callbacks to this process at all - which looks exactly like the invite
            // silently doing nothing.
            Line(sb, "Overlay enabled", () => SteamUtils.IsOverlayEnabled()
                ? "true"
                : "FALSE - not launched by Steam; invites cannot arrive");

            Line(sb, "Rich presence connect", () =>
            {
                var connect = SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect");
                return string.IsNullOrEmpty(connect) ? "<empty - not joinable>" : connect;
            });
            Line(sb, "Steam id", () => SteamIdentity.GetSteamId64().ToString());
            Line(sb, "Persona", SteamIdentity.GetPersonaName);
            Line(sb, "Local PlayerId", () => SteamIdentity.GetLocalPlayerId().ToString());
            Line(sb, "MAXIMUM_PLAYERS_IN_PARTY", () => Data.Constants.MAXIMUM_PLAYERS_IN_PARTY.ToString());

            Line(sb, "Party lobby", () => PartyBackend.InLobby
                ? PartyBackend.Lobby.m_SteamID + " (party id "
                  + PartyBackend.EncodeLobby(PartyBackend.Lobby.m_SteamID) + ")"
                : "<none - create a party>");
            Line(sb, "Lobby members", () => PartyBackend.InLobby && SteamIdentity.IsSteamReady()
                ? SteamMatchmaking.GetNumLobbyMembers(PartyBackend.Lobby) + " / 4"
                : "0 / 4");
            Line(sb, "Lobby role", () => !PartyBackend.InLobby
                ? "<none>"
                : PartyBackend.IsOwner ? "owner (leader)" : "member (joined)");

            Line(sb, "Backend", () => Plugin.Backend.Value + (BackendClient.Available
                ? " - " + (LocalBackendHost.Available
                    ? LocalBackendHost.Backend.Describe()
                    : BackendClient.BaseUrl)
                : " - nothing answering; endpoints fail fast"));

            // Whether a party member's level came through tells us which half of the peer exchange
            // is broken when the party screen shows nothing: an empty own level means the mirror
            // never fired, an empty peer level means Steam has not delivered their data yet.
            Line(sb, "Own season level", () => LocalBackendHost.Available
                    && LocalBackendHost.Backend.TryGetLocalLevel(out var level, out var xp)
                ? level + " (" + xp + " xp)"
                : "<not recorded yet>");

            Line(sb, "Steam transport", () => !Plugin.SteamTransport.Value
                ? "disabled"
                : SteamTransport.Ready
                    ? "attached, " + SteamTransport.KnownPeers + " known peer(s), "
                      + SteamTransport.QueuedSends + " queued send(s), "
                      + SteamTransport.QueuedReceives + " queued receive(s)"
                    : "NOT attached - UdpClientManager.Initialise has not run");

            var shell = Shell.Instance;
            if (shell == null)
            {
                sb.AppendLine("  Shell.Instance is null - too early, or startup aborted.");
                sb.AppendLine("=========================================================");
                Plugin.Log.LogInfo(sb.ToString());
                return;
            }

            Line(sb, "Authenticated", () =>
            {
                var auth = Call(shell, "GetAuthenticationService");
                if (auth == null) return "<no service>";
                return AccessTools.Property(auth.GetType(), "IsAuthenticated")
                    .GetValue(auth, null).ToString();
            });

            Line(sb, "ValidGameVersion", () =>
            {
                var svc = Call(shell, "GetVersionControlAndMaintenanceModeService");
                if (svc == null) return "<no service>";
                return AccessTools.Property(svc.GetType(), "ValidGameVersion")
                    .GetValue(svc, null).ToString();
            });

            var network = Call(shell, "GetNetworkService") as INetworkService;
            if (network == null)
            {
                sb.AppendLine("  NetworkService: <null>");
            }
            else
            {
                Line(sb, "NetworkState", () => network.GetNetworkState().ToString());
                Line(sb, "IsHosting", () => network.IsHosting().ToString());
                Line(sb, "IsJoined", () => network.IsJoined().ToString());
                Line(sb, "HasAnyRemotePeers", () => network.HasAnyRemotePeers().ToString());
                Line(sb, "LocalPeerId", () => network.GetLocalPeerId().ToString());
                Line(sb, "HostPeerId", () => network.GetHostPeerId().ToString());
            }

            sb.AppendLine("=========================================================");
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void Line(StringBuilder sb, string label, Func<string> read)
        {
            string value;
            try
            {
                value = read() ?? "<null>";
            }
            catch (Exception ex)
            {
                value = "<" + ex.GetType().Name + ": " + ex.Message + ">";
            }

            sb.Append("  ").Append(label.PadRight(26)).Append(": ").AppendLine(value);
        }

        private static object Call(object instance, string methodName)
        {
            var m = AccessTools.Method(instance.GetType(), methodName);
            return m?.Invoke(instance, null);
        }
    }
}
