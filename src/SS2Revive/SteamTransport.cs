using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Steamworks;

namespace SS2Revive
{
    /// <summary>
    /// Carries the game's peer-to-peer traffic over Steam instead of raw UDP.
    ///
    /// The whole game above this point speaks in IPEndPoints - connections, ping timing, host
    /// election, the packet demultiplexer, all of it. None of that needs to change, because none of
    /// it ever inspects an address; it only uses one as a key. So each peer gets a *synthetic*
    /// endpoint derived from their Steam account, we publish those in the party's connectionDetails,
    /// and here we translate back: an outbound packet aimed at a synthetic endpoint goes out as a
    /// Steam P2P message, and an inbound Steam message is handed to the game as if it had arrived
    /// from that endpoint.
    ///
    /// This replaces the part Bossa's backend used to do rather than reimplementing it. Their server
    /// performed the introduction - STUN for external addresses, TURN to relay when a direct route
    /// could not be established. Both of those hosts are gone, and no amount of correct addressing
    /// gets two players behind NAT talking without something in the middle. Steam is that something,
    /// and it is already holding both accounts in a lobby.
    ///
    /// The mapping is a pure function of the Steam account id, so every machine derives the same
    /// endpoint for the same player. That matters: peers tell each other what address they were
    /// reached on, so a mapping that differed per machine would have them disagree about who is who.
    /// </summary>
    internal static class SteamTransport
    {
        /// <summary>
        /// Individual account, public universe, instance 1 - the high bits every player's SteamID64
        /// shares. Only the low 32 bits distinguish accounts, so those are the only ones an address
        /// has to carry.
        /// </summary>
        private const ulong IndividualPublic = 0x0110000100000000UL;

        /// <summary>Arbitrary but fixed; it only has to be consistent and not look like a real port.</summary>
        private const int SyntheticPort = 45510;

        /// <summary>
        /// Steam's unreliable path is limited to roughly this much per message, while the game will
        /// happily hand us up to 1280 bytes. Anything larger is sent reliably instead - which is
        /// slower but arrives, and is much better than a packet that silently vanishes.
        /// </summary>
        private const int UnreliableLimit = 1200;

        private const int Channel = 0;

        /// <summary>Bounded so a stalled main thread cannot let the queue grow without limit.</summary>
        private const int MaxQueuedSends = 256;

        private const int MaxPacketSize = 1280;

        // Only accounts we have actually seen in the party are translated. Without this, a packet
        // aimed at a real STUN or TURN server could be mistaken for a peer if its address happened
        // to decode to something plausible.
        //
        // Read from the game's send thread and written from the main thread, so every touch is
        // under this lock - a HashSet read concurrently with a write can loop forever, not merely
        // return the wrong answer.
        private static readonly HashSet<ulong> Peers = new HashSet<ulong>();
        private static readonly object PeerLock = new object();

        private struct QueuedSend
        {
            public ulong SteamId;
            public byte[] Data;
            public int Length;
        }

        // Steamworks is called only from the main thread, so sends raised on the game's send thread
        // are parked here and flushed by the pump. See TrySend.
        private static readonly Queue<QueuedSend> Outbound = new Queue<QueuedSend>();
        private static readonly Stack<byte[]> BufferPool = new Stack<byte[]>();
        private static readonly object SendLock = new object();
        private static int _droppedSends;

        /// <summary>Bounded for the same reason as the send queue; loss is survivable, growth is not.</summary>
        private const int MaxQueuedReceives = 512;

        private static Callback<P2PSessionRequest_t> _sessionRequest;
        private static Callback<P2PSessionConnectFail_t> _sessionFailed;

        private static object _clientManager;
        private static MethodInfo _onReceivePacket;
        private static readonly byte[] ReadBuffer = new byte[MaxPacketSize];

        private struct QueuedReceive
        {
            public IPEndPoint Sender;
            public byte[] Data;
            public int Length;
        }

        // Handed from the pump to the delivery thread. See DeliveryThread_Execute for why the
        // delivery cannot happen on the main thread.
        private static readonly Queue<QueuedReceive> Inbound = new Queue<QueuedReceive>();
        private static readonly Stack<byte[]> ReceivePool = new Stack<byte[]>();
        private static readonly object ReceiveLock = new object();
        private static readonly AutoResetEvent InboundReady = new AutoResetEvent(false);
        private static Thread _deliveryThread;
        private static volatile bool _exiting;
        private static int _droppedReceives;

