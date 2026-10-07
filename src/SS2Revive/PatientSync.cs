using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Services;
using Services.Network;
using Utilities;

namespace SS2Revive
{
    /// <summary>
    /// Keeps Bob's state flowing between host and clients once surgery gets busy.
    ///
    /// Bob is simulated on the host only. Clients send their cuts, organ swaps and limb changes to
    /// the host as <c>PatientActions</c>; the host answers with <c>PatientUpdates</c>, the replicated
    /// state every client's blood monitor, loss-rate readout and diagnostic scanner are computed
    /// from. Each message also carries the acknowledgements for the other direction, and neither
    /// side drops anything from its resend list until the other has acknowledged it.
    ///
    /// Neither message is ever split. <c>NetworkSender.SendMessage</c> refuses anything over 1200
    /// bytes - it logs "it should be no larger than 1200" and returns an error both send loops
    /// ignore - so a message that outgrows the limit is never sent, its contents are never
    /// acknowledged, and the list it was built from can only grow. That direction then stays dead
    /// for the rest of the level:
    ///
    ///   client to host  every unacknowledged action is resent each time, at about 53 bytes each,
    ///                   so roughly 22 pending actions lock the client out. Its own screen keeps
    ///                   predicting; the host and everyone else stop seeing anything it does, which
    ///                   is "the organs are replaced, but it shows otherwise for the rest".
    ///   host to client  Bob's state is about 180 bytes plus 29 per open cut, and every detached
    ///                   limb adds its own. A cut-up Bob with loose limbs, or two Bobs, passes the
    ///                   limit, and every client freezes on the last state it received: blood that
    ///                   stops falling until Bob dies.
    ///
    /// Latency is what tips it over. Acks ride back on the host's next update, which only goes out
    /// every 500 ms, so the longer the round trip the more actions are pending at once - and a Steam
    /// relay is slower than the servers this protocol was tuned against.
    ///
    /// The same latency causes the second fault. A client resends an action whenever its ack has
    /// not come back within 500 ms, and <c>ProcessPatientActions</c> applies whatever it receives.
    /// A late ack therefore meant the host applied that cut twice - the instant damage again and a
    /// second increase to the bleed rate - on a Bob whose clients were predicting it once.
    ///
    /// So both send loops split what they send into messages the sender will accept and only treat
    /// acks as delivered when the send succeeded, and the host applies each client action once,
    /// acknowledging repeats without replaying them.
    /// </summary>
    internal static class PatientSync
    {
        /// <summary><c>NetworkSender.SendMessage</c> rejects <c>length &gt; 1200</c>.</summary>
        private const int MaxMessageBytes = 1200;

        /// <summary><c>PatientService.PATIENT_UPDATE_TIME</c>, in clock sequence numbers.</summary>
        private const int SendInterval = 500;

        private static readonly List<PatientService.PatientAction> NoActions =
            new List<PatientService.PatientAction>();

        private static bool _resolved;

        private static FieldInfo _perPeerData;
        private static FieldInfo _patientData;
        private static FieldInfo _sentActions;
        private static FieldInfo _newActions;
        private static FieldInfo _sendingStates;
        private static FieldInfo _sendingLimbs;
        private static FieldInfo _stateAcks;
        private static FieldInfo _limbAcks;
        private static FieldInfo _lastSent;
        private static FieldInfo _approvedStates;
        private static FieldInfo _approvedLimbs;
        private static FieldInfo _serializer;
        private static FieldInfo _network;
        private static FieldInfo _level;
        private static FieldInfo _clock;

        /// <summary>One side's view of a peer's <c>PerPeerData.patientData</c>. The lists are the
        /// game's own instances, so changes to them land in the game's state directly.</summary>
        private struct PeerLists
        {
            internal List<PatientService.PatientAction> SentActions;
            internal List<PatientService.PatientAction> NewActions;
            internal List<PatientService.MessageData> SendingStates;
            internal List<PatientService.MessageData> SendingLimbs;
            internal List<int> StateAcks;
            internal List<int> LimbAcks;
            internal int LastSent;
        }

        /// <summary>
        /// Action ids the host has already applied for one peer. Ids come from a counter in the
        /// client's own process, so the history belongs to whoever holds the peer slot and is
        /// dropped when they leave, are replaced, or the level changes.
        /// </summary>
        private sealed class PeerHistory
        {
            internal string Owner;
            internal readonly HashSet<int> Applied = new HashSet<int>();
            internal int Repeats;
        }

