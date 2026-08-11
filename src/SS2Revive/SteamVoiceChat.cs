using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Audio.Voip;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Redirects the concrete Vivox provider into <see cref="SteamVoiceChat"/> while leaving the
    /// game's provider, services and UI intact. TextChatService explicitly casts the voice
    /// provider back to VivoxPlatform, so replacing the object is substantially more fragile than
    /// turning that object into a compatibility facade.
    /// </summary>
    internal static class SteamVoiceChatPatches
    {
        private static readonly FieldInfo TransmissionMutedField =
            AccessTools.Field(typeof(VOIPProvider), "_voiceTransmissionMuted");

        private static readonly FieldInfo ReceiveMutedField =
            AccessTools.Field(typeof(VOIPProvider), "_voiceReceiveMuted");

        internal static void Apply(Harmony harmony)
        {
            var patches = new List<KeyValuePair<MethodInfo, MethodInfo>>();
            AddPatch(patches, "InitialiseEngine", nameof(InitialiseEngine_Prefix),
                typeof(string), typeof(string));
            AddPatch(patches, "DeinitialiseEngine", nameof(DeinitialiseEngine_Prefix));
            AddPatch(patches, "UpdateEngine", nameof(UpdateEngine_Prefix), typeof(float));
            AddPatch(patches, "RequestJoinChannel", nameof(RequestJoinChannel_Prefix), typeof(string));
            AddPatch(patches, "RequestLeaveActiveChannel", nameof(RequestLeaveActiveChannel_Prefix));
            AddPatch(patches, "HasJoinedChannel", nameof(HasJoinedChannel_Prefix), typeof(string));
            AddPatch(patches, "GetCurrentChannel", nameof(GetCurrentChannel_Prefix));
            AddPatch(patches, "SendMessage", nameof(SendMessage_Prefix), typeof(string));
            AddPatch(patches, "TrySetRemotePlayerMuted", nameof(TrySetRemotePlayerMuted_Prefix),
                typeof(string), typeof(bool));
            AddPatch(patches, "SetEnabled", nameof(SetEnabled_Prefix), typeof(bool));
            AddPatch(patches, "SetMuteTransmission", nameof(SetMuteTransmission_Prefix), typeof(bool));
            AddPatch(patches, "SetMuteReceive", nameof(SetMuteReceive_Prefix), typeof(bool));
            AddPatch(patches, "SetPlaybackVolume", nameof(SetPlaybackVolume_Prefix), typeof(float));
            AddPatch(patches, "SetRecordingVolume", nameof(SetRecordingVolume_Prefix), typeof(float));

            // Defence in depth. InitialiseEngine is skipped, so Login is unreachable through the
            // normal lifecycle, but a later game call must still never expose the retired tenant's
            // client-side signing material or contact Vivox.
            AddPatch(patches, "Login", nameof(Login_Prefix));

            // Validate every inferred signature before applying the first prefix. A partial facade
            // is worse than no facade: one original Vivox method mixed with Steam state could log
            // in, dereference an uninitialised client, or leave the stock services wedged.
            for (var i = 0; i < patches.Count; i++)
                harmony.Patch(patches[i].Key, new HarmonyMethod(patches[i].Value));
        }

        private static void AddPatch(List<KeyValuePair<MethodInfo, MethodInfo>> patches,
            string targetName, string prefixName,
            params Type[] parameterTypes)
        {
            var target = AccessTools.Method(typeof(VivoxPlatform), targetName, parameterTypes);
            var prefix = AccessTools.Method(typeof(SteamVoiceChatPatches), prefixName);
            if (target == null)
                throw new MissingMethodException(typeof(VivoxPlatform).FullName, targetName);
            if (prefix == null)
                throw new MissingMethodException(typeof(SteamVoiceChatPatches).FullName, prefixName);
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, prefix));
        }

        private static bool InitialiseEngine_Prefix(VivoxPlatform __instance, string myUID,
            string playerDisplayName)
        {
            SteamVoiceChat.Initialise(__instance, myUID, playerDisplayName);
            return false;
        }

        private static bool DeinitialiseEngine_Prefix(VivoxPlatform __instance)
        {
            SteamVoiceChat.Deinitialise(__instance);
            return false;
        }

        private static bool UpdateEngine_Prefix(VivoxPlatform __instance, float deltaTime)
        {
            SteamVoiceChat.Update(__instance, deltaTime);
            return false;
        }

        private static bool RequestJoinChannel_Prefix(VivoxPlatform __instance,
            string voiceChannelId)
        {
            SteamVoiceChat.RequestJoinChannel(__instance, voiceChannelId);
            return false;
        }

        private static bool RequestLeaveActiveChannel_Prefix(VivoxPlatform __instance)
        {
            SteamVoiceChat.RequestLeaveChannel(__instance);
            return false;
        }

        private static bool HasJoinedChannel_Prefix(VivoxPlatform __instance, string channelName,
            ref bool __result)
        {
            __result = SteamVoiceChat.HasJoinedChannel(__instance, channelName);
            return false;
        }

        private static bool GetCurrentChannel_Prefix(VivoxPlatform __instance, ref string __result)
        {
            __result = SteamVoiceChat.GetCurrentChannel(__instance);
            return false;
        }

        private static bool SendMessage_Prefix(VivoxPlatform __instance, string message)
        {
            SteamVoiceChat.SendText(__instance, message);
            return false;
        }

        private static bool TrySetRemotePlayerMuted_Prefix(VivoxPlatform __instance,
            string playerId, bool muted)
        {
            SteamVoiceChat.SetRemoteMuted(__instance, playerId, muted);
            return false;
        }

        private static bool SetEnabled_Prefix(VivoxPlatform __instance, bool enabled)
        {
            SteamVoiceChat.SetEnabled(__instance, enabled);
            return false;
        }

        private static bool SetMuteTransmission_Prefix(VivoxPlatform __instance, bool muted)
        {
            TransmissionMutedField?.SetValue(__instance, muted);
            SteamVoiceChat.SetTransmissionMuted(__instance, muted);
            return false;
        }

        private static bool SetMuteReceive_Prefix(VivoxPlatform __instance, bool muted)
        {
            ReceiveMutedField?.SetValue(__instance, muted);
            SteamVoiceChat.SetReceiveMuted(__instance, muted);
            return false;
        }

        private static bool SetPlaybackVolume_Prefix(VivoxPlatform __instance,
            float volumeAsPercentage)
        {
            SteamVoiceChat.SetPlaybackVolume(__instance, volumeAsPercentage);
            return false;
        }

        private static bool SetRecordingVolume_Prefix(VivoxPlatform __instance,
            float volumeAsPercentage)
        {
            SteamVoiceChat.SetRecordingVolume(__instance, volumeAsPercentage);
            return false;
        }

        private static bool Login_Prefix()
        {
            return false;
        }
    }

    /// <summary>
    /// Steam-backed voice and party text chat. All Steam and Unity calls happen from the game's
    /// main thread through VivoxPlatform.UpdateEngine or Steam's callback pump. Only the PCM ring
    /// buffer crosses onto Unity's audio thread.
    /// </summary>
    internal static class SteamVoiceChat
    {
        private const int VoiceChannel = 1;
        private const int VoicePacketLimit = 1200;
        private const int VoiceHeaderSize = 22;
        private const int MaxVoicePayload = VoicePacketLimit - VoiceHeaderSize;
        private const int MaxSteamCaptureBytes = 32768;
        private const int MaxDecompressedBytes = 65536;
        private const int MaxDrainPacketBytes = 1024 * 1024;
        private const int MaxVoicePacketsPerFrame = 64;
        private const int MaxVoicePacketsPerPeerSecond = 100;
        private const int MaxVoiceBytesPerPeerSecond = 128 * 1024;
        private const int MaxTextUtf8Bytes = 4000;
        private const int MaxTextMessagesPerTenSeconds = 8;
        private const float RosterRefreshSeconds = 0.5f;
        private const float StatePublishSeconds = 0.1f;
        private const float StateHeartbeatSeconds = 2f;
        private const float SpeakingTimeoutSeconds = 0.3f;

        private const byte ProtocolVersion = 1;
        private const byte VoiceFrameType = 1;
        private const byte VoiceStateType = 2;
        private const byte TextMessageType = 1;

        private static readonly byte[] VoiceMagic = { 0x53, 0x53, 0x32, 0x56 }; // SS2V
        private static readonly byte[] TextMagic = { 0x53, 0x53, 0x32, 0x54 }; // SS2T
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static readonly MethodInfo RaisePeersUpdatedMethod =
            AccessTools.Method(typeof(VOIPProvider), "RaisePeersUpdated");

        private static readonly MethodInfo RaisePeersStateUpdatedMethod =
            AccessTools.Method(typeof(VOIPProvider), "RaisePeersStateUpdated");

        private static readonly byte[] CaptureBuffer = new byte[MaxSteamCaptureBytes];
        private static readonly byte[] VoiceSendBuffer = new byte[VoicePacketLimit];
        private static readonly byte[] VoiceReceiveBuffer = new byte[MaxDrainPacketBytes];
        private static readonly byte[] CompressedScratch = new byte[MaxVoicePayload];
        private static readonly byte[] DecompressedBuffer = new byte[MaxDecompressedBytes];
        private static readonly byte[] LobbyTextBuffer = new byte[4096];

        private static readonly Dictionary<ulong, RemoteSpeaker> Speakers =
            new Dictionary<ulong, RemoteSpeaker>();

        private static readonly Dictionary<ulong, TextRateWindow> TextRates =
            new Dictionary<ulong, TextRateWindow>();

        // Refreshed twice a second from Steam and reused by the per-frame capture path. Voice must
        // not allocate a new roster list every frame; those collections would turn into audible
        // garbage-collection stalls during open-mic play.
        private static readonly List<ulong> LobbyMembers = new List<ulong>(4);

        private static VivoxPlatform _provider;
        private static Callback<LobbyChatMsg_t> _lobbyChatCallback;
        private static string _myUid;
        private static string _displayName;
        private static string _currentChannel;
        private static uint _sequence;
        private static uint _sampleRate = 48000;
        private static bool _enabled;
        private static bool _transmissionMuted = true;
        private static bool _receiveMuted = true;
        private static bool _captureRunning;
        private static bool _drainAfterStop;
        private static float _receiveVolume = 1f;
        private static float _transmitVolume = 1f;
        private static float _nextRosterRefresh;
        private static float _nextStatePublish;
        private static float _nextStateHeartbeat;
        private static float _lastLocalVoiceFrame;
        private static bool _peersStateDirty;
        private static int _droppedVoicePackets;
        private static int _rejectedVoicePackets;
        private static int _rejectedTextMessages;
        private static float _nextDropLog;

        internal static bool Active => _provider != null;
        internal static string CurrentChannel => _currentChannel ?? string.Empty;
        internal static int RemoteSpeakerCount => Speakers.Count;
        internal static bool Capturing => _captureRunning;
        internal static int DroppedVoicePackets => _droppedVoicePackets;
        internal static int RejectedVoicePackets => _rejectedVoicePackets;
        internal static int RejectedTextMessages => _rejectedTextMessages;

        internal static void Initialise(VivoxPlatform provider, string myUid, string playerDisplayName)
        {
            if (provider == null)
                return;

            if (_provider != null && _provider != provider)
                Shutdown();

            _provider = provider;
            _myUid = string.IsNullOrEmpty(myUid)
                ? SteamIdentity.GetLocalPlayerId().ToString()
                : myUid;
            _displayName = string.IsNullOrEmpty(playerDisplayName)
                ? SteamIdentity.GetPersonaName()
                : playerDisplayName;
            _currentChannel = string.Empty;
            _sequence = 0;
            LobbyMembers.Clear();
            _enabled = false;
            _transmissionMuted = true;
            _receiveMuted = true;
            _captureRunning = false;
            _drainAfterStop = false;
            _nextRosterRefresh = 0f;
            _nextStatePublish = 0f;
            _nextStateHeartbeat = 0f;
            _lastLocalVoiceFrame = 0f;
            _droppedVoicePackets = 0;
            _rejectedVoicePackets = 0;
            _rejectedTextMessages = 0;
            provider.ParticipantByPlayerUID.Clear();

            try
            {
                var sampleRate = SteamUser.GetVoiceOptimalSampleRate();
                if (sampleRate >= 8000 && sampleRate <= 48000)
                    _sampleRate = sampleRate;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam voice sample-rate query failed; using 48000 Hz: "
                                      + ex.Message);
                _sampleRate = 48000;
            }

            _lobbyChatCallback?.Dispose();
            _lobbyChatCallback = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);

            Plugin.Log.LogInfo("Steam voice/chat initialised at " + _sampleRate + " Hz.");
        }

        internal static void Deinitialise(VivoxPlatform provider)
        {
            if (provider == _provider)
                Shutdown();
        }

        internal static void Shutdown()
        {
            StopCapture();
            _drainAfterStop = false;

            _lobbyChatCallback?.Dispose();
            _lobbyChatCallback = null;

            ClearRemoteState(clearParticipants: true);
            _provider = null;
            _myUid = null;
            _displayName = null;
            _currentChannel = string.Empty;
            LobbyMembers.Clear();
        }

        internal static void NotifyLobbyChanged()
        {
            if (_provider == null)
                return;

            StopCapture();
            _drainAfterStop = false;
            ClearRemoteState(clearParticipants: true);
            _nextRosterRefresh = 0f;
        }

        internal static void NotifyLobbyMembershipChanged()
        {
            if (_provider == null)
                return;
            _nextRosterRefresh = 0f;
            if (PartyBackend.InLobby && !string.IsNullOrEmpty(_currentChannel))
                SyncRoster(force: true);
        }

        internal static void RequestJoinChannel(VivoxPlatform provider, string channelName)
        {
            if (!Owns(provider) || string.IsNullOrEmpty(channelName))
                return;

            if (string.Equals(_currentChannel, channelName, StringComparison.Ordinal))
                return;

            StopCapture();
            _drainAfterStop = false;
            ClearRemoteState(clearParticipants: true);
            _currentChannel = channelName;
            _nextRosterRefresh = 0f;
            SyncRoster(force: true);
            Plugin.Log.LogInfo("Steam voice/chat joined " + channelName + ".");
        }

        internal static void RequestLeaveChannel(VivoxPlatform provider)
        {
            if (!Owns(provider))
                return;

            StopCapture();
            _drainAfterStop = false;
            _currentChannel = string.Empty;
            LobbyMembers.Clear();
            ClearRemoteState(clearParticipants: true);
        }

        internal static bool HasJoinedChannel(VivoxPlatform provider, string channelName)
        {
            return Owns(provider)
                   && PartyBackend.InLobby
                   && !string.IsNullOrEmpty(channelName)
                   && string.Equals(_currentChannel, channelName, StringComparison.Ordinal);
        }

        internal static string GetCurrentChannel(VivoxPlatform provider)
        {
            return Owns(provider) ? (_currentChannel ?? string.Empty) : string.Empty;
        }

        internal static void SetEnabled(VivoxPlatform provider, bool enabled)
        {
            if (!Owns(provider))
                return;
            _enabled = enabled;
            if (!enabled)
                StopCapture();
        }

        internal static void SetTransmissionMuted(VivoxPlatform provider, bool muted)
        {
            if (!Owns(provider))
                return;

            if (_transmissionMuted == muted)
                return;

            _transmissionMuted = muted;
            if (muted)
                StopCapture();
            UpdateLocalParticipantState();
            SendVoiceState();
        }

        internal static void SetReceiveMuted(VivoxPlatform provider, bool muted)
        {
            if (!Owns(provider))
                return;
            _receiveMuted = muted;
            ApplyPlaybackSettings();
        }

        internal static void SetPlaybackVolume(VivoxPlatform provider, float volume)
        {
            if (!Owns(provider))
                return;
            _receiveVolume = Clamp01(volume);
            ApplyPlaybackSettings();
        }

        internal static void SetRecordingVolume(VivoxPlatform provider, float volume)
        {
            if (!Owns(provider))
                return;
            _transmitVolume = Clamp01(volume);
        }

        internal static void SetRemoteMuted(VivoxPlatform provider, string playerId, bool muted)
        {
            if (!Owns(provider) || string.IsNullOrEmpty(playerId))
                return;

            if (!provider.ParticipantByPlayerUID.TryGetValue(playerId, out var info))
                return;

            if (info.isMuted == muted)
                return;

            info.isMuted = muted;
            provider.ParticipantByPlayerUID[playerId] = info;

            var steamId = TrySteamIdFromPlayerId(playerId);
            if (steamId != 0UL && Speakers.TryGetValue(steamId, out var speaker))
            {
                speaker.Buffer.Clear();
                ApplyPlaybackSettings(speaker, info);
            }

            RaisePeersStateUpdated();
        }

        internal static void Update(VivoxPlatform provider, float deltaTime)
        {
            if (!Owns(provider))
                return;

            var now = Time.realtimeSinceStartup;

            if (!PartyBackend.InLobby || string.IsNullOrEmpty(_currentChannel)
                || !SteamIdentity.IsSteamReady())
            {
                StopCapture();
                if (SteamIdentity.IsSteamReady())
                    PumpIncomingVoice(now);
                PublishPeerState(now);
                return;
            }

            if (now >= _nextRosterRefresh)
            {
                _nextRosterRefresh = now + RosterRefreshSeconds;
                SyncRoster(force: false);
            }

            UpdateCapture(now);
            PumpIncomingVoice(now);

            if (now >= _nextStateHeartbeat)
            {
                _nextStateHeartbeat = now + StateHeartbeatSeconds;
                SendVoiceState();
            }

            PublishPeerState(now);
            LogDrops(now);
        }

        internal static bool SendText(VivoxPlatform provider, string message)
        {
            if (!Owns(provider) || !PartyBackend.InLobby || string.IsNullOrEmpty(_currentChannel)
                || HasTextRestriction())
                return false;

            message = message ?? string.Empty;

            byte[] textBytes;
            try
            {
                textBytes = StrictUtf8.GetBytes(message);
            }
            catch (EncoderFallbackException)
            {
                _rejectedTextMessages++;
                return false;
            }

            if (textBytes.Length > MaxTextUtf8Bytes)
            {
                _rejectedTextMessages++;
                Plugin.Log.LogWarning("Refusing oversized party chat message ("
                                      + textBytes.Length + " UTF-8 bytes).");
                return false;
            }

            var packet = new byte[8 + textBytes.Length];
            Buffer.BlockCopy(TextMagic, 0, packet, 0, TextMagic.Length);
            packet[4] = ProtocolVersion;
            packet[5] = TextMessageType;
            WriteUInt16(packet, 6, (ushort)textBytes.Length);
            Buffer.BlockCopy(textBytes, 0, packet, 8, textBytes.Length);

            try
            {
                return SteamMatchmaking.SendLobbyChatMsg(PartyBackend.Lobby, packet, packet.Length);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Sending party chat failed: " + ex.Message);
                return false;
            }
        }

        private static void OnLobbyChatMessage(LobbyChatMsg_t callback)
        {
            if (_provider == null || !PartyBackend.InLobby
                || callback.m_ulSteamIDLobby != PartyBackend.Lobby.m_SteamID
                || callback.m_iChatID > int.MaxValue)
                return;

            try
            {
                var length = SteamMatchmaking.GetLobbyChatEntry(
                    PartyBackend.Lobby,
                    (int)callback.m_iChatID,
                    out var sender,
                    LobbyTextBuffer,
                    LobbyTextBuffer.Length,
                    out var entryType);

                if (length < 8 || length > LobbyTextBuffer.Length
                    || entryType != EChatEntryType.k_EChatEntryTypeChatMsg
                    || sender.m_SteamID == SteamIdentity.GetSteamId64()
                    || sender.m_SteamID != callback.m_ulSteamIDUser
                    || !PartyBackend.IsLobbyMember(sender.m_SteamID)
                    || IsBlocked(sender.m_SteamID)
                    || HasTextRestriction())
                    return;

                if (!MatchesMagic(LobbyTextBuffer, TextMagic)
                    || LobbyTextBuffer[4] != ProtocolVersion
                    || LobbyTextBuffer[5] != TextMessageType)
                {
                    return; // Other lobby messages are allowed; they simply are not ours.
                }

                var textLength = ReadUInt16(LobbyTextBuffer, 6);
                if (textLength > MaxTextUtf8Bytes || textLength != length - 8)
                {
                    _rejectedTextMessages++;
                    return;
                }

                var now = Time.realtimeSinceStartup;
                if (!AllowText(sender.m_SteamID, now))
                {
                    _rejectedTextMessages++;
                    return;
                }

                string text;
                try
                {
                    text = StrictUtf8.GetString(LobbyTextBuffer, 8, textLength);
                }
                catch (DecoderFallbackException)
                {
                    _rejectedTextMessages++;
                    return;
                }

                if (ContainsUnsafeControlCharacter(text))
                {
                    _rejectedTextMessages++;
                    return;
                }

                // TextChatPanelController wraps messages in <noparse>, but a literal closing tag
                // could end that region. Full-width brackets preserve readability while making a
                // message incapable of becoming TextMeshPro markup.
                text = text.Replace('<', '\uFF1C').Replace('>', '\uFF1E');

                var playerId = SteamIdentity.BuildPlayerIdString(sender.m_SteamID);
                if (!_provider.ParticipantByPlayerUID.ContainsKey(playerId))
                    return;

                var name = SteamIdentity.GetPersonaName(sender.m_SteamID);
                if (string.IsNullOrEmpty(name))
                    name = "Steam Player";

                try
                {
                    _provider.MessageReceived?.Invoke(name, playerId, text);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Displaying party chat failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Reading party chat failed: " + ex.Message);
            }
        }

        private static void UpdateCapture(float now)
        {
            var shouldCapture = _enabled
                                && !_transmissionMuted
                                && _transmitVolume > 0f
                                && !HasVoiceRestriction()
                                && LobbyMembers.Count > 1;

            if (shouldCapture && !_captureRunning)
                StartCapture();
            else if (!shouldCapture && _captureRunning)
                StopCapture();

            if (!_captureRunning && !_drainAfterStop)
                return;

            EVoiceResult availableResult;
            uint available;
            try
            {
                availableResult = SteamUser.GetAvailableVoice(out available);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam GetAvailableVoice failed: " + ex.Message);
                StopCapture();
                _drainAfterStop = false;
                return;
            }

            if (availableResult == EVoiceResult.k_EVoiceResultNotRecording)
            {
                _drainAfterStop = false;
                return;
            }

            if (availableResult == EVoiceResult.k_EVoiceResultNoData || available == 0)
                return;

            if (availableResult != EVoiceResult.k_EVoiceResultOK
                || available > CaptureBuffer.Length)
            {
                _droppedVoicePackets++;
                RestartCaptureAfterBadFrame(shouldCapture);
                return;
            }

            EVoiceResult voiceResult;
            uint written;
            try
            {
                voiceResult = SteamUser.GetVoice(true, CaptureBuffer,
                    (uint)CaptureBuffer.Length, out written);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam GetVoice failed: " + ex.Message);
                RestartCaptureAfterBadFrame(shouldCapture);
                return;
            }

            if (voiceResult == EVoiceResult.k_EVoiceResultNotRecording)
            {
                _drainAfterStop = false;
                return;
            }

            if (voiceResult != EVoiceResult.k_EVoiceResultOK || written == 0)
            {
                if (voiceResult != EVoiceResult.k_EVoiceResultNoData)
                    _droppedVoicePackets++;
                return;
            }

            if (written > MaxVoicePayload)
            {
                // A frame accumulated during a long main-thread stall. Delayed speech is worse
                // than loss, and reliable fragmentation would create head-of-line blocking.
                _droppedVoicePackets++;
                return;
            }

            var packetLength = BuildVoicePacket(VoiceFrameType, CaptureBuffer, (int)written,
                _transmitVolume);
            BroadcastVoicePacket(packetLength);
            _lastLocalVoiceFrame = now;
            _peersStateDirty = true;
        }

        private static void StartCapture()
        {
            if (_captureRunning || !SteamIdentity.IsSteamReady())
                return;

            try
            {
                SteamUser.StartVoiceRecording();
                _captureRunning = true;
                _drainAfterStop = false;
                SendVoiceState();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Starting Steam voice capture failed: " + ex.Message);
            }
        }

        private static void StopCapture()
        {
            if (!_captureRunning)
                return;

            try
            {
                SteamUser.StopVoiceRecording();
                _drainAfterStop = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Stopping Steam voice capture failed: " + ex.Message);
                _drainAfterStop = false;
            }
            finally
            {
                _captureRunning = false;
                _peersStateDirty = true;
            }

            SendVoiceState();
        }

        private static void RestartCaptureAfterBadFrame(bool shouldCapture)
        {
            if (_captureRunning)
            {
                try { SteamUser.StopVoiceRecording(); }
                catch { /* best effort reset */ }
            }

            _captureRunning = false;
            _drainAfterStop = false;
            if (shouldCapture)
                StartCapture();
        }

        private static void SendVoiceState()
        {
            if (_provider == null || !PartyBackend.InLobby || string.IsNullOrEmpty(_currentChannel))
                return;

            VoiceSendBuffer[VoiceHeaderSize] = _transmissionMuted || !_enabled ? (byte)1 : (byte)0;
            var length = BuildVoicePacket(VoiceStateType, VoiceSendBuffer, VoiceHeaderSize, 1,
                _transmitVolume);
            BroadcastVoicePacket(length);
        }

        private static int BuildVoicePacket(byte packetType, byte[] payload, int payloadLength,
            float gain)
        {
            return BuildVoicePacket(packetType, payload, 0, payloadLength, gain);
        }

        private static int BuildVoicePacket(byte packetType, byte[] payload, int payloadOffset,
            int payloadLength, float gain)
        {
            if (payloadLength < 0 || payloadLength > MaxVoicePayload)
                return 0;

            Buffer.BlockCopy(VoiceMagic, 0, VoiceSendBuffer, 0, VoiceMagic.Length);
            VoiceSendBuffer[4] = ProtocolVersion;
            VoiceSendBuffer[5] = packetType;
            WriteUInt32(VoiceSendBuffer, 6, ++_sequence);
            WriteUInt64(VoiceSendBuffer, 10,
                PartyBackend.InLobby ? PartyBackend.Lobby.m_SteamID : 0UL);
            WriteUInt16(VoiceSendBuffer, 18, (ushort)(Clamp01(gain) * 1000f));
            WriteUInt16(VoiceSendBuffer, 20, (ushort)payloadLength);
            if (payloadLength > 0 && !ReferenceEquals(payload, VoiceSendBuffer))
                Buffer.BlockCopy(payload, payloadOffset, VoiceSendBuffer, VoiceHeaderSize, payloadLength);
            else if (payloadLength > 0 && payloadOffset != VoiceHeaderSize)
                Buffer.BlockCopy(payload, payloadOffset, VoiceSendBuffer, VoiceHeaderSize, payloadLength);
            return VoiceHeaderSize + payloadLength;
        }

        private static void BroadcastVoicePacket(int packetLength)
        {
            if (packetLength <= VoiceHeaderSize || packetLength > VoicePacketLimit)
                return;

            var self = SteamIdentity.GetSteamId64();
            for (var i = 0; i < LobbyMembers.Count; i++)
            {
                var member = LobbyMembers[i];
                if (member == 0UL || member == self)
                    continue;

                try
                {
                    if (!SteamNetworking.SendP2PPacket(new CSteamID(member), VoiceSendBuffer,
                            (uint)packetLength, EP2PSend.k_EP2PSendUnreliableNoDelay, VoiceChannel))
                        _droppedVoicePackets++;
                }
                catch
                {
                    _droppedVoicePackets++;
                }
            }
        }

        private static void PumpIncomingVoice(float now)
        {
            var processed = 0;

            try
            {
                while (processed < MaxVoicePacketsPerFrame
                       && SteamNetworking.IsP2PPacketAvailable(out var size, VoiceChannel))
                {
                    processed++;

                    if (size > VoiceReceiveBuffer.Length)
                    {
                        // Steam's legacy P2P maximum is bounded by the fixed drain buffer. A packet
                        // beyond it cannot be valid voice; stop this frame rather than allocate from
                        // attacker-controlled input.
                        _rejectedVoicePackets++;
                        break;
                    }

                    if (!SteamNetworking.ReadP2PPacket(VoiceReceiveBuffer,
                            (uint)VoiceReceiveBuffer.Length, out var read, out var sender,
                            VoiceChannel))
                        break;

                    if (read > VoicePacketLimit || read < VoiceHeaderSize
                        || !PartyBackend.IsLobbyMember(sender.m_SteamID)
                        || IsBlocked(sender.m_SteamID))
                    {
                        _rejectedVoicePackets++;
                        continue;
                    }

                    ProcessVoicePacket(sender.m_SteamID, (int)read, now);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam voice receive pump failed: " + ex.Message);
            }
        }

        private static void ProcessVoicePacket(ulong sender, int length, float now)
        {
            if (!MatchesMagic(VoiceReceiveBuffer, VoiceMagic)
                || VoiceReceiveBuffer[4] != ProtocolVersion)
            {
                _rejectedVoicePackets++;
                return;
            }

            var packetType = VoiceReceiveBuffer[5];
            var sequence = ReadUInt32(VoiceReceiveBuffer, 6);
            var lobby = ReadUInt64(VoiceReceiveBuffer, 10);
            var gain = ReadUInt16(VoiceReceiveBuffer, 18) / 1000f;
            var payloadLength = ReadUInt16(VoiceReceiveBuffer, 20);

            if (lobby == 0UL || !PartyBackend.InLobby || lobby != PartyBackend.Lobby.m_SteamID
                || payloadLength != length - VoiceHeaderSize
                || gain < 0f || gain > 1f)
            {
                _rejectedVoicePackets++;
                return;
            }

            var playerId = SteamIdentity.BuildPlayerIdString(sender);
            if (_provider == null || !_provider.ParticipantByPlayerUID.ContainsKey(playerId))
            {
                _rejectedVoicePackets++;
                return;
            }

            if (!Speakers.TryGetValue(sender, out var speaker))
            {
                speaker = new RemoteSpeaker(sender, playerId, _sampleRate);
                Speakers.Add(sender, speaker);
            }

            if (!speaker.AllowVoicePacket(now, length, MaxVoicePacketsPerPeerSecond,
                    MaxVoiceBytesPerPeerSecond))
            {
                _rejectedVoicePackets++;
                return;
            }

            if (packetType == VoiceStateType)
            {
                if (payloadLength != 1)
                {
                    _rejectedVoicePackets++;
                    return;
                }

                var muted = VoiceReceiveBuffer[VoiceHeaderSize] != 0;
                var state = _provider.ParticipantByPlayerUID[playerId];
                if (state.isTransmissionMuted != muted)
                {
                    state.isTransmissionMuted = muted;
                    if (muted)
                    {
                        state.isSpeaking = false;
                        state.audioEnergy = 0f;
                        speaker.Buffer.Clear();
                    }
                    _provider.ParticipantByPlayerUID[playerId] = state;
                    _peersStateDirty = true;
                }
                return;
            }

            if (packetType != VoiceFrameType || payloadLength == 0
                || payloadLength > MaxVoicePayload || HasVoiceRestriction())
            {
                _rejectedVoicePackets++;
                return;
            }

            if (speaker.HasSequence
                && unchecked((int)(sequence - speaker.LastSequence)) <= 0)
            {
                _rejectedVoicePackets++;
                return;
            }
            speaker.HasSequence = true;
            speaker.LastSequence = sequence;

            if (_receiveMuted)
                return;

            Buffer.BlockCopy(VoiceReceiveBuffer, VoiceHeaderSize, CompressedScratch, 0,
                payloadLength);

            EVoiceResult result;
            uint written;
            try
            {
                result = SteamUser.DecompressVoice(CompressedScratch, payloadLength,
                    DecompressedBuffer, (uint)DecompressedBuffer.Length, out written, _sampleRate);
            }
            catch
            {
                _rejectedVoicePackets++;
                return;
            }

            if (result != EVoiceResult.k_EVoiceResultOK || written == 0
                || written > DecompressedBuffer.Length || (written & 1u) != 0u)
            {
                _rejectedVoicePackets++;
                return;
            }

            var info = _provider.ParticipantByPlayerUID[playerId];
            var localMuted = info.isMuted || IsBlocked(sender);
            var energy = speaker.Buffer.MeasureAndWritePcm16(
                DecompressedBuffer, (int)written, gain, !localMuted);

            speaker.LastVoiceAt = now;
            info.isTransmissionMuted = false;
            info.isSpeaking = true;
            info.audioEnergy = energy;
            _provider.ParticipantByPlayerUID[playerId] = info;
            ApplyPlaybackSettings(speaker, info);
            _peersStateDirty = true;
        }

        private static void SyncRoster(bool force)
        {
            if (_provider == null || !PartyBackend.InLobby || string.IsNullOrEmpty(_currentChannel))
                return;

            var members = PartyBackend.GetLobbyMembers();
            LobbyMembers.Clear();
            LobbyMembers.AddRange(members);
            var keep = new HashSet<ulong>();
            var changed = false;
            var self = SteamIdentity.GetSteamId64();

            for (var i = 0; i < members.Count; i++)
            {
                var steamId = members[i];
                if (steamId == 0UL)
                    continue;

                keep.Add(steamId);
                var playerId = steamId == self ? _myUid : SteamIdentity.BuildPlayerIdString(steamId);
                var name = steamId == self ? _displayName : SteamIdentity.GetPersonaName(steamId);
                if (string.IsNullOrEmpty(name))
                    name = "Steam Player";

                if (!_provider.ParticipantByPlayerUID.TryGetValue(playerId, out var info))
                {
                    info = new VOIPPlayerInfo
                    {
                        uid = playerId,
                        displayName = name,
                        isMuted = false,
                        isTransmissionMuted = steamId == self && _transmissionMuted,
                        isSpeaking = false,
                        audioEnergy = 0f,
                        key = steamId.ToString()
                    };
                    _provider.ParticipantByPlayerUID.Add(playerId, info);
                    changed = true;
                }
                else if (!string.Equals(info.displayName, name, StringComparison.Ordinal)
                         && name != "Steam Player")
                {
                    info.displayName = name;
                    _provider.ParticipantByPlayerUID[playerId] = info;
                    changed = true;
                }
            }

            var removePlayers = new List<string>();
            foreach (var pair in _provider.ParticipantByPlayerUID)
            {
                var steamId = TrySteamIdFromPlayerId(pair.Key);
                if (pair.Key == _myUid)
                    steamId = self;
                if (steamId == 0UL || !keep.Contains(steamId))
                    removePlayers.Add(pair.Key);
            }

            for (var i = 0; i < removePlayers.Count; i++)
            {
                var playerId = removePlayers[i];
                var steamId = TrySteamIdFromPlayerId(playerId);
                _provider.ParticipantByPlayerUID.Remove(playerId);
                if (steamId != 0UL && Speakers.TryGetValue(steamId, out var speaker))
                {
                    speaker.Dispose();
                    Speakers.Remove(steamId);
                }
                if (steamId != 0UL)
                    TextRates.Remove(steamId);
                changed = true;
            }

            var removeSpeakers = new List<ulong>();
            foreach (var pair in Speakers)
            {
                if (!keep.Contains(pair.Key))
                    removeSpeakers.Add(pair.Key);
            }
            for (var i = 0; i < removeSpeakers.Count; i++)
            {
                Speakers[removeSpeakers[i]].Dispose();
                Speakers.Remove(removeSpeakers[i]);
                TextRates.Remove(removeSpeakers[i]);
                changed = true;
            }

            if (changed || force)
                RaisePeersUpdated();
        }

        private static void UpdateLocalParticipantState()
        {
            if (_provider == null || string.IsNullOrEmpty(_myUid)
                || !_provider.ParticipantByPlayerUID.TryGetValue(_myUid, out var info))
                return;

            info.isTransmissionMuted = _transmissionMuted || !_enabled;
            if (info.isTransmissionMuted)
            {
                info.isSpeaking = false;
                info.audioEnergy = 0f;
            }
            _provider.ParticipantByPlayerUID[_myUid] = info;
            _peersStateDirty = true;
        }

        private static void PublishPeerState(float now)
        {
            if (_provider == null || now < _nextStatePublish)
                return;

            _nextStatePublish = now + StatePublishSeconds;

            if (!string.IsNullOrEmpty(_myUid)
                && _provider.ParticipantByPlayerUID.TryGetValue(_myUid, out var local))
            {
                var speaking = _captureRunning
                               && !_transmissionMuted
                               && now - _lastLocalVoiceFrame <= SpeakingTimeoutSeconds;
                if (local.isSpeaking != speaking || (speaking && local.audioEnergy < 0.35f)
                    || (!speaking && local.audioEnergy != 0f))
                {
                    local.isSpeaking = speaking;
                    local.audioEnergy = speaking ? 0.35f : 0f;
                    local.isTransmissionMuted = _transmissionMuted || !_enabled;
                    _provider.ParticipantByPlayerUID[_myUid] = local;
                    _peersStateDirty = true;
                }
            }

            foreach (var pair in Speakers)
            {
                var speaker = pair.Value;
                if (now - speaker.LastVoiceAt <= SpeakingTimeoutSeconds)
                    continue;

                if (_provider.ParticipantByPlayerUID.TryGetValue(speaker.PlayerId, out var info)
                    && (info.isSpeaking || info.audioEnergy != 0f))
                {
                    info.isSpeaking = false;
                    info.audioEnergy = 0f;
                    _provider.ParticipantByPlayerUID[speaker.PlayerId] = info;
                    _peersStateDirty = true;
                }
            }

            if (_peersStateDirty)
            {
                _peersStateDirty = false;
                RaisePeersStateUpdated();
            }
        }

        private static void ApplyPlaybackSettings()
        {
            if (_provider == null)
                return;

            foreach (var pair in Speakers)
            {
                if (_provider.ParticipantByPlayerUID.TryGetValue(pair.Value.PlayerId, out var info))
                    ApplyPlaybackSettings(pair.Value, info);
            }
        }

        private static void ApplyPlaybackSettings(RemoteSpeaker speaker, VOIPPlayerInfo info)
        {
            if (speaker.Source == null)
                return;
            speaker.Source.volume = _receiveVolume;
            speaker.Source.mute = _receiveMuted || info.isMuted || IsBlocked(speaker.SteamId);
            if (speaker.Source.mute)
                speaker.Buffer.Clear();
        }

        private static void ClearRemoteState(bool clearParticipants)
        {
            foreach (var pair in Speakers)
                pair.Value.Dispose();
            Speakers.Clear();
            TextRates.Clear();
            LobbyMembers.Clear();

            if (clearParticipants && _provider != null
                && _provider.ParticipantByPlayerUID.Count > 0)
            {
                _provider.ParticipantByPlayerUID.Clear();
                RaisePeersUpdated();
            }
        }

        private static bool AllowText(ulong steamId, float now)
        {
            if (!TextRates.TryGetValue(steamId, out var rate))
            {
                rate = new TextRateWindow { WindowStart = now, Messages = 0 };
                TextRates.Add(steamId, rate);
            }

            if (now - rate.WindowStart >= 10f)
            {
                rate.WindowStart = now;
                rate.Messages = 0;
            }

            if (rate.Messages >= MaxTextMessagesPerTenSeconds)
                return false;

            rate.Messages++;
            return true;
        }

        private static bool HasVoiceRestriction()
        {
            try
            {
                var restrictions = SteamFriends.GetUserRestrictions();
                return (restrictions & (uint)EUserRestriction.k_nUserRestrictionAnyChat) != 0u
                       || (restrictions & (uint)EUserRestriction.k_nUserRestrictionVoiceChat) != 0u;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasTextRestriction()
        {
            try
            {
                var restrictions = SteamFriends.GetUserRestrictions();
                return (restrictions & (uint)EUserRestriction.k_nUserRestrictionAnyChat) != 0u
                       || (restrictions & (uint)EUserRestriction.k_nUserRestrictionGroupChat) != 0u;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBlocked(ulong steamId)
        {
            if (steamId == 0UL || steamId == SteamIdentity.GetSteamId64())
                return false;

            try
            {
                var relationship = SteamFriends.GetFriendRelationship(new CSteamID(steamId));
                return relationship == EFriendRelationship.k_EFriendRelationshipBlocked
                       || relationship == EFriendRelationship.k_EFriendRelationshipIgnored
                       || relationship == EFriendRelationship.k_EFriendRelationshipIgnoredFriend;
            }
            catch
            {
                return false;
            }
        }

        private static void RaisePeersUpdated()
        {
            if (_provider == null || RaisePeersUpdatedMethod == null)
                return;
            try { RaisePeersUpdatedMethod.Invoke(_provider, null); }
            catch (Exception ex) { Plugin.Log.LogWarning("Voice roster UI update failed: " + ex.Message); }
        }

        private static void RaisePeersStateUpdated()
        {
            if (_provider == null || RaisePeersStateUpdatedMethod == null)
                return;
            try { RaisePeersStateUpdatedMethod.Invoke(_provider, null); }
            catch (Exception ex) { Plugin.Log.LogWarning("Voice state UI update failed: " + ex.Message); }
        }

        private static bool Owns(VivoxPlatform provider)
        {
            return provider != null && provider == _provider;
        }

        private static ulong TrySteamIdFromPlayerId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)
                || !playerId.StartsWith("STEAM-", StringComparison.Ordinal))
                return 0UL;
            var digits = playerId.Substring(6).TrimEnd('-');
            return ulong.TryParse(digits, out var value) ? value : 0UL;
        }

        private static bool MatchesMagic(byte[] buffer, byte[] magic)
        {
            return buffer != null && magic != null && buffer.Length >= magic.Length
                   && buffer[0] == magic[0] && buffer[1] == magic[1]
                   && buffer[2] == magic[2] && buffer[3] == magic[3];
        }

        private static bool ContainsUnsafeControlCharacter(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsControl(text[i]))
                    return true;
            }
            return false;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }

        private static void LogDrops(float now)
        {
            if (now < _nextDropLog)
                return;
            _nextDropLog = now + 10f;
            if (_droppedVoicePackets > 0 || _rejectedVoicePackets > 0
                || _rejectedTextMessages > 0)
            {
                Plugin.Log.LogInfo("Steam voice/chat counters: droppedVoice="
                                   + _droppedVoicePackets + ", rejectedVoice="
                                   + _rejectedVoicePackets + ", rejectedText="
                                   + _rejectedTextMessages + ".");
            }
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | (buffer[offset + 1] << 8)
                          | (buffer[offset + 2] << 16)
                          | (buffer[offset + 3] << 24));
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (var i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (i * 8));
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0UL;
            for (var i = 0; i < 8; i++)
                value |= (ulong)buffer[offset + i] << (i * 8);
            return value;
        }

        private sealed class TextRateWindow
        {
            internal float WindowStart;
            internal int Messages;
        }

        private sealed class RemoteSpeaker : IDisposable
        {
            private float _voiceWindowStart;
            private int _voicePackets;
            private int _voiceBytes;

            internal readonly ulong SteamId;
            internal readonly string PlayerId;
            internal readonly PcmRingBuffer Buffer;
            internal readonly AudioClip Clip;
            internal readonly AudioSource Source;
            internal uint LastSequence;
            internal bool HasSequence;
            internal float LastVoiceAt;

            internal RemoteSpeaker(ulong steamId, string playerId, uint sampleRate)
            {
                SteamId = steamId;
                PlayerId = playerId;
                var rate = (int)sampleRate;
                Buffer = new PcmRingBuffer(rate * 2, (int)(rate * 0.1f));

                var host = new GameObject("SS2Revive Voice " + steamId)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(host);
                Source = host.AddComponent<AudioSource>();
                Source.playOnAwake = false;
                Source.loop = true;
                Source.spatialBlend = 0f;
                Source.priority = 32;
                Clip = AudioClip.Create("SS2Revive Voice " + steamId, rate, 1, rate, true,
                    Buffer.Read, null);
                Source.clip = Clip;
                Source.Play();
            }

            internal bool AllowVoicePacket(float now, int bytes, int packetLimit, int byteLimit)
            {
                if (now - _voiceWindowStart >= 1f)
                {
                    _voiceWindowStart = now;
                    _voicePackets = 0;
                    _voiceBytes = 0;
                }

                if (_voicePackets >= packetLimit || _voiceBytes > byteLimit - bytes)
                    return false;

                _voicePackets++;
                _voiceBytes += bytes;
                return true;
            }

            public void Dispose()
            {
                Buffer.Clear();
                if (Source != null)
                {
                    Source.Stop();
                    UnityEngine.Object.Destroy(Source.gameObject);
                }
                if (Clip != null)
                    UnityEngine.Object.Destroy(Clip);
            }
        }

        /// <summary>A bounded single-writer/single-reader PCM queue shared with Unity's audio thread.</summary>
        private sealed class PcmRingBuffer
        {
            private readonly float[] _samples;
            private readonly int _primeSamples;
            private readonly object _lock = new object();
            private int _read;
            private int _write;
            private int _count;
            private bool _primed;

            internal PcmRingBuffer(int capacity, int primeSamples)
            {
                _samples = new float[Math.Max(1024, capacity)];
                _primeSamples = Math.Max(1, Math.Min(primeSamples, _samples.Length));
            }

            internal float MeasureAndWritePcm16(byte[] pcm, int byteCount, float gain, bool write)
            {
                var sampleCount = byteCount / 2;
                if (sampleCount <= 0)
                    return 0f;

                double sumSquares = 0d;
                gain = Clamp01(gain);

                lock (_lock)
                {
                    var startSample = sampleCount > _samples.Length
                        ? sampleCount - _samples.Length
                        : 0;

                    if (write)
                    {
                        var retained = sampleCount - startSample;
                        var overflow = Math.Max(0, _count + retained - _samples.Length);
                        if (overflow > 0)
                        {
                            _read = (_read + overflow) % _samples.Length;
                            _count -= overflow;
                        }
                    }

                    for (var i = 0; i < sampleCount; i++)
                    {
                        var byteIndex = i * 2;
                        var raw = (short)(pcm[byteIndex] | (pcm[byteIndex + 1] << 8));
                        var sample = (raw / 32768f) * gain;
                        sumSquares += sample * sample;

                        if (!write || i < startSample)
                            continue;

                        _samples[_write] = sample;
                        _write = (_write + 1) % _samples.Length;
                        _count++;
                    }
                }

                var rms = (float)Math.Sqrt(sumSquares / sampleCount);
                return Clamp01(rms * 4f);
            }

            internal void Read(float[] destination)
            {
                if (destination == null)
                    return;

                lock (_lock)
                {
                    if (!_primed && _count >= _primeSamples)
                        _primed = true;

                    if (!_primed)
                    {
                        Array.Clear(destination, 0, destination.Length);
                        return;
                    }

                    var copied = Math.Min(destination.Length, _count);
                    for (var i = 0; i < copied; i++)
                    {
                        destination[i] = _samples[_read];
                        _read = (_read + 1) % _samples.Length;
                    }
                    _count -= copied;

                    if (copied < destination.Length)
                    {
                        Array.Clear(destination, copied, destination.Length - copied);
                        if (_count == 0)
                            _primed = false;
                    }
                }
            }

            internal void Clear()
            {
                lock (_lock)
                {
                    _read = 0;
                    _write = 0;
                    _count = 0;
                    _primed = false;
                }
            }
        }
    }
}