        // Reused per peer so the game is handed a stable instance. NetworkPeerManager stores the
        // sender it was given in knownReceivedFromEndPoint and compares against it later, so a
        // fresh object per packet would be pure garbage for no benefit. Touched only by the pump,
        // which is main-thread only, so it needs no lock.
        private static readonly Dictionary<ulong, IPEndPoint> EndPoints = new Dictionary<ulong, IPEndPoint>();

        internal static bool Ready => _clientManager != null && _onReceivePacket != null;

        internal static int KnownPeers
        {
            get { lock (PeerLock) return Peers.Count; }
        }

        internal static void Initialise()
        {
            // Anyone in our lobby is expected; accepting is what opens the session in both
            // directions. There is no wider exposure here than the lobby already implies.
            _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<P2PSessionConnectFail_t>.Create(OnSessionFailed);
            Plugin.Log.LogInfo("Steam transport ready.");
        }

        /// <summary>Captured from UdpClientManager.Initialise so received packets can be injected.</summary>
        internal static void AttachClientManager(object clientManager)
        {
            _clientManager = clientManager;
            _onReceivePacket = AccessTools.Method(clientManager.GetType(), "OnReceivePacket",
                new[] { typeof(IPEndPoint), typeof(byte[]), typeof(int) });

            if (_onReceivePacket == null)
            {
                Plugin.Log.LogError("UdpClientManager.OnReceivePacket not found; inbound traffic cannot be delivered.");
                return;
            }

            StartDeliveryThread();
            Plugin.Log.LogInfo("Attached to UdpClientManager; inbound Steam traffic will be delivered.");
        }

        private static void StartDeliveryThread()
        {
            if (_deliveryThread != null)
                return;

            _exiting = false;
            _deliveryThread = new Thread(DeliveryThread_Execute)
            {
                Name = "SS2Revive Steam delivery",
                // Background, so a thread parked on NetworkReceiver's backpressure cannot keep the
                // process alive after the game has decided to quit.
                IsBackground = true
            };
            _deliveryThread.Start();
        }

        // ------------------------------------------------------------------ addressing

        /// <summary>
        /// Records a player as reachable over Steam and returns the address the game should use for
        /// them. Called while building the party, which is the only place both facts are known.
        /// </summary>
        internal static uint RegisterPeer(ulong steamId64)
        {
            if (steamId64 == 0UL)
                return 0u;

            lock (PeerLock)
                Peers.Add(steamId64);

            return (uint)(steamId64 & 0xFFFFFFFFUL);
        }

        private static bool IsPeer(ulong steamId64)
        {
            lock (PeerLock)
                return Peers.Contains(steamId64);
        }

        internal static int Port => SyntheticPort;

        internal static bool TryResolve(IPEndPoint endPoint, out ulong steamId64)
        {
            steamId64 = 0UL;

            if (endPoint == null || endPoint.Port != SyntheticPort)
                return false;

            // IPAddress.Address is the packed little-endian form the game uses throughout, so the
            // low 32 bits come back exactly as RegisterPeer produced them.
#pragma warning disable CS0618
            var accountId = (uint)(endPoint.Address.Address & 0xFFFFFFFFL);
#pragma warning restore CS0618

            var candidate = IndividualPublic | accountId;
            if (!IsPeer(candidate))
                return false;

            steamId64 = candidate;
            return true;
        }

        // ----------------------------------------------------------------------- send

        /// <summary>
        /// Returns true when the packet was handled here. A false result means the recipient is not
        /// a Steam peer - a STUN or TURN server - and the original UDP path should run, where it
        /// will fail against a host that no longer exists, exactly as it does today.
        ///
        /// This runs on NetworkSender's own send thread, which is why nothing here touches Steam.
        /// The Steamworks client interfaces are not safe to drive from a background thread while the
        /// main thread is inside RunCallbacks or reading packets; doing so does not throw, it
        /// corrupts the process. So the packet is copied and parked, and the pump sends it.
        ///
        /// The copy is not optional either: the caller reuses its buffer the moment this returns.
        /// </summary>
        internal static bool TrySend(IPEndPoint recipient, byte[] buffer, int byteCount)
        {
            if (!TryResolve(recipient, out var steamId))
                return false;

            if (byteCount > MaxPacketSize)
            {
                Plugin.Log.LogWarning("Refusing oversized outbound packet (" + byteCount + " bytes).");
                return true;
            }

            lock (SendLock)
            {
                if (Outbound.Count >= MaxQueuedSends)
                {
                    // Dropping is the right failure for an unreliable transport - the game's own
                    // protocol already treats loss as normal and will resend what matters.
                    _droppedSends++;
                    return true;
                }

                var data = BufferPool.Count > 0 ? BufferPool.Pop() : new byte[MaxPacketSize];
                Buffer.BlockCopy(buffer, 0, data, 0, byteCount);

                Outbound.Enqueue(new QueuedSend { SteamId = steamId, Data = data, Length = byteCount });
            }

            return true;
        }