        private static readonly Dictionary<int, PeerHistory> Histories = new Dictionary<int, PeerHistory>();

        // Per-level diagnostics, summarised and reset when the next level starts.
        private static int _splitSends;
        private static int _largestMessage;
        private static bool _splitLogged;
        private static bool _sendFailureLogged;
        private static bool _oversizedLogged;
        private static bool _staleLogged;
        private static bool _senderFailureLogged;

        [ThreadStatic] private static PatientService _readingActionsFor;
        [ThreadStatic] private static int _readingActionsFromPeer;

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("PatientService -> locate per-peer patient sync state", Resolve);
            if (!_resolved) return;

            PatchSet.Try("PatientService send loops -> split patient sync to fit the 1200-byte limit", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(PatientService), "SendMessagesAsHost"),
                    prefix: Hook(nameof(SendMessagesAsHost_Prefix)));
                harmony.Patch(PatchSet.Method(typeof(PatientService), "SendMessagesAsClient"),
                    prefix: Hook(nameof(SendMessagesAsClient_Prefix)));
                harmony.Patch(PatchSet.Method(typeof(PatientService), "LevelStateAboutToChange"),
                    postfix: Hook(nameof(LevelStateAboutToChange_Postfix)));
            });

            PatchSet.Try("PatientService.ProcessPatientActions -> apply each client action once", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(PatientService), "ProcessPatientActions"),
                    prefix: Hook(nameof(ProcessPatientActions_Prefix)),
                    finalizer: Hook(nameof(ProcessPatientActions_Finalizer)));
                harmony.Patch(PatchSet.Method(typeof(MessageSerializer), "ReadPatientActions"),
                    postfix: Hook(nameof(ReadPatientActions_Postfix)));
                harmony.Patch(PatchSet.Method(typeof(PatientService), "OnNetworkTopologyChanged"),
                    postfix: Hook(nameof(TopologyChanged_Postfix)));
            });
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(AccessTools.Method(typeof(PatientSync), name));

        private static void Resolve()
        {
            var service = typeof(PatientService);
            _perPeerData = Field(service, "_perPeerData");
            _patientData = Field(_perPeerData.FieldType.GetElementType(), "patientData");

            var patientData = _patientData.FieldType;
            _sentActions = Field(patientData, "sentPatientActions");
            _newActions = Field(patientData, "newPatientActionsToSend");
            _sendingStates = Field(patientData, "sendingOverallPatientState");
            _sendingLimbs = Field(patientData, "sendingLimbHealthState");
            _stateAcks = Field(patientData, "acknowledgedMsgs");
            _limbAcks = Field(patientData, "acknowledgedLimbsMsgs");
            _lastSent = Field(patientData, "lastSentSequenceNumber");

            _approvedStates = Field(service, "_approvedPatientStatesById");
            _approvedLimbs = Field(service, "_approvedLimbHealthStatesById");
            _serializer = Field(service, "_messageSerializer");
            _network = Field(service, "_networkService");
            _level = Field(service, "_levelService");
            _clock = Field(service, "_sequenceNumberClock");
            _resolved = true;
        }

        private static FieldInfo Field(Type type, string name)
        {
            var field = type == null ? null : AccessTools.Field(type, name);
            if (field == null)
                throw new MissingFieldException(type?.FullName ?? "<null>", name);
            return field;
        }

        // ------------------------------------------------------------------ per-peer state

        private static PeerLists ReadPeer(PatientService service, int peerId)
        {
            var peers = (Array)_perPeerData.GetValue(service);
            var patientData = _patientData.GetValue(peers.GetValue(peerId));
            return new PeerLists
            {
                SentActions = (List<PatientService.PatientAction>)_sentActions.GetValue(patientData),
                NewActions = (List<PatientService.PatientAction>)_newActions.GetValue(patientData),
                SendingStates = (List<PatientService.MessageData>)_sendingStates.GetValue(patientData),
                SendingLimbs = (List<PatientService.MessageData>)_sendingLimbs.GetValue(patientData),
                StateAcks = (List<int>)_stateAcks.GetValue(patientData),
                LimbAcks = (List<int>)_limbAcks.GetValue(patientData),
                LastSent = (int)_lastSent.GetValue(patientData),
            };
        }

        /// <summary>
        /// Records a send in the game's own field, which is also what makes the original loop skip
        /// this peer. <c>PerPeerData</c> and <c>PatientData</c> are structs held by value in an
        /// array, so the boxed copies have to be written back level by level.
        /// </summary>
        private static void WriteLastSent(PatientService service, int peerId, int sequenceNumber)
        {
            var peers = (Array)_perPeerData.GetValue(service);
            var perPeer = peers.GetValue(peerId);
            var patientData = _patientData.GetValue(perPeer);
            _lastSent.SetValue(patientData, sequenceNumber);
            _patientData.SetValue(perPeer, patientData);
            peers.SetValue(perPeer, peerId);
        }

        // ------------------------------------------------------------------- host to client

        /// <summary>
        /// Sends each peer's pending patient and limb states, splitting them when the game's single
        /// message would be refused. Runs before the original, which then finds the peer already
        /// sent this interval and leaves it alone; anything this throws on falls through to it.
        /// </summary>
        private static void SendMessagesAsHost_Prefix(PatientService __instance)
        {
            try
            {
                var network = (INetworkService)_network.GetValue(__instance);
                var level = (LevelService)_level.GetValue(__instance);
                var serializer = (MessageSerializer)_serializer.GetValue(__instance);
                var states = (Dictionary<int, PatientService.PatientOverallState>)_approvedStates.GetValue(__instance);
                var limbs = (Dictionary<int, PatientService.LimbHealthState>)_approvedLimbs.GetValue(__instance);
                var now = ((SequenceNumberClock)_clock.GetValue(__instance)).GetSequenceNumber();
                var hostPeerId = network.GetHostPeerId();
                var levelSequence = level.GetLevelSequenceNumber();

                var peers = network.GetNetworkPeers();
                for (var i = 0; i < peers.Count; i++)
                {
                    var peerId = peers[i].peerId;
                    if (peerId == hostPeerId) continue;

                    var lists = ReadPeer(__instance, peerId);
                    if (lists.StateAcks.Count + lists.SendingStates.Count + lists.SendingLimbs.Count == 0) continue;
                    if (now < lists.LastSent + SendInterval) continue;

                    DropStaleEntries(lists, states, limbs);
                    SendUpdates(() => SendToPeer(network, serializer, peerId), serializer, levelSequence,
                        states, limbs, lists, "Patient state for peer " + peerId);
                    WriteLastSent(__instance, peerId, now);
                }
            }
            catch (Exception ex)
            {
                if (!_senderFailureLogged)
                {
                    _senderFailureLogged = true;
                    Plugin.Log.LogError("Patient update sender threw; the game's own sender handles it instead: " + ex);
                }
            }
        }

        /// <summary>
        /// <c>WritePatientUpdates</c> looks every pending id up in the approved dictionaries and
        /// throws <c>KeyNotFoundException</c> - which it does not catch - for one that is gone. That
        /// would stop every update to the peer for good, so an entry with nothing behind it is
        /// dropped instead.
        /// </summary>
        private static void DropStaleEntries(PeerLists lists,
                                             Dictionary<int, PatientService.PatientOverallState> states,
                                             Dictionary<int, PatientService.LimbHealthState> limbs)
        {
            var dropped = lists.SendingStates.RemoveAll(entry => !states.ContainsKey(entry.propInstanceId))
                          + lists.SendingLimbs.RemoveAll(entry => !limbs.ContainsKey(entry.propInstanceId));
            if (dropped == 0 || _staleLogged) return;

            _staleLogged = true;
            Plugin.Log.LogWarning("Dropped " + dropped + " pending patient update(s) for props that no "
                                  + "longer have a patient state.");
        }

        /// <summary><paramref name="send"/> transmits whatever the serializer last wrote and says
        /// whether the sender accepted it.</summary>
        private static void SendUpdates(Func<bool> send, MessageSerializer serializer, int levelSequence,
                                        Dictionary<int, PatientService.PatientOverallState> states,
                                        Dictionary<int, PatientService.LimbHealthState> limbs,
                                        PeerLists lists, string what)
        {
            // The usual case, byte for byte what the game would have sent.
            var written = serializer.WritePatientUpdates(levelSequence, ref states, lists.SendingStates,
                ref limbs, lists.SendingLimbs, lists.StateAcks);
            var fullLength = serializer.GetSendMessageLength();
            if (written && fullLength <= MaxMessageBytes)
            {
                if (send()) lists.StateAcks.Clear();
                return;
            }

            var batchStates = new List<PatientService.MessageData>();
            var batchLimbs = new List<PatientService.MessageData>();
            var batchAcks = new List<int>();
            var deliveredAcks = new HashSet<int>();
            var messages = 0;
            var oversized = 0;

            bool Write() =>
                serializer.WritePatientUpdates(levelSequence, ref states, batchStates, ref limbs, batchLimbs, batchAcks)
                && serializer.GetSendMessageLength() <= MaxMessageBytes;

            bool Empty() => batchStates.Count + batchLimbs.Count + batchAcks.Count == 0;

            void Flush()
            {
                if (Empty()) return;
                if (Write() && send())
                {
                    messages++;
                    deliveredAcks.UnionWith(batchAcks);
                }
                batchStates.Clear();
                batchLimbs.Clear();
                batchAcks.Clear();
            }

            bool Add<T>(List<T> batch, T item)
            {
                batch.Add(item);
                if (Write()) return true;
                batch.RemoveAt(batch.Count - 1);
                if (Empty()) return false;

                Flush();
                batch.Add(item);
                if (Write()) return true;
                batch.RemoveAt(batch.Count - 1);
                return false;
            }

            // Acks first: they are what lets the client stop resending, and they are tiny.
            foreach (var ack in lists.StateAcks) Add(batchAcks, ack);
            foreach (var entry in lists.SendingStates)
                if (!Add(batchStates, entry)) oversized++;
            foreach (var entry in lists.SendingLimbs)
                if (!Add(batchLimbs, entry)) oversized++;
            Flush();

            lists.StateAcks.RemoveAll(deliveredAcks.Contains);
            NoteSplit(what, fullLength, messages, oversized);
        }

        private static bool SendToPeer(INetworkService network, MessageSerializer serializer, int peerId) =>
            Sent(network.SendToPeer(peerId, serializer.GetSendMessageBuffer(), serializer.GetSendMessageLength()));

        // ------------------------------------------------------------------- client to host

        /// <summary>
        /// Sends the client's unacknowledged and new patient actions to the host, split the same
        /// way. Batches go out in order in the same frame, so the host still applies them in the
        /// order they happened.
        /// </summary>
        private static void SendMessagesAsClient_Prefix(PatientService __instance)
        {
            try
            {
                var network = (INetworkService)_network.GetValue(__instance);
                var level = (LevelService)_level.GetValue(__instance);
                var serializer = (MessageSerializer)_serializer.GetValue(__instance);
                var now = ((SequenceNumberClock)_clock.GetValue(__instance)).GetSequenceNumber();
                var hostPeerId = network.GetHostPeerId();

                var lists = ReadPeer(__instance, hostPeerId);
                if (lists.StateAcks.Count + lists.SentActions.Count + lists.NewActions.Count
                    + lists.LimbAcks.Count == 0) return;
                if (now <= lists.LastSent + SendInterval) return;

                SendActions(() => SendToHost(network, serializer), serializer, level.GetLevelSequenceNumber(), lists);
                WriteLastSent(__instance, hostPeerId, now);
            }
            catch (Exception ex)
            {
                if (!_senderFailureLogged)
                {
                    _senderFailureLogged = true;
                    Plugin.Log.LogError("Patient action sender threw; the game's own sender handles it instead: " + ex);
                }
            }
        }

        private static void SendActions(Func<bool> send, MessageSerializer serializer, int levelSequence,
                                        PeerLists lists)
        {
            var written = serializer.WritePatientActions(levelSequence, lists.NewActions, lists.SentActions,
                lists.StateAcks, lists.LimbAcks);
            var fullLength = serializer.GetSendMessageLength();
            if (written && fullLength <= MaxMessageBytes)
            {
                if (send())
                {
                    lists.StateAcks.Clear();
                    lists.LimbAcks.Clear();
                }
            }
            else
            {
                var batchActions = new List<PatientService.PatientAction>();
                var batchStateAcks = new List<int>();
                var batchLimbAcks = new List<int>();
                var deliveredStateAcks = new HashSet<int>();
                var deliveredLimbAcks = new HashSet<int>();
                var messages = 0;

                bool Write() =>
                    serializer.WritePatientActions(levelSequence, NoActions, batchActions, batchStateAcks, batchLimbAcks)
                    && serializer.GetSendMessageLength() <= MaxMessageBytes;

                bool Empty() => batchActions.Count + batchStateAcks.Count + batchLimbAcks.Count == 0;

                void Flush()
                {
                    if (Empty()) return;
                    if (Write() && send())
                    {
                        messages++;
                        deliveredStateAcks.UnionWith(batchStateAcks);
                        deliveredLimbAcks.UnionWith(batchLimbAcks);
                    }
                    batchActions.Clear();
                    batchStateAcks.Clear();
                    batchLimbAcks.Clear();
                }

                void Add<T>(List<T> batch, T item)
                {
                    batch.Add(item);
                    if (Write()) return;
                    batch.RemoveAt(batch.Count - 1);
                    Flush();
                    batch.Add(item);
                }

                foreach (var ack in lists.StateAcks) Add(batchStateAcks, ack);
                foreach (var ack in lists.LimbAcks) Add(batchLimbAcks, ack);
                // Oldest first, exactly as WritePatientActions orders them.
                foreach (var action in lists.SentActions) Add(batchActions, action);
                foreach (var action in lists.NewActions) Add(batchActions, action);
                Flush();

                lists.StateAcks.RemoveAll(deliveredStateAcks.Contains);
                lists.LimbAcks.RemoveAll(deliveredLimbAcks.Contains);
                NoteSplit("Patient actions to the host", fullLength, messages, 0);
            }

            // As the game does: whatever was offered now waits for the host's ack, sent or not.
            lists.SentActions.AddRange(lists.NewActions);
            lists.NewActions.Clear();
        }

        private static bool SendToHost(INetworkService network, MessageSerializer serializer) =>
            Sent(network.SendToHost(serializer.GetSendMessageBuffer(), serializer.GetSendMessageLength()));

        private static bool Sent(SendMessageResult result)
        {
            if (result == SendMessageResult.Ok) return true;
            if (!_sendFailureLogged)
            {
                _sendFailureLogged = true;
                Plugin.Log.LogWarning("A patient sync message was not sent (" + result + "); it will be "
                                      + "offered again next interval.");
            }
            return false;
        }

        private static void NoteSplit(string what, int fullLength, int messages, int oversized)
        {
            _splitSends++;
            _largestMessage = Math.Max(_largestMessage, fullLength);

            if (oversized > 0 && !_oversizedLogged)
            {
                _oversizedLogged = true;
                Plugin.Log.LogError(what + ": " + oversized + " entry(s) exceed " + MaxMessageBytes
                                    + " bytes on their own and cannot be sent.");
            }

            if (_splitLogged) return;
            _splitLogged = true;
            Plugin.Log.LogInfo(what + " came to " + fullLength + " bytes, over the " + MaxMessageBytes
                               + " the game will send in one message; sent as " + messages + " instead.");
        }

        // ------------------------------------------------------------- apply actions once

        private static void ProcessPatientActions_Prefix(PatientService __instance, int peerId)
        {
            _readingActionsFor = __instance;
            _readingActionsFromPeer = peerId;
        }

        private static void ProcessPatientActions_Finalizer()
        {
            _readingActionsFor = null;
        }

        /// <summary>
        /// Removes actions this peer has already had applied, before <c>ProcessPatientActions</c>
        /// replays them, and acknowledges them in their place - the client resent them only because
        /// the first ack was late, and it keeps resending until one arrives.
        /// </summary>
        private static void ReadPatientActions_Postfix(bool __result, ref int levelSequenceNumber,
                                                       ref List<PatientService.PatientAction> patientActions)
        {
            var service = _readingActionsFor;
            if (!__result || service == null || patientActions == null || patientActions.Count == 0) return;

            try
            {
                var network = (INetworkService)_network.GetValue(service);
                var level = (LevelService)_level.GetValue(service);

                // Mirror ProcessPatientActions' own early exits: only actions it is about to apply
                // count as applied.
                if (!network.IsHosting() || level.GetCurrentState() == LevelState.Editing
                    || level.GetLevelSequenceNumber() != levelSequenceNumber) return;

                var peerId = _readingActionsFromPeer;
                var history = HistoryFor(network, peerId);
                var acks = ReadPeer(service, peerId).StateAcks;

                var kept = 0;
                var repeats = 0;
                for (var i = 0; i < patientActions.Count; i++)
                {
                    var action = patientActions[i];
                    if (!history.Applied.Add(action.messageId))
                    {
                        acks.Add(action.messageId);
                        repeats++;
                        continue;
                    }
                    patientActions[kept++] = action;
                }

                if (repeats == 0) return;
                patientActions.RemoveRange(kept, patientActions.Count - kept);

                if (history.Repeats == 0)
                    Plugin.Log.LogInfo("Peer " + peerId + " resent patient actions the host had already "
                                       + "applied; acknowledging them without applying them again.");
                history.Repeats += repeats;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not check patient actions for repeats: " + ex.Message);
            }
        }

        private static PeerHistory HistoryFor(INetworkService network, int peerId)
        {
            var owner = OwnerOf(network, peerId);
            if (!Histories.TryGetValue(peerId, out var history) || history.Owner != owner)
            {
                history = new PeerHistory { Owner = owner };
                Histories[peerId] = history;
            }
            return history;
        }

        /// <summary>The player on a peer, as text: PlayerId's own == goes through a native
        /// UnsafeUtility call, and the string form is the same 36 bytes.</summary>
        private static string OwnerOf(INetworkService network, int peerId)
        {
            var members = network.GetValidGroupMembers();
            for (var i = 0; i < members.Count; i++)
                if (members[i].networkPeerId == peerId)
                    return members[i].playerId.ToString();
            return null;
        }

        /// <summary>A peer that left, or whose slot now belongs to someone else, starts over:
        /// their next client process numbers its actions from the beginning again.</summary>
        private static void TopologyChanged_Postfix(PatientService __instance)
        {
            if (Histories.Count == 0) return;

            try
            {
                var network = (INetworkService)_network.GetValue(__instance);
                var present = new HashSet<int>();
                foreach (var peer in network.GetNetworkPeers()) present.Add(peer.peerId);

                var gone = new List<int>();
                foreach (var pair in Histories)
                    if (!present.Contains(pair.Key) || pair.Value.Owner != OwnerOf(network, pair.Key))
                        gone.Add(pair.Key);
                foreach (var peerId in gone) Histories.Remove(peerId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not prune patient action history: " + ex.Message);
                Histories.Clear();
            }
        }

        private static void LevelStateAboutToChange_Postfix()
        {
            if (_splitSends > 0)
                Plugin.Log.LogInfo("Patient sync last level: " + _splitSends + " send(s) had to be split; "
                                   + "the largest would have been " + _largestMessage + " bytes.");
            foreach (var pair in Histories)
                if (pair.Value.Repeats > 0)
                    Plugin.Log.LogInfo("Patient sync last level: ignored " + pair.Value.Repeats
                                       + " repeated action(s) from peer " + pair.Key + ".");

            Histories.Clear();
            _splitSends = 0;
            _largestMessage = 0;
            _splitLogged = false;
            _sendFailureLogged = false;
            _oversizedLogged = false;
            _staleLogged = false;
            _senderFailureLogged = false;
        }

        // ------------------------------------------------------------------------- probe

        /// <summary>One line for the F9 dump: what is still waiting on the other side.</summary>
        internal static string Describe(PatientService service)
        {
            if (!_resolved) return "<patch not active>";

            var network = (INetworkService)_network.GetValue(service);
            var text = new System.Text.StringBuilder();
            if (network.IsHosting())
            {
                var hostPeerId = network.GetHostPeerId();
                foreach (var peer in network.GetNetworkPeers())
                {
                    if (peer.peerId == hostPeerId) continue;
                    var lists = ReadPeer(service, peer.peerId);
                    if (text.Length > 0) text.Append("; ");
                    text.Append("peer ").Append(peer.peerId).Append(": ")
                        .Append(lists.SendingStates.Count).Append(" state(s), ")
                        .Append(lists.SendingLimbs.Count).Append(" limb(s), ")
                        .Append(lists.StateAcks.Count).Append(" ack(s) unconfirmed");
                    if (Histories.TryGetValue(peer.peerId, out var history) && history.Repeats > 0)
                        text.Append(", ").Append(history.Repeats).Append(" repeat(s) ignored");
                }
                if (text.Length == 0) text.Append("hosting, no peers");
            }
            else if (network.GetHostPeerId() < 0)
            {
                text.Append("not in a group");
            }
            else
            {
                var lists = ReadPeer(service, network.GetHostPeerId());
                text.Append(lists.SentActions.Count).Append(" action(s) awaiting ack, ")
                    .Append(lists.NewActions.Count).Append(" queued");
            }

            text.Append(" | split sends this level: ").Append(_splitSends);
            if (_splitSends > 0) text.Append(" (largest ").Append(_largestMessage).Append(" B)");
            return text.ToString();
        }
    }
}
