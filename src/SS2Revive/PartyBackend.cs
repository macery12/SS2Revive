using System;
using System.Collections.Generic;
using System.Text;
using Data;
using Services;
using Steamworks;
using Guid = Bossa.Framework.Utils.Guid;

namespace SS2Revive
{
    /// <summary>
    /// Stands in for Bossa's party service, backed by a Steam lobby.
    ///
    /// The important thing here is what it does *not* do: it does not touch PartyService. That
    /// class is a seven-state machine that drives the whole party UI - the 4 member slots, the
    /// leader crown, the lock toggle, invite buttons - and it is entirely intact. It only ever
    /// stalled because every transition waits on an HTTP response from a host that stopped
    /// answering.
    ///
    /// So we answer instead. PartyApi.SerializeAndSendHttpRequest is the single point where the
    /// state machine reaches for the network; we intercept it, do the equivalent work against a
    /// Steam lobby, and hand back the same JSON their server would have. Every state transition,
    /// every UI update and the existing Steam invite path then run unmodified.
    ///
    /// The party id doubles as the lobby handle: a Steam lobby's CSteamID is packed into the first
    /// eight bytes of the Guid, with a marker in the rest. That matters because SteamPlatform
    /// already sends party ids as the connect string of a Steam invite and parses them back on the
    /// receiving end - so encoding the lobby there means an accepted Steam invite lands the joiner
    /// in the right lobby using only code Bossa already wrote.
    /// </summary>
    internal static class PartyBackend
    {
        private const int MaxMembers = 4;

        /// <summary>
        /// The state machine does not treat "refresh failed" as "no party" - it re-reads the body
        /// and only advances toward creating one when the error is exactly this. An empty 404 body
        /// parses as Unknown, which makes it retry every 10 seconds forever, silently. This string
        /// is the entire difference between a party being created and nothing ever happening.
        /// </summary>
        private const string NotInPartyJson = "{\"error\":\"PLAYER_NOT_IN_PARTY\"}";

        // Marks a party id as one of ours, so a stale id from a save file or an old invite is
        // rejected rather than decoded into a garbage lobby handle.
        private static readonly byte[] Marker = { 0x53, 0x53, 0x32, 0x52, 0x45, 0x56, 0x49, 0x56 };

        private static int _nextRequestId = 0x53530000;

        private static CSteamID _lobby;
        private static bool _locked;

        /// <summary>
        /// The lobby we intend to end up in, latched the moment the game decides to join one and
        /// cleared only when we have arrived or failed. While this is set, nothing auto-creates a
        /// lobby.
        ///
        /// It exists because joining is not one step. PartyService.CreateOrJoinParty switches to
        /// LeavingParty first, which sends RemoveMember(self) - so the joiner passes through a
        /// window where they are deliberately in no lobby but very much not looking for a new one.
        /// Without the latch, EnsureLobby reads that window as "no party" and starts building one,
        /// and a SteamAPICall already in flight cannot be cancelled.
        /// </summary>
        private static ulong _joinTarget;

        private static Callback<LobbyChatUpdate_t> _lobbyChatUpdate;
        private static Callback<LobbyDataUpdate_t> _lobbyDataUpdate;
        private static Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
        private static CallResult<LobbyCreated_t> _lobbyCreated;
        private static CallResult<LobbyEnter_t> _lobbyEnter;

        /// <summary>Requests parked until a Steam lobby call comes back.</summary>
        private static readonly Dictionary<string, Pending> Waiting = new Dictionary<string, Pending>();

        private struct Pending
        {
            public int RequestId;
            public PlayerId PlayerId;
        }

        internal static bool InLobby => _lobby != CSteamID.Nil && _lobby.IsValid();

        internal static CSteamID Lobby => _lobby;