        private static void FlushOutbound()
        {
            while (true)
            {
                QueuedSend send;
                lock (SendLock)
                {
                    if (Outbound.Count == 0)
                    {
                        if (_droppedSends > 0)
                        {
                            Plugin.Log.LogWarning("Dropped " + _droppedSends
                                                  + " outbound packet(s); send queue was full.");
                            _droppedSends = 0;
                        }
                        return;
                    }

                    send = Outbound.Dequeue();
                }

                // The game's protocol is built for lossy unordered UDP and does its own sequencing,
                // so sending unreliably preserves the behaviour it was tuned against. Reliability
                // would add head-of-line blocking to a stream designed to tolerate loss. Only
                // packets past Steam's unreliable size limit are promoted, so that a large one
                // arrives late rather than not at all.
                var mode = send.Length > UnreliableLimit
                    ? EP2PSend.k_EP2PSendReliable
                    : EP2PSend.k_EP2PSendUnreliable;

                try
                {
                    if (!SteamNetworking.SendP2PPacket(new CSteamID(send.SteamId), send.Data,
                            (uint)send.Length, mode, Channel))
                        Plugin.Log.LogWarning("SendP2PPacket to " + send.SteamId + " failed ("
                                              + send.Length + " bytes).");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("SendP2PPacket threw: " + ex);
                }
                finally
                {
                    lock (SendLock)
                        BufferPool.Push(send.Data);
                }
            }
        }

        // -------------------------------------------------------------------- receive

