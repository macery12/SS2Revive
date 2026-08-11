using System;
using System.Collections;
using System.Text;
using Data;
using HarmonyLib;
using Patient;
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
                ? " - " + LocalBackendHost.Backend.Describe()
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

            Line(sb, "Steam voice/chat", () => !Plugin.SteamVoiceChat.Value
                ? "disabled"
                : !SteamVoiceChat.Active
                    ? "waiting for the game's voice provider"
                    : "channel="
                      + (string.IsNullOrEmpty(SteamVoiceChat.CurrentChannel)
                          ? "<none>"
                          : SteamVoiceChat.CurrentChannel)
                      + ", capture=" + (SteamVoiceChat.Capturing ? "recording" : "stopped")
                      + ", remoteSpeakers=" + SteamVoiceChat.RemoteSpeakerCount
                      + ", droppedVoice=" + SteamVoiceChat.DroppedVoicePackets
                      + ", rejectedVoice=" + SteamVoiceChat.RejectedVoicePackets
                      + ", rejectedText=" + SteamVoiceChat.RejectedTextMessages);

            var shell = Shell.Instance;
            if (shell == null)
            {
                sb.AppendLine("  Shell.Instance is null - too early, or startup aborted.");
                sb.AppendLine("=========================================================");
                Plugin.Log.LogInfo(sb.ToString());
                return;
            }

            // Everything above is identity, party and transport - the answers to "why did my
            // invite do nothing", which is most of what a bug report needs. Below is live session
            // and patient state, which is long, only means anything mid-level, and is worth being
            // able to turn off for a player who is reading their own log rather than filing one.
            if (!Plugin.VerboseProbe.Value)
            {
                sb.AppendLine("  (session and patient state omitted; set Diagnostics.Verbose = true"
                              + " in the config to include it)");
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

            DumpPatients(sb, shell);

            sb.AppendLine("=========================================================");
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Bob's blood, and the three things that have to agree before it can move.
        ///
        /// Blood is not simulated per frame - it is a snapshot (level, per-source rates, and the
        /// clock reading they were taken at) that every machine extrapolates locally. So a blood
        /// bar that will not drop is one of four distinct faults, and only a dump from both
        /// players at once tells them apart:
        ///
        ///   no approved state    -> the patient never replicated at all
        ///   all rates zero       -> nobody's cuts are reaching the host
        ///   rates set, level flat-> the clock is not advancing
        ///   level sequence differs between the two dumps -> every patient message is being
        ///                           discarded by the mismatch guard on both sides, which is
        ///                           silent by design and looks exactly like nothing happening
        /// </summary>
        private static void DumpPatients(StringBuilder sb, Shell shell)
        {
            var level = shell.GetLevelService();
            var patients = shell.GetPatientModel();
            var service = shell.GetPatientService();

            Line(sb, "Level state", () => level.GetCurrentState() + ", mode " + level.GetCurrentLevelMode());
            Line(sb, "Loading level", () => level.IsLoadingLevel().ToString());

            // Must be identical on host and client. If it is not, the patient message guards
            // drop every action and every state update without a word.
            Line(sb, "Level sequence number", () => level.GetLevelSequenceNumber().ToString());
            Line(sb, "Clock sequence number", () =>
            {
                var clock = shell.GetSequenceClockService().GetSequenceNumberClock();
                return clock.GetSequenceNumber() + " @ " + clock.GetSequenceNumbersPerSecond() + "/s";
            });
            Line(sb, "Logic enabled", () => shell.GetLogicService().IsLogicEnabled().ToString());

            if (patients == null || service == null)
            {
                sb.AppendLine("  PatientService/PatientModel: <null>");
                return;
            }

            var approved = ApprovedPatientStates(service);
            var list = patients.PatientList;
            if (list == null || list.Count == 0)
            {
                sb.AppendLine("  Patients                  : <none in level>");
                return;
            }

            foreach (var id in list)
            {
                var patientId = id;
                Line(sb, "Bob " + patientId, () => DescribePatient(service, approved, patientId));
            }
        }

        private static string DescribePatient(
            PatientService service, IDictionary approved, int patientId)
        {
            // GetPatientRemainingBlood answers MaxBloodLevel for a patient it has never heard of,
            // which on screen is indistinguishable from a healthy Bob. Say which one it is.
            if (approved != null && !approved.Contains(patientId))
                return "NO APPROVED STATE - never replicated to this machine";

            var blood = service.GetPatientRemainingBlood(patientId);
            var rate = service.GetPatientTotalBloodLossRate(patientId);

            var sources = new StringBuilder();
            for (var i = 0; i < (int)PatientBloodLossRateSource.Count; i++)
            {
                var source = (PatientBloodLossRateSource)i;
                var perSource = service.GetPatientBloodLossRateOnSource(patientId, source);
                if (perSource <= 0f) continue;
                if (sources.Length > 0) sources.Append(", ");
                sources.Append(source).Append(' ').Append(perSource.ToString("0.#"));
            }

            return blood.ToString("0") + "ml, losing " + rate.ToString("0.#") + "ml/s"
                   + (sources.Length > 0 ? " from " + sources : " from nothing - no cut has registered");
        }

        private static IDictionary ApprovedPatientStates(PatientService service)
        {
            try
            {
                return AccessTools.Field(typeof(PatientService), "_approvedPatientStatesById")
                    ?.GetValue(service) as IDictionary;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read approved patient states: " + ex.Message);
                return null;
            }
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