        /// <summary>
        /// True when this player owns the lobby. Only the owner may write lobby data, so anything
        /// that changes shared party state has to check first - Steam ignores the call for everyone
        /// else, and we would otherwise believe a change that never happened.
        /// </summary>
        internal static bool IsOwner =>
            InLobby && SteamMatchmaking.GetLobbyOwner(_lobby) == SteamUser.GetSteamID();

        /// <summary>
        /// Latched as early as we can see the intent - from PartyService.CreateOrJoinParty, which
        /// runs before the state machine has even begun leaving the current party.
        /// </summary>
        internal static void NoteJoinIntent(Guid partyId)
        {
            if (!TryDecodeLobby(partyId, out var lobbyId))
                return;

            if (InLobby && _lobby.m_SteamID == lobbyId)
                return;

            _joinTarget = lobbyId;
            Plugin.Log.LogInfo("Join intent: lobby " + lobbyId + "; auto-create suspended.");
        }

        internal static void Initialise()
        {
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _lobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);

            // Steam has two separate "join my game" mechanisms and delivers them to two different
            // callbacks. SteamPlatform registers only GameRichPresenceJoinRequested_t, so a Steam
            // *lobby* invite lands on a callback nobody is listening to and vanishes without a
            // trace - no error, no log, nothing. Listening for it ourselves means either kind of
            // invite gets the player into the party.
            _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);