        /// <summary>
        /// Main-thread half of the transport: everything that has to talk to Steam. Sends are
        /// flushed, inbound packets are read and authorised, and that is all - the packets are
        /// handed to the delivery thread rather than pushed into the game here.
        /// </summary>
        internal static void Pump()
        {
            if (!Ready || !SteamIdentity.IsSteamReady())
                return;

            // Sends first: a packet queued during the last frame should leave before we spend time
            // draining inbound traffic.
            FlushOutbound();

            var queued = 0;

            try
            {
                while (SteamNetworking.IsP2PPacketAvailable(out var size, Channel))
                {
                    if (size > MaxPacketSize)
                    {
                        // Read and drop: leaving it queued would block every packet behind it.
                        SteamNetworking.ReadP2PPacket(ReadBuffer, MaxPacketSize, out _, out _, Channel);
                        Plugin.Log.LogWarning("Dropped oversized P2P packet (" + size + " bytes).");
                        continue;
                    }

                    if (!SteamNetworking.ReadP2PPacket(ReadBuffer, MaxPacketSize,
                            out var read, out var sender, Channel))
                        break;

                    // Authorising here, not on the delivery thread, is deliberate: IsLobbyMember
                    // asks Steam, and Steam is only ever asked from this thread.
                    //
                    // Same reasoning as accepting a session - if they are in the lobby they are
                    // legitimate, and dropping their first packets because we had not indexed them
                    // yet would stall a handshake that is otherwise fine.
                    var steamId = sender.m_SteamID;
                    if (!IsPeer(steamId))
                    {
                        if (!PartyBackend.IsLobbyMember(steamId))
                        {
                            Plugin.Log.LogWarning("P2P packet from unknown peer " + steamId + "; ignored.");
                            continue;
                        }

                        RegisterPeer(steamId);
                    }

                    if (!EndPoints.TryGetValue(steamId, out var endPoint))
                    {
                        endPoint = new IPEndPoint((uint)(steamId & 0xFFFFFFFFUL), SyntheticPort);
                        EndPoints[steamId] = endPoint;
                    }

                    lock (ReceiveLock)
                    {
                        // Discard the *oldest* rather than refusing the newest. A backlog this deep
                        // only happens after the main thread stalled - a level load - and everything
                        // in it is by then several seconds of stale world state that the game would
                        // discard on sequence number anyway. The freshest packet is the useful one.
                        while (Inbound.Count >= MaxQueuedReceives)
                        {
                            ReceivePool.Push(Inbound.Dequeue().Data);
                            _droppedReceives++;
                        }

                        var data = ReceivePool.Count > 0 ? ReceivePool.Pop() : new byte[MaxPacketSize];
                        Buffer.BlockCopy(ReadBuffer, 0, data, 0, (int)read);

                        Inbound.Enqueue(new QueuedReceive
                        {
                            Sender = endPoint,
                            Data = data,
                            Length = (int)read
                        });
                    }

                    queued++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Steam receive pump threw: " + ex);
            }

            if (queued > 0)
                InboundReady.Set();

            if (_droppedReceives > 0)
            {
                Plugin.Log.LogWarning("Dropped " + _droppedReceives
                                      + " inbound packet(s); delivery queue was full.");
                _droppedReceives = 0;
            }
        }

        /// <summary>
        /// Feeds received packets into the game's own demultiplexer, which reads the two-byte header
        /// and routes to the right handler - so dispatch behaves exactly as it would for real UDP.
        ///
        /// This has to be its own thread, and that is not a performance choice. NetworkReceiver
        /// applies backpressure by *blocking the caller* when its message buffer is full, and the
        /// only thing that frees that buffer is UdpNetworkService draining it on the main thread.
        /// The game gets away with this because delivery happens on MultithreadedUdpClient's receive
        /// thread. Deliver from the main thread instead and the first full buffer parks the main
        /// thread waiting for the main thread - an unrecoverable hang, which is exactly what
        /// Windows reported as AppHangB1 the moment gameplay traffic began.
        ///
        /// So we mirror the game's own arrangement: Steam is read on the main thread because
        /// Steamworks demands it, and the game is fed from a thread that is allowed to wait.
        /// </summary>
        private static void DeliveryThread_Execute()
        {
            var args = new object[3];

            while (!_exiting)
            {
                QueuedReceive item;

                lock (ReceiveLock)
                {
                    if (Inbound.Count == 0)
                        item = default(QueuedReceive);
                    else
                        item = Inbound.Dequeue();
                }

                if (item.Data == null)
                {
                    InboundReady.WaitOne(100);
                    continue;
                }

                args[0] = item.Sender;
                args[1] = item.Data;
                args[2] = item.Length;

                try
                {
                    _onReceivePacket.Invoke(_clientManager, args);
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    Plugin.Log.LogError("Delivering P2P packet threw: " + inner);
                }
                finally
                {
                    lock (ReceiveLock)
                        ReceivePool.Push(item.Data);
                }
            }
        }

        // ------------------------------------------------------------------ sessions

        /// <summary>
        /// Authorised against the live Steam lobby rather than our own peer table.
        ///
        /// The table is filled as we learn about players, and a peer that starts sending before we
        /// have got to them would be refused - which is not a recoverable state. Steam raises this
        /// request once per session; there is no retry, so the sender simply waits out
        /// k_EP2PSessionErrorTimeout and the group is abandoned. Sharing a lobby is the same
        /// authorisation the party itself runs on, and it is true the moment they arrive.
        /// </summary>
        private static void OnSessionRequest(P2PSessionRequest_t request)
        {
            var remote = request.m_steamIDRemote;

            if (!PartyBackend.IsLobbyMember(remote.m_SteamID))
            {
                Plugin.Log.LogWarning("Refusing P2P session from " + remote.m_SteamID
                                      + " - not in our lobby.");
                return;
            }

            // They may not be in our table yet; they are now, so replies can be addressed back.
            RegisterPeer(remote.m_SteamID);

            Plugin.Log.LogInfo("Accepting P2P session from " + remote.m_SteamID);
            SteamNetworking.AcceptP2PSessionWithUser(remote);
        }

        private static void OnSessionFailed(P2PSessionConnectFail_t failure)
        {
            Plugin.Log.LogWarning("P2P session with " + failure.m_steamIDRemote.m_SteamID
                                  + " failed: " + (EP2PSessionError)failure.m_eP2PSessionError);
        }

        internal static void Reset()
        {
            lock (PeerLock)
                Peers.Clear();

            lock (SendLock)
            {
                while (Outbound.Count > 0)
                    BufferPool.Push(Outbound.Dequeue().Data);
                _droppedSends = 0;
            }

            lock (ReceiveLock)
            {
                while (Inbound.Count > 0)
                    ReceivePool.Push(Inbound.Dequeue().Data);
                _droppedReceives = 0;
            }
        }

        internal static void Shutdown()
        {
            _exiting = true;
            InboundReady.Set();
            _deliveryThread = null;
        }

        internal static int QueuedSends
        {
            get { lock (SendLock) return Outbound.Count; }
        }

        internal static int QueuedReceives
        {
            get { lock (ReceiveLock) return Inbound.Count; }
        }
    }
}