            Plugin.Log.LogInfo("Party backend ready (Steam lobby).");
        }

        /// <summary>Connect string Steam should hand a joiner - the party id, as Bossa encodes it.</summary>
        internal static string ConnectString =>
            InLobby ? EncodeLobby(_lobby.m_SteamID).ToString() : string.Empty;

        private static void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            Plugin.Log.LogInfo("Steam lobby join requested: lobby "
                               + request.m_steamIDLobby.m_SteamID
                               + " from " + request.m_steamIDFriend.m_SteamID);

            // Route it through the game's own entry point rather than joining the lobby directly,
            // so the party state machine performs the leave/join it expects to perform.
            JoinByPartyId(EncodeLobby(request.m_steamIDLobby.m_SteamID));
        }

        private static void JoinByPartyId(Guid partyId)
        {
            var shell = Shell.Instance;
            var party = shell?.GetPartyService();
            if (party == null)
            {
                Plugin.Log.LogError("Cannot join: party service not available yet.");
                return;
            }

            PlayerId playerId;
            try
            {
                playerId = shell.GetNetworkService().GetLocalPlayer(0).playerId;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("No local network player (" + ex.Message
                                      + "); using the Steam identity instead.");
                playerId = SteamIdentity.GetLocalPlayerId();
            }

            party.CreateOrJoinParty(playerId, partyId);
        }

        // ------------------------------------------------------------ request entry point

        /// <summary>
        /// Returns the request id the state machine will match the response against, or 0 to let
        /// the original HTTP call proceed.
        /// </summary>
        internal static int Handle(PartyApi.HttpRequest request, PlayerId playerId)
        {
            var requestId = System.Threading.Interlocked.Increment(ref _nextRequestId);

            // Logged unconditionally. Party traffic is a handful of requests a minute, and when
            // the state machine stalls the only way to see it is the request it keeps repeating.
            Plugin.Log.LogInfo("party <- " + request.type);

            try
            {
                Dispatch(requestId, request, playerId);
            }
            catch (Exception ex)
            {
                // The state machine is waiting on this id and has no timeout of its own, so an
                // unanswered request is a party that never advances again. 503 is the shape it
                // reads as "transient": it retries rather than concluding anything.
                Plugin.Log.LogError("Party request " + request.type + " threw: " + ex);
                Respond(requestId, 503, string.Empty);
            }

            return requestId;
        }

        private static void Dispatch(int requestId, PartyApi.HttpRequest request, PlayerId playerId)
        {
            // Every branch below reaches SteamMatchmaking, and every one of those throws once the
            // Steam API is down. That happens on the way out: SteamManager is destroyed before
            // Shell, and Shell.OnApplicationQuit then calls PartyService.Shutdown, which sends one
            // last RemoveMember through us. InLobby is no help - it is a cached id that stays true
            // after Steam has gone. So the readiness of Steam, not the presence of a lobby, is what
            // gates the rest of this method.
            if (!SteamIdentity.IsSteamReady())
            {
                // 503 with an empty body is the transient-failure shape: the state machine retries
                // rather than concluding anything, so a Steam client that comes back is recovered
                // from. On the quit path there is no next frame, so this is simply dropped.
                ForgetLobby();
                Plugin.Log.LogInfo("Steam is not available; failing party request " + request.type + ".");
                Respond(requestId, 503, string.Empty);
                return;
            }

            // Not for a join: that request carries its own destination, and the latch may not be
            // set yet if the join arrived by some path we do not patch.
            if (request.type != PartyApi.HttpRequestType.CreateOrJoinPartyByPartyId)
                EnsureLobby();

            switch (request.type)
            {
                case PartyApi.HttpRequestType.CreateParty:
                    CreateLobby(requestId, playerId);
                    break;

                case PartyApi.HttpRequestType.CreateOrJoinPartyByPartyId:
                    JoinLobby(requestId, playerId, request.partyId);
                    break;

                case PartyApi.HttpRequestType.RefreshParty:
                    if (InLobby)
                        RespondWithParty(requestId, playerId);
                    else
                        Respond(requestId, 404, NotInPartyJson);
                    break;

                case PartyApi.HttpRequestType.LockParty:
                case PartyApi.HttpRequestType.UnlockParty:
                    // Applied locally by everyone, published to the lobby only by the owner.
                    //
                    // Answering a member's LockParty with the *old* state looks harmless and is not.
                    // LobbyService locks the party the moment all players are ready, then waits for
                    // an OnPartyUpdated that reports isLocked before it starts the countdown UI. A
                    // member who is told "still unlocked" keeps waiting, and only learns the truth
                    // from the 16-second RefreshParty poll - by which point the countdown has already
                    // run and finished on network messages, and StartCountDown dereferences the
                    // countdown deadline it just cleared. The result was a stale exception long after
                    // the level had started, and no 3-2-1 for anyone who was not the leader.
                    //
                    // Bossa's server applied the lock for whoever asked and returned the new state, so
                    // answering locally is what the caller has always been written against. The owner
                    // remains the authority: OnLobbyDataUpdate corrects us if we disagree.
                    _locked = request.type == PartyApi.HttpRequestType.LockParty;
                    if (IsOwner)
                        PublishLobbyState();
                    RespondWithParty(requestId, playerId);
                    break;

                case PartyApi.HttpRequestType.UpdatePartyConfig:
                case PartyApi.HttpRequestType.ChangePartyLeader:
                    RespondWithParty(requestId, playerId);
                    break;

                case PartyApi.HttpRequestType.RemoveMember:
                    // Removing yourself is how State_LeavingParty leaves - and it is the first thing
                    // that happens when accepting an invite, before the join is even attempted. It
                    // has to genuinely leave the Steam lobby, or the joiner arrives at their friend's
                    // party while still sitting in their own.
                    //
                    // Removing anyone else is advisory: Steam has no kick, only self-departure. Say
                    // yes and let the member list correct us if they are still there.
                    if (request.otherPlayerId == playerId)
                        LeaveLobby();

                    RespondWithParty(requestId, playerId);
                    break;

                case PartyApi.HttpRequestType.InvitePlayer:
                    // The actual invite is sent by SteamPlatform.InvitePlayer through Steam. The
                    // server-side invitation record has no local equivalent, so acknowledge it.
                    Respond(requestId, 200, InvitationJson(playerId, request.otherPlayerId));
                    break;

                case PartyApi.HttpRequestType.RefreshSentPartyInvitations:
                case PartyApi.HttpRequestType.RefreshReceivedPartyInvitations:
                    Respond(requestId, 200, "[]");
                    break;

                case PartyApi.HttpRequestType.CancelPartyInvitation:
                case PartyApi.HttpRequestType.AcceptPartyInvitation:
                case PartyApi.HttpRequestType.RejectPartyInvitation:
                    Respond(requestId, 200, string.Empty);
                    break;

                default:
                    Plugin.Log.LogWarning("Unhandled party request: " + request.type);
                    Respond(requestId, 404, string.Empty);
                    break;
            }
        }

        // ------------------------------------------------------------------ steam lobby

        /// <summary>
        /// Keeps a lobby alive for as long as the game is running, without waiting for anything in
        /// the UI to ask for one.
        ///
        /// The party UI cannot be the trigger: nothing in it calls a "create party" method,
        /// because no such method exists. Parties were only ever created by the state machine
        /// walking itself from Initialisation to CreatingParty, which is a fragile thing to depend
        /// on when we are also the one answering it. Owning the lobby ourselves means a party is
        /// always available to hand back, and to invite people into.
        ///
        /// Called on every party request, which the game polls on its own - so this doubles as the
        /// heartbeat that re-creates the lobby if it is ever lost.
        /// </summary>
        private static void EnsureLobby()
        {
            if (InLobby || Waiting.ContainsKey("create") || Waiting.ContainsKey("enter"))
                return;

            // Somebody else's lobby is the destination; making our own would race it.
            if (_joinTarget != 0UL)
                return;

            if (!SteamIdentity.IsSteamReady())
                return;

            Plugin.Log.LogInfo("No lobby; creating one.");
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxMembers);
            Waiting["create"] = new Pending { RequestId = 0, PlayerId = SteamIdentity.GetLocalPlayerId() };
            _lobbyCreated.Set(call);
        }

        private static void CreateLobby(int requestId, PlayerId playerId)
        {
            if (InLobby)
            {
                // Already hosting; the state machine just wants a party back.
                RespondWithParty(requestId, playerId);
                return;
            }

            // EnsureLobby has almost always started one already. Attach this request to it rather
            // than issuing a second CreateLobby and orphaning the first.
            if (Waiting.TryGetValue("create", out var inFlight))
            {
                Waiting["create"] = new Pending { RequestId = requestId, PlayerId = playerId };
                Plugin.Log.LogInfo("Lobby creation already in flight; attaching request "
                                   + requestId + (inFlight.RequestId == 0 ? " (was automatic)." : "."));
                return;
            }

            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxMembers);
            Waiting["create"] = new Pending { RequestId = requestId, PlayerId = playerId };
            _lobbyCreated.Set(call);
            Plugin.Log.LogInfo("Creating Steam lobby for party...");
        }

        private static void JoinLobby(int requestId, PlayerId playerId, Guid partyId)
        {
            if (!TryDecodeLobby(partyId, out var lobbyId))
            {
                Plugin.Log.LogWarning("Party id is not an SS2Revive lobby handle; cannot join.");
                _joinTarget = 0UL;
                Respond(requestId, 404, string.Empty);
                return;
            }

            _joinTarget = lobbyId;

            if (InLobby && _lobby.m_SteamID == lobbyId)
            {
                _joinTarget = 0UL;
                RespondWithParty(requestId, playerId);
                return;
            }

            // Steam allows membership of several lobbies at once; the party model does not. Leave
            // first so we never hold two, which would make GetLobbyOwner and the member list
            // disagree with whichever one the game thinks it is in.
            if (InLobby)
                LeaveLobby();

            var call = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            Waiting["enter"] = new Pending { RequestId = requestId, PlayerId = playerId };
            _lobbyEnter.Set(call);
            Plugin.Log.LogInfo("Joining Steam lobby " + lobbyId + "...");
        }

        internal static void LeaveLobby()
        {
            if (!InLobby)
                return;

            Plugin.Log.LogInfo("Leaving Steam lobby " + _lobby.m_SteamID);
            SteamMatchmaking.LeaveLobby(_lobby);
            ForgetLobby();
        }

        /// <summary>
        /// Drops every piece of state that was only true while we were in that lobby.
        ///
        /// The transport reset is the part that matters. Its peer table is what decides whether a
        /// synthetic endpoint gets translated into a Steam message, and it is only ever added to -
        /// so without this, everyone from every party this session stays addressable for the rest
        /// of it. Packets aimed at someone who left would keep going out over Steam instead of
        /// falling through to the original UDP path, and the queues would still be holding their
        /// traffic.
        /// </summary>
        private static void ForgetLobby()
        {
            _lobby = CSteamID.Nil;
            _locked = false;
            SteamTransport.Reset();
            SteamVoiceChat.NotifyLobbyChanged();
        }

        private static void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (!Waiting.TryGetValue("create", out var pending))
                return;
            Waiting.Remove("create");

            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogError("Lobby creation failed: "
                                    + (ioFailure ? "IO failure" : result.m_eResult.ToString()));
                if (pending.RequestId != 0)
                    Respond(pending.RequestId, 503, string.Empty);
                return;
            }

            var created = new CSteamID(result.m_ulSteamIDLobby);

            // A CreateLobby call cannot be recalled once issued, so the decision to keep it has to
            // be made here, on arrival. If we have since decided to join someone - or already have -
            // this lobby is unwanted: adopting it would silently strand the join, leaving the player
            // alone in a party of one while believing they joined a friend.
            if (_joinTarget != 0UL || InLobby)
            {
                Plugin.Log.LogInfo("Discarding lobby " + created.m_SteamID
                                   + "; " + (InLobby ? "already in one." : "joining another."));
                SteamMatchmaking.LeaveLobby(created);
                if (pending.RequestId != 0)
                    RespondWithParty(pending.RequestId, pending.PlayerId);
                return;
            }

            _lobby = created;
            _locked = false;
            SteamVoiceChat.NotifyLobbyChanged();
            PublishLobbyState();

            Plugin.Log.LogInfo("Lobby created: " + _lobby.m_SteamID
                               + " (party id " + EncodeLobby(_lobby.m_SteamID) + ")");

            // A request id of 0 means we created this lobby on our own initiative, so there is
            // nothing waiting on an answer - the next poll will pick the party up.
            if (pending.RequestId != 0)
                RespondWithParty(pending.RequestId, pending.PlayerId);
        }

        private static void OnLobbyEnter(LobbyEnter_t result, bool ioFailure)
        {
            if (!Waiting.TryGetValue("enter", out var pending))
                return;
            Waiting.Remove("enter");

            // Cleared either way: on success we have arrived, and on failure we must stop suppressing
            // auto-create or the player is left with no party at all and no way to get one.
            _joinTarget = 0UL;

            if (ioFailure || result.m_EChatRoomEnterResponse
                != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogError("Could not enter lobby: " + result.m_EChatRoomEnterResponse
                                    + " (full, locked, or gone). Falling back to our own party.");
                Respond(pending.RequestId, 404, string.Empty);
                return;
            }

            _lobby = new CSteamID(result.m_ulSteamIDLobby);
            _locked = SteamMatchmaking.GetLobbyData(_lobby, "locked") == "1";
            SteamVoiceChat.NotifyLobbyChanged();
            RegisterLobbyPeers();
            Plugin.Log.LogInfo("Entered lobby " + _lobby.m_SteamID
                               + " with " + MemberCount() + " member(s); owner is "
                               + (IsOwner ? "us" : "someone else") + ".");
            RespondWithParty(pending.RequestId, pending.PlayerId);
        }

        private static void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (!InLobby || update.m_ulSteamIDLobby != _lobby.m_SteamID)
                return;

            const uint gone = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                                     | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                     | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                                     | EChatMemberStateChange.k_EChatMemberStateChangeBanned);

            // If we are the one who left, our cached lobby id is now a handle to a room we are not
            // in - every read through it would be wrong rather than merely stale. Drop it and let
            // EnsureLobby give us a party of our own again on the next poll.
            if (update.m_ulSteamIDUserChanged == SteamUser.GetSteamID().m_SteamID
                && (update.m_rgfChatMemberStateChange & gone) != 0)
            {
                Plugin.Log.LogInfo("We left lobby " + _lobby.m_SteamID
                                   + " (" + (EChatMemberStateChange)update.m_rgfChatMemberStateChange + ").");
                ForgetLobby();
                return;
            }

            RegisterLobbyPeers();
            SteamVoiceChat.NotifyLobbyMembershipChanged();
            Plugin.Log.LogInfo("Lobby membership changed; now " + MemberCount() + " member(s), owner "
                               + (IsOwner ? "us" : "someone else") + ".");
        }

        private static void OnLobbyDataUpdate(LobbyDataUpdate_t update)
        {
            if (!InLobby || update.m_ulSteamIDLobby != _lobby.m_SteamID)
                return;

            var locked = SteamMatchmaking.GetLobbyData(_lobby, "locked");
            if (!string.IsNullOrEmpty(locked))
                _locked = locked == "1";
        }

        private static void PublishLobbyState()
        {
            if (!InLobby)
                return;

            SteamMatchmaking.SetLobbyData(_lobby, "game", "SurgeonSimulator2");
            SteamMatchmaking.SetLobbyData(_lobby, "locked", _locked ? "1" : "0");
        }

        private static int MemberCount()
        {
            return InLobby ? SteamMatchmaking.GetNumLobbyMembers(_lobby) : 0;
        }

        /// <summary>
        /// Asks Steam, not our cache. Session requests can arrive before we have built a party
        /// containing the sender, and refusing one is not recoverable - the peer does not ask
        /// twice, it just times out.
        /// </summary>
        internal static bool IsLobbyMember(ulong steamId64)
        {
            if (!InLobby || !SteamIdentity.IsSteamReady())
                return false;

            try
            {
                var count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
                for (var i = 0; i < count; i++)
                {
                    if (SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID == steamId64)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Lobby membership check failed: " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Returns a main-thread snapshot of the authenticated Steam lobby roster. Voice and text
        /// use this as their complete authorization boundary; packet payloads never get to claim
        /// an identity of their own.
        /// </summary>
        internal static List<ulong> GetLobbyMembers()
        {
            var members = new List<ulong>(MaxMembers);
            if (!InLobby || !SteamIdentity.IsSteamReady())
                return members;

            try
            {
                var count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
                for (var i = 0; i < count && i < MaxMembers; i++)
                {
                    var id = SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID;
                    if (id != 0UL)
                        members.Add(id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Reading lobby roster failed: " + ex.Message);
            }

            return members;
        }

        /// <summary>
        /// Makes every current lobby member addressable immediately, rather than whenever the party
        /// JSON next happens to be built. The two are usually seconds apart, and a peer that starts
        /// sending inside that gap is refused and never retries.
        /// </summary>
        private static void RegisterLobbyPeers()
        {
            if (!InLobby)
                return;

            try
            {
                var count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
                for (var i = 0; i < count; i++)
                    SteamTransport.RegisterPeer(SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Registering lobby peers failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------- responses

        private static void RespondWithParty(int requestId, PlayerId playerId)
        {
            Respond(requestId, 200, PartyJson(playerId));
        }

        /// <summary>
        /// Deferred by a frame for the same reason authentication is - the state machine calls
        /// this from inside its own transition, and answering synchronously would re-enter it.
        /// </summary>
        private static void Respond(int requestId, int statusCode, string json)
        {
            Plugin.Log.LogInfo("party -> " + statusCode
                               + (json.Length > 0 ? " " + json : string.Empty));

            Dispatcher.NextFrame(() =>
            {
                var complete = PatchSet.GetPartyCompletionDelegate();
                if (complete == null)
                {
                    Plugin.Log.LogError("No party completion delegate; response dropped.");
                    return;
                }

                // UTF-16, not ASCII. ManualJsonDeserializer's NativeByteBuffer overload reads two
                // bytes per character (Count / 2), so an ASCII body is parsed at half length with
                // every character wrong - it does not error, it just quietly yields nothing.
                var buffer = new NativeByteBuffer(Math.Max(json.Length * 2, 2));
                try
                {
                    buffer.WriteUnicode(json);
                    complete(requestId, statusCode == 200, statusCode, buffer);
                }
                finally
                {
                    buffer.Dispose();
                }
            });
        }

        // ------------------------------------------------------------------------ json

        /// <summary>
        /// The shape PartyApi.ReadPartyFromJson requires. All five keys must be present or it
        /// silently discards the party, which is a very quiet way to fail - so they are all
        /// written unconditionally.
        /// </summary>
        private static string PartyJson(PlayerId selfPlayerId)
        {
            var sb = new StringBuilder(256);
            var partyId = InLobby ? EncodeLobby(_lobby.m_SteamID) : new Guid(System.Guid.NewGuid());

            sb.Append("{\"id\":\"").Append(partyId.ToString());
            sb.Append("\",\"leaderPlayerId\":\"").Append(LeaderPlayerId(selfPlayerId));
            sb.Append("\",\"maxNumPlayers\":").Append(MaxMembers);
            sb.Append(",\"locked\":").Append(_locked ? "true" : "false");
            sb.Append(",\"members\":[");

            var members = OrderedMembers();
            if (members.Count == 0)
            {
                AppendMember(sb, selfPlayerId.ToString(), SteamIdentity.GetSteamId64());
            }
            else
            {
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendMember(sb, SteamIdentity.BuildPlayerIdString(members[i]), members[i]);
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Owner first, then everyone else by ascending Steam id.
        ///
        /// Steam does not promise a stable order from GetLobbyMemberByIndex, and PartyService decides
        /// whether the party changed by comparing the whole struct - member array included, in order.
        /// An order that shuffles between two otherwise identical polls therefore reads as a party
        /// update and fires OnPartyUpdated, which is not a harmless notification: LobbyService starts
        /// the map countdown from it. Sorting makes "unchanged" actually compare as unchanged.
        ///
        /// The owner is pinned to the front rather than sorted with the rest, because that is where
        /// Steam happens to put them today and the group's player indices are derived from this order
        /// on both machines.
        /// </summary>
        private static List<ulong> OrderedMembers()
        {
            var members = new List<ulong>(MaxMembers);
            if (!InLobby)
                return members;

            var count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            for (var i = 0; i < count; i++)
                members.Add(SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID);

            var owner = SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID;
            members.Sort((a, b) =>
            {
                if (a == owner) return b == owner ? 0 : -1;
                if (b == owner) return 1;
                return a.CompareTo(b);
            });

            return members;
        }

        /// <summary>
        /// Where Bossa's server used to publish the endpoint it had discovered for a player, we
        /// publish the synthetic one <see cref="SteamTransport"/> will translate back into a Steam
        /// account. The game treats it as an ordinary address and routes to it as usual.
        ///
        /// Both an external address and at least one entry in <c>clientJson.localEndPoints</c> are
        /// required: NetworkPeerManager rejects the whole group configuration - loudly, then does
        /// nothing - for any member reporting zero local endpoints. The same synthetic endpoint
        /// serves as both, since there is only ever one route to a Steam peer.
        /// </summary>
        private static void AppendMember(StringBuilder sb, string playerId, ulong steamId64)
        {
            var address = SteamTransport.RegisterPeer(steamId64);
            var port = SteamTransport.Port;

            sb.Append("{\"id\":\"").Append(playerId).Append("\",\"connectionDetails\":{");
            sb.Append("\"remoteAddress\":\"").Append(FormatAddress(address)).Append('"');
            sb.Append(",\"remotePort\":").Append(port);

            // localEndPoints is "<packedAddress>:<port>," repeated - and it travels as a JSON
            // *string*, so the object it describes is escaped inside this one.
            sb.Append(",\"clientJson\":\"{\\\"localEndPoints\\\":\\\"")
              .Append(address).Append(':').Append(port).Append(',')
              .Append("\\\"}\"");

            sb.Append("}}");
        }

        /// <summary>
        /// Dotted quad in the byte order the game's own parser expects: least significant byte
        /// first, matching EndPointHelper.ConvertIpAddressFromString and IPAddress.Address.
        /// </summary>
        private static string FormatAddress(uint address)
        {
            return (address & 0xFF) + "."
                   + ((address >> 8) & 0xFF) + "."
                   + ((address >> 16) & 0xFF) + "."
                   + ((address >> 24) & 0xFF);
        }

        private static string LeaderPlayerId(PlayerId selfPlayerId)
        {
            if (!InLobby)
                return selfPlayerId.ToString();

            var owner = SteamMatchmaking.GetLobbyOwner(_lobby);
            return owner.IsValid()
                ? SteamIdentity.BuildPlayerIdString(owner.m_SteamID)
                : selfPlayerId.ToString();
        }

        private static string InvitationJson(PlayerId inviter, PlayerId invitee)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"id\":\"").Append(new Guid(System.Guid.NewGuid()).ToString());
            sb.Append("\",\"inviterPlayerId\":\"").Append(inviter);
            sb.Append("\",\"inviteePlayerId\":\"").Append(invitee);
            sb.Append("\",\"invitationStatus\":\"PENDING\"}");
            return sb.ToString();
        }

        // ------------------------------------------------------- party id <-> lobby id

        internal static Guid EncodeLobby(ulong lobbyId)
        {
            var guid = default(Guid);
            guid.byte_0 = (byte)lobbyId;
            guid.byte_1 = (byte)(lobbyId >> 8);
            guid.byte_2 = (byte)(lobbyId >> 16);
            guid.byte_3 = (byte)(lobbyId >> 24);
            guid.byte_4 = (byte)(lobbyId >> 32);
            guid.byte_5 = (byte)(lobbyId >> 40);
            guid.byte_6 = (byte)(lobbyId >> 48);
            guid.byte_7 = (byte)(lobbyId >> 56);
            guid.byte_8 = Marker[0];
            guid.byte_9 = Marker[1];
            guid.byte_a = Marker[2];
            guid.byte_b = Marker[3];
            guid.byte_c = Marker[4];
            guid.byte_d = Marker[5];
            guid.byte_e = Marker[6];
            guid.byte_f = Marker[7];
            return guid;
        }

        internal static bool TryDecodeLobby(Guid partyId, out ulong lobbyId)
        {
            lobbyId = 0;

            if (partyId.byte_8 != Marker[0] || partyId.byte_9 != Marker[1]
                || partyId.byte_a != Marker[2] || partyId.byte_b != Marker[3]
                || partyId.byte_c != Marker[4] || partyId.byte_d != Marker[5]
                || partyId.byte_e != Marker[6] || partyId.byte_f != Marker[7])
                return false;

            lobbyId = partyId.byte_0
                      | ((ulong)partyId.byte_1 << 8)
                      | ((ulong)partyId.byte_2 << 16)
                      | ((ulong)partyId.byte_3 << 24)
                      | ((ulong)partyId.byte_4 << 32)
                      | ((ulong)partyId.byte_5 << 40)
                      | ((ulong)partyId.byte_6 << 48)
                      | ((ulong)partyId.byte_7 << 56);
            return lobbyId != 0;
        }
    }
}
