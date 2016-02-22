﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
#if NETFX_CORE
using Windows.Networking.Connectivity;
using Windows.Networking;
#endif

namespace FalconUDP
{
    /// <summary>
    /// Represents a FalconUDP peer which can discover, join and commuincate with other 
    /// compatible FalconUDP peers connected to the same network.
    /// </summary>
    public class FalconPeer
    {
        /// <summary>
        /// Maximum FalconUDP datagram size including FalconUDP header. 
        /// </summary>
        /// <remarks>
        /// Try to stay under MTU which is most likely 1500 but we could be encapsulated https://cway.cisco.com/tools/ipsec-overhead-calc/ipsec-overhead-calc.html, 
        /// that said actual datagram size sent will only as big as neccessary so be kind and allow 
        /// the expected upper limit with a potential cost of fragmentation.
        /// </remarks>
        public const int MaxDatagramSize = 1500;

        /// <summary>
        /// Maximum timeout for response from a single ACK. ACK timeout increases each re-send up to this ceiling.
        /// </summary>
        public const float MaxAckTimeoutSeconds = 4.6f;

        //
        // Configurable Settings REMEMBER to update XML doc if change defaults. We favour fields
        // instead of properties for members accessed frequently outside of this class internally 
        // to prevent the method call (perhaps that is too pedantic).
        //
        private byte latencySampleLength = 2;
        private ushort resendRatioSampleLength = 24;
        private int receiveBufferSize = 8192;
        private int sendBufferSize = 8192;
        private TimeSpan addUPnPMappingTimeout = TimeSpan.FromSeconds(3.6);
        internal float AckTimeoutSeconds = 1.0f;
        internal int MaxResends = 5;
        internal int OutOfOrderTolerance = 100;
        internal int MaxNeededOrindalSeq = UInt16.MaxValue + 100; // must be UInt16.MaxValue + OutOfOrderTolerance
        internal float KeepAliveIntervalSeconds = 10.0f;
        internal float KeepAliveProbeAfterSeconds = 0.0f; // Calculated, see UpdateProbeKeepAliveIfNoKeepAlive
        internal float AutoFlushIntervalSeconds = 0.5f;
        internal float PingTimeoutSeconds = 3.0f;
        internal float SimulateLatencySeconds = 0.0f;
        internal float SimulateJitterSeconds = 0.0f;
        internal float quickDisconnectTimeout = 0.0f; // Calculated, see UpdateQuickDiconnectTimeout()
        internal double SimulatePacketLossProbability = 0.0;

        private readonly ProcessReceivedPacket processReceivedPacketDelegate;
        private readonly byte[] receiveBuffer;
        private readonly Dictionary<IPEndPoint, RemotePeer> peersByIp;   // same RemotePeers as peersById
        private readonly Dictionary<int, RemotePeer> peersById;          // same RemotePeers as peersByIp
        private readonly List<AwaitingAcceptDetail> awaitingAcceptDetails;
        private readonly List<Packet> readPacketsList;
        private readonly List<RemotePeer> remotePeersToRemove;
        private readonly Queue<Tuple<IPEndPoint, byte[]>> dummyDatagramsToProcess;
        private readonly GenericObjectPool<EmitDiscoverySignalTask> emitDiscoverySignalTaskPool;
        private readonly GenericObjectPool<PingDetail> pingPool;
        private readonly List<EmitDiscoverySignalTask> discoveryTasks;
        private readonly List<Guid> onlyReplyToDiscoveryRequestsWithToken;
        private readonly RemotePeer unknownPeer;                         // peer re-used to send unsolicited messages to
        private readonly DatagramPool sendDatagramsPool;

        private int port;
        private IPEndPoint anyAddrEndPoint;
        private int peerIdCount;
        private string joinPass;
        private PunchThroughCallback punchThroughCallback;
        private bool stopped;
        private bool acceptJoinRequests;
        private bool replyToAnonymousPings;
        private float ellapsedSecondsAtLastUpdate;
        private bool replyToAnyDiscoveryRequests;                       // i.e. reply unconditionally with or without a token
        private List<IPEndPoint> broadcastEndPoints;
        private AddUPnPPortMappingCallback addUPnPMappingCallback;
        private TimeSpan addUPnPEllapsedAtStart;
        private bool upnpMappingAdded;
        private UPnPInternetGatewayDevice upnpDevice;


#if DEBUG
        private LogLevel logLvl;
        private LogCallback logger;
#endif
        internal readonly Stopwatch Stopwatch;
        internal readonly PacketPool PacketPool;
#if NETFX_CORE
        internal readonly List<HostName> LocalAddresses;
#else
        internal readonly HashSet<IPAddress> LocalAddresses;
#endif
        internal readonly List<PingDetail> PingsAwaitingPong;
        internal readonly FalconPoolSizes PoolSizes;
        internal static readonly Encoding TextEncoding = Encoding.UTF8;
        internal IFalconTransceiver Transceiver;

        internal bool IsCollectingStatistics { get { return Statistics != null; } }
        internal bool HasPingsAwaitingPong { get { return PingsAwaitingPong.Count > 0; } }
        internal static int MaxPayloadSize { get { return MaxDatagramSize - Const.FALCON_PACKET_HEADER_SIZE; } }

        /// <summary>
        /// Event raised when another FalconUDP peer joined.
        /// </summary>
        /// <remarks>A peer can be added when either this FalconPeer successfully joins another, or
        /// another peer joins this one. Use the id in <see cref="PeerAdded"/> to send data to the 
        /// new peer.</remarks>
        public event PeerAdded PeerAdded;

        /// <summary>
        /// Event raised when another FalconUDP peer leaves us.
        /// </summary>
        /// <remarks>This event is not raised when we leave other peers. The dropped peer could 
        /// have left some time ago and failed to notify us, e.g. if their network cable was
        /// unplugged. </remarks>
        public event PeerDropped PeerDropped;

        /// <summary>
        /// Event raised during a discovery operation started by calling either <see cref="DiscoverFalconPeersAsync(TimeSpan, int, Guid?, DiscoveryCallback, int)"/>
        /// or <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint}, TimeSpan, int, Guid?, PunchThroughCallback)"/>
        /// </summary>
        /// <remarks>This event is raised as soon as reply is received from a discovery request. The callback to <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint}, TimeSpan, int, Guid?, PunchThroughCallback)"/>
        /// will have the details of any other peers discovered. The callback to <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint}, TimeSpan, int, Guid?, PunchThroughCallback)"/>
        /// will have the details of the first peer that responded which will be the same as the details in this event.</remarks>
        public event PeerDiscovered PeerDiscovered;

        /// <summary>
        /// Event raised when Pong received in reply to a Ping sent to a known Peer using <see cref="PingPeer(int)"/>.
        /// </summary>
        public event PongReceivedFromPeer PongReceivedFromPeer;

        /// <summary>
        /// Event raised when Pong received in reply to a Ping sent to a known or unknown Peer using <see cref="PingEndPoint(IPEndPoint)"/>.
        /// </summary>
        public event PongReceivedFromUnknownPeer PongReceivedFromUnknownPeer;

        /// <summary>
        /// Port this FalconPeer is or will be listening on.
        /// </summary>
        public int Port
        {
            get { return port; }
            private set
            {
                port = value;
#if NETFX_CORE
                PortAsString = port.ToString();
#endif
            }
        }

#if NETFX_CORE
        public string PortAsString { get; private set; }
#endif

        /// <summary>
        /// <see cref="Statistics"/> structure containing total bytes sent and recieved in the last second.
        /// </summary>
        public Statistics Statistics { get; private set; }

        /// <summary>
        /// Gets whether this <see cref="FalconPeer"/> is started.
        /// </summary>
        public bool IsStarted { get { return !stopped; } }

        /// <summary>
        /// Time after which to to re-send a reliable message if not ACKnowledged within.
        /// </summary>
        /// <remarks>Default 1.2 seconds.</remarks>
        public TimeSpan AckTimeout
        {
            get
            {
                return TimeSpan.FromSeconds(AckTimeoutSeconds);
            }
            set
            {
                CheckNotStarted();
                float seconds = (float)value.TotalSeconds;
                if (seconds <= 0.0f)
                    throw new ArgumentOutOfRangeException("value", "must be greater than 0");
                AckTimeoutSeconds = seconds;
                UpdateProbeKeepAliveIfNoKeepAlive();
                UpdateQuickDisconnectTimeout();
            }
        }

        /// <summary>
        /// Maximum number of times to re-send an unACKnowledged message before giving up.
        /// </summary>
        /// <remarks>Defaults to 5.</remarks>
        public int MaxMessageResends
        {
            get
            {
                return MaxResends;
            }
            set
            {
                CheckNotStarted();
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 0");
                MaxResends = value;
                UpdateProbeKeepAliveIfNoKeepAlive();
                UpdateQuickDisconnectTimeout();
            }
        }

        /// <summary>
        /// Messages received out-of-order from last received greater than this are dropped 
        /// indiscrimintly.
        /// </summary>
        /// <remarks>Defaults 100</remarks>
        public int MaxOutOfOrderTolerence
        {
            get { return OutOfOrderTolerance; }
            set
            {
                CheckNotStarted();
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 0");
                OutOfOrderTolerance = value;
                MaxNeededOrindalSeq = UInt16.MaxValue + value;
            }
        }

        /// <summary>
        /// The number of most recent round-trip-times (from sending reliable message till 
        /// receiving ACKnowledgment) to each peer used in the <see cref="QualityOfService.RoudTripTime"/> 
        /// calculation.
        /// </summary>
        /// <remarks>Default 2.</remarks>
        public byte LatencySampleSize
        {
            get { return latencySampleLength; }
            set
            {
                CheckNotStarted();
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 1");
                latencySampleLength = value;
            }
        }

        /// <summary>
        /// The number of most recent reliable messages that had to be re-sent or not to each peer 
        /// used in the <see cref="QualityOfService.ResendRatio"/> calculation.
        /// </summary>
        /// <remarks>Default 24.</remarks>
        public ushort ResendRatioSampleSize
        {
            get { return resendRatioSampleLength; }
            set
            {
                CheckNotStarted();
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 1");
                resendRatioSampleLength = value;
            }
        }

        /// <summary>
        /// The time span after which to send Falcon KeepAlive's to a remote peer if no reliable 
        /// message sent or received from the peer.
        /// </summary>
        /// <remarks>
        /// <para>Defaults 10.0 seconds.</para> 
        /// <para>
        /// KeepAlive's help determine dropped peers that did not properly "disconnect" (i.e. say 
        /// Bye), update latency estimates and prevent stateful nodes timing-out our route they 
        /// may hold to a remote peer (e.g. when traversed EIM NAT).</para>
        /// <para>
        /// Note: Only the peer which accpeted the connection sends KeepAlives to reduce bandwidth
        /// (the KeepAlive master). If no KeepAlive is received from the KeepAlive master for a 
        /// while the joinee peer (not the KeepAlive master) will send one (probe) to see if the 
        /// KeepAlive master is still alive!</para>
        /// </remarks>
        public TimeSpan KeepAliveInterval
        {
            get { return TimeSpan.FromSeconds(KeepAliveIntervalSeconds); }
            set
            {
                CheckNotStarted();
                float seconds = (float)value.TotalSeconds;
                if (seconds <= 0.0f)
                    throw new ArgumentOutOfRangeException("value", "must be greater than 0");
                KeepAliveIntervalSeconds = seconds;
                UpdateProbeKeepAliveIfNoKeepAlive();
            }
        }

        /// <summary>
        /// Send enqued packets if the application has not done so for time span.
        /// </summary>
        /// <remarks>Defaults to 0.5 seconds.
        /// 
        /// Set to 0 to disable auto flushing.
        /// 
        /// Note: this is called in a call to Update() so will not flush send queues 
        /// automatically, i.e. Update() still has to be called.</remarks>
        public TimeSpan AutoFlushInterval
        {
            get { return TimeSpan.FromSeconds(AutoFlushIntervalSeconds); }
            set
            {
                CheckNotStarted();
                float seconds = (float)value.TotalSeconds;
                if (seconds < 0.0f)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 0");
                AutoFlushIntervalSeconds = seconds;
            }
        }

        /// <summary>
        /// Size of the receive buffer in bytes for the single Socket this peer will use.
        /// </summary>
        /// <remarks>Default is 8192 (i.e. 8 KB)</remarks>
        public int ReceiveBufferSize
        {
            get { return receiveBufferSize; }
            set
            {
                CheckNotStarted();
                if (value <= 0)
                    throw new ArgumentOutOfRangeException("value", "must be greater than 0");
                receiveBufferSize = value;
            }
        }

        /// <summary>
        /// Size of the send buffer in bytes for the single Socket this peer will use.
        /// </summary>
        /// <remarks>Default is 8192 (i.e. 8 KB)</remarks>
        public int SendBufferSize
        {
            get { return sendBufferSize; }
            set
            {
                CheckNotStarted();
                if (value <= 0)
                    throw new ArgumentOutOfRangeException("value", "must be greater than 0");
                sendBufferSize = value;
            }
        }

        /// <summary>
        /// Time span after which to stop listening for a reply Pong to a Ping this peer sent.
        /// </summary>
        /// <remarks>Defaults to 2 seconds.</remarks>
        public TimeSpan PingTimeout
        {
            get { return TimeSpan.FromSeconds(PingTimeoutSeconds); }
            set
            {
                CheckNotStarted();
                float seconds = (float)value.TotalSeconds;
                if (seconds <= 0.0f)
                    throw new ArgumentOutOfRangeException("value", "must be greater than 0");
                PingTimeoutSeconds = seconds;
            }
        }

        /// <summary>
        /// Get or sets period to delay outgoing sends from when they otherwise would be sent.
        /// </summary>
        /// <remarks>Set to 0 (the default) to disable delaying.</remarks>
        public TimeSpan SimulateDelayTimeSpan
        {
            get { return TimeSpan.FromSeconds(SimulateLatencySeconds); }
            set
            {
                float seconds = (float)value.TotalSeconds;
                if (seconds < 0.0f)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 0");
                SimulateLatencySeconds = seconds;
            }
        }

        /// <summary>
        /// Gets or sets maximum period up to this value (inclusive) to add or subtract from 
        /// <see cref="SimulateDelayTimeSpan"/> when delaying sends that are about to be sent.
        /// </summary>
        /// <remarks>Set to 0 (the default) to disable. <see cref="SimulateDelayTimeSpan"/> must 
        /// be set before this and this value cannot be greater than <see cref="SimulateDelayTimeSpan"/>.</remarks>
        public TimeSpan SimulateDelayJitterTimeSpan
        {
            get { return TimeSpan.FromSeconds(SimulateJitterSeconds); }
            set
            {
                float seconds = (float)value.TotalSeconds;
                if (seconds < 0.0f)
                    throw new ArgumentOutOfRangeException("value", "cannot be less than 0");
                if (seconds > SimulateLatencySeconds)
                    throw new ArgumentOutOfRangeException("value", "cannot be greater than SimulateDelayTimeSpan");
                SimulateJitterSeconds = seconds;
            }
        }

        /// <summary>
        /// Get or sets probability outgoing sends will be silently dropped to simulate poor 
        /// network conditions. 0.0 no sends will be dropped, 1.0 all sends will be dropped.
        /// </summary>
        public double SimulatePacketLossChance
        {
            get { return SimulatePacketLossProbability; }
            set
            {
                if (value > 1.0 || value < 0.0)
                    throw new ArgumentOutOfRangeException("value", "must be between 0.0 and 1.0 inclusive");
                SimulatePacketLossProbability = value;
            }
        }

#if DEBUG
        /// <summary>
        /// Creates a new FalconPeer.
        /// </summary>
        /// <param name="port">Port to listen on.</param>
        /// <param name="processReceivedPacketDelegate">Callback invoked when 
        /// <see cref="ProcessReceivedPackets()"/> called for each packet received.</param>
        /// <param name="poolSizes">Numbers of objects this FalconPeer should pre-allocate</param>
        /// <param name="logCallback">Callback to use for logging, if not supplied logs written to Debug.</param>
        /// <param name="logLevel">Severtiy level and more serious levels which to log.</param>
        public FalconPeer(int port,
            ProcessReceivedPacket processReceivedPacketDelegate,
            FalconPoolSizes poolSizes,
            LogCallback logCallback = null,
            LogLevel logLevel = LogLevel.Warning)
#else
        /// <summary>
        /// Creates a new FalconPeer.
        /// </summary>
        /// <param name="port">Port to listen on.</param>
        /// <param name="processReceivedPacketDelegate">Callback invoked when 
        /// <param name="poolSizes">Numbers of objects this FalconPeer should pre-allocate</param>
        /// <see cref="ProcessReceivedPackets()"/> called for each packet received.</param>
        public FalconPeer(int port, ProcessReceivedPacket processReceivedPacketDelegate, FalconPoolSizes poolSizes)
#endif
        {
            if (!BitConverter.IsLittleEndian)
                new PlatformNotSupportedException("CPU architecture not supported: Big Endian reading and writing to and from FalconUDP packets has not been implemented.");

            this.Port = port;
            this.processReceivedPacketDelegate = processReceivedPacketDelegate;
            this.peersByIp = new Dictionary<IPEndPoint, RemotePeer>();
            this.peersById = new Dictionary<int, RemotePeer>();
#if NETFX_CORE
            this.LocalAddresses = new List<HostName>();
#else
            this.anyAddrEndPoint = new IPEndPoint(IPAddress.Any, port);
            this.LocalAddresses = new HashSet<IPAddress>();
#endif
            this.peerIdCount = 0;
            this.awaitingAcceptDetails = new List<AwaitingAcceptDetail>();
            this.acceptJoinRequests = false;
            this.PingsAwaitingPong = new List<PingDetail>();
            this.receiveBuffer = new byte[MaxDatagramSize];
            this.readPacketsList = new List<Packet>();
            this.stopped = true;
            this.remotePeersToRemove = new List<RemotePeer>();
            this.dummyDatagramsToProcess = new Queue<Tuple<IPEndPoint, byte[]>>();
            this.Stopwatch = new Stopwatch();

            // pools
            this.PoolSizes = poolSizes;
            this.PacketPool = new PacketPool(MaxPayloadSize, poolSizes.InitalNumPacketsToPool);
            this.emitDiscoverySignalTaskPool = new GenericObjectPool<EmitDiscoverySignalTask>(poolSizes.InitalNumEmitDiscoverySignalTaskToPool);
            this.pingPool = new GenericObjectPool<PingDetail>(poolSizes.InitalNumPingsToPool);
            this.sendDatagramsPool = new DatagramPool(MaxDatagramSize, poolSizes.InitalNumSendDatagramsToPoolPerPeer);

            // discovery
            this.discoveryTasks = new List<EmitDiscoverySignalTask>();
            this.onlyReplyToDiscoveryRequestsWithToken = new List<Guid>();

            // helper
#if NETFX_CORE
            this.unknownPeer = new RemotePeer(this, new IPEndPoint("127.0.0.1", this.Port.ToString()), 0, false);
#else
            this.unknownPeer = new RemotePeer(this, new IPEndPoint(IPAddress.Broadcast, this.Port), 0, false);
#endif

#if DEBUG
            // log
            this.logLvl = logLevel;
            if (logCallback != null)
            {
                logger = logCallback;
            }
            else
            {
#if !NETFX_CORE
                Debug.AutoFlush = true;
#endif
            }
            Log(LogLevel.Info, "Initialized");
#endif

            // calculated vars
            UpdateProbeKeepAliveIfNoKeepAlive();
            UpdateQuickDisconnectTimeout();
        }

        private void CheckStarted()
        {
            if (stopped)
                throw new InvalidOperationException("FalconPeer is not started!");
        }

        private void CheckNotStarted()
        {
            if (!stopped)
                throw new InvalidOperationException("FalconPeer already started!");
        }

        private void UpdateProbeKeepAliveIfNoKeepAlive()
        {
            // Updates the amount of time peer who is not the KeepAlive master should send a 
            // KeepAlive probe after not receiving any reliable message from the KeepAlive master 
            // to see if the master is still alive!

            // Calculate to be:
            //
            //      KeepAliveInterval + 3 x re-sends + a bit
            //
            KeepAliveProbeAfterSeconds = KeepAliveIntervalSeconds +
                AckTimeoutSeconds +
                AckTimeoutSeconds * 2 +
                AckTimeoutSeconds * 3 +
                0.5f;
        }

        private void UpdateQuickDisconnectTimeout()
        {
            // If time between calls to Update exceeds this, drop all peers rather then waiting to 
            // learn they would have dropped us (becuase we know they would dropped us as we could 
            // not have responded).

            float total = 0.0f;
            for (int i = 1; i <= MaxResends; i++)
            {
                total += Math.Min(AckTimeoutSeconds * i, MaxAckTimeoutSeconds);
            }
            quickDisconnectTimeout = total;
        }

        private void ProcessReceivedPackets()
        {
            // clear the list of previously read packets
            readPacketsList.Clear();

            // move received packets ready for reading from remote peers into readPacketList
            foreach (RemotePeer rp in peersById.Values)
            {
                if (rp.UnreadPacketCount > 0)
                {
                    readPacketsList.AddRange(rp.Read());
                }
            }

            // for each packet call the process received packet delegate then return it to the pool
            foreach (Packet p in readPacketsList)
            {
                try
                {
                    //-------------------------------
                    processReceivedPacketDelegate(p);
                    //-------------------------------
                }
                finally
                {
                    PacketPool.Return(p);
                }
            }
        }

        private void Update(float dt)
        {
            // peers to remove
            if (remotePeersToRemove.Count > 0)
            {
                for (int i = 0; i < remotePeersToRemove.Count; i++)
                {
                    RemovePeer(remotePeersToRemove[i], false);
                }
                remotePeersToRemove.Clear();
            }

            // dummy datagrams
            while (dummyDatagramsToProcess.Count > 0)
            {
                Tuple<IPEndPoint, byte[]> datagram = dummyDatagramsToProcess.Dequeue();
                ProcessReceivedDatagram(datagram.Item1, datagram.Item2, datagram.Item2.Length);
            }

            // read received datagrams
            while (Transceiver.BytesAvaliable > 0)
            {
                IPEndPoint fromIPEndPoint = anyAddrEndPoint;

                int size = Transceiver.Read(receiveBuffer, ref fromIPEndPoint);

                Log(LogLevel.Debug, String.Format("Received {0} bytes from: {1}", size, (IPEndPoint)fromIPEndPoint));

                if (size == 0) // i.e. failure
                {
                    TryRemovePeer((IPEndPoint)fromIPEndPoint, false, false);
                }
                else
                {
                    if (IsCollectingStatistics)
                    {
                        Statistics.AddBytesReceived(size);
                    }

                    // detect if we are waiting for an UPnP response this is it
                    if (addUPnPMappingCallback != null
                        && size > 4
                        && receiveBuffer[0] == 'H'
                        && receiveBuffer[1] == 'T'
                        && receiveBuffer[2] == 'T'
                        && receiveBuffer[3] == 'P')
                    {
                        ProcessUPnPBroadcastResponse((IPEndPoint)fromIPEndPoint, receiveBuffer, size);
                    }
                    else
                    {
                        ProcessReceivedDatagram((IPEndPoint)fromIPEndPoint, receiveBuffer, size);
                    }
                }
            }

            // pings awaiting pong 
            if (PingsAwaitingPong.Count > 0)
            {
                // NOTE: This must be done before processing recieved packets and updating remote 
                //       peers so times updated.

                for (int i = 0; i < PingsAwaitingPong.Count; i++)
                {
                    PingDetail detail = PingsAwaitingPong[i];
                    detail.EllapsedSeconds += dt;
                    if (detail.EllapsedSeconds > PingTimeoutSeconds)
                    {
                        PingsAwaitingPong.RemoveAt(i);
                        --i;
                        pingPool.Return(detail);
                    }
                }
            }

            // process received packets
            ProcessReceivedPackets();

            // unknown peer
            unknownPeer.Update(dt);

            // remote peers
            foreach (RemotePeer rp in peersById.Values)
            {
                rp.Update(dt);
            }

            // stats
            if (IsCollectingStatistics)
            {
                Statistics.Update(dt);
            }

            // discovery
            if (discoveryTasks.Count > 0)
            {
                for (int i = 0; i < discoveryTasks.Count; i++)
                {
                    EmitDiscoverySignalTask task = discoveryTasks[i];
                    task.Update(dt);
                    if (task.TaskEnded)
                    {
                        discoveryTasks.RemoveAt(i);
                        i--;
                        emitDiscoverySignalTaskPool.Return(task);
                    }
                }
            }

            // awaiting accept details
            if (awaitingAcceptDetails.Count > 0)
            {
                for (int i = 0; i < awaitingAcceptDetails.Count; i++)
                {
                    AwaitingAcceptDetail aad = awaitingAcceptDetails[i];
                    aad.EllapsedSecondsSinceStart += dt;
                    float acceptTimeout = GetAckTimeout(aad.RetryCount);

                    if (aad.EllapsedSecondsSinceStart >= acceptTimeout)
                    {
                        if (aad.RetryCount < MaxResends)
                        {
                            // try again
                            TryJoinPeerAsync(aad);
                            aad.EllapsedSecondsSinceStart = 0.0f;
                            aad.RetryCount++;
                        }
                        else
                        {
                            // give up, peer has not been added yet so no need to drop
                            awaitingAcceptDetails.RemoveAt(i);
                            i--;
                            if (aad.Callback != null)
                            {
                                aad.Callback(new FalconOperationResult<int>(false, "Remote peer never responded to join request.", -1));
                            }
                            if (aad.UserDataPacket != null)
                            {
                                ReturnPacketToPool(aad.UserDataPacket);
                            }
                        }
                    }
                }
            }
        }

        private void SendToUnknownPeer(IPEndPoint ep, PacketType type, SendOptions opts, byte[] payload)
        {
            Debug.Assert((opts & SendOptions.Reliable) != SendOptions.Reliable, "cannot send reliable messages to unknown peer");

            Packet p = PacketPool.Borrow();
            p.WriteBytes(payload);
            unknownPeer.UpdateEndPoint(ep);
            unknownPeer.EnqueueSend(type, opts, p);

            // Flushes queue immediatly in case another packet to send before user-application 
            // gets around to flushing send queues.

            unknownPeer.ForceFlushSendChannelNow(opts);
        }

        private bool TryGetAndRemoveWaitingAcceptDetail(IPEndPoint ep, out AwaitingAcceptDetail detail)
        {
            detail = awaitingAcceptDetails.Find(aad => aad.EndPoint.FastEquals(ep));
            if (detail != null)
            {
                awaitingAcceptDetails.Remove(detail);
                return true;
            }
            return false;
        }

        private void DiscoverFalconPeersAsync(bool listenForReply,
            TimeSpan timeSpan,
            int numOfRequests,
            int maxPeersToDiscover,
            IEnumerable<IPEndPoint> endPoints,
            Guid? token,
            DiscoveryCallback callback)
        {
            EmitDiscoverySignalTask task = emitDiscoverySignalTaskPool.Borrow();
            task.Init(this, listenForReply, (float)timeSpan.TotalSeconds, numOfRequests, maxPeersToDiscover, endPoints, token, callback);
            task.EmitDiscoverySignal(); // emit first signal now
            discoveryTasks.Add(task);
        }

        internal void TryRemovePeer(IPEndPoint ip, bool logFailure, bool sayBye)
        {
            RemotePeer rp;
            if (!peersByIp.TryGetValue(ip, out rp))
            {
                if (logFailure)
                {
                    Log(LogLevel.Error, String.Format("Failed to remove peer: {0}, peer unknown.", ip));
                }
            }
            else
            {
                RemovePeer(rp, sayBye);
            }
        }

        private void TryJoinPeerAsync(AwaitingAcceptDetail detail)
        {
            SendToUnknownPeer(detail.EndPoint, PacketType.JoinRequest, SendOptions.None, detail.JoinData);
        }

        // ASSUMPTION: caller checked operation is in progress by checking callback is not null
        private void EndAddUPnPMapping(AddUPnPMappingResult result)
        {
            Log(LogLevel.Info, "EndAddUPnPMapping, result: " + result.ToString());
            upnpMappingAdded = result == AddUPnPMappingResult.Success;
            AddUPnPPortMappingCallback callback = addUPnPMappingCallback;
            addUPnPMappingCallback = null;
            addUPnPMappingCallback(result);
        }
        
        private void ProcessUPnPBroadcastResponse(IPEndPoint fromIPEndPoint, byte[] buffer, int size)
        {
            // An actual response for reference: HTTP/1.1 200 OK\r\nCACHE-CONTROL:max-age=1800\r\nEXT:\r\nLOCATION:http://10.0.0.138:80/upnp/IGD.xml\r\nSERVER:Thomson TG 782T 8.6.P.3 UPnP/1.0 (00-24-17-D1-37-43)\r\nST:upnp:rootdevice\r\nUSN:uuid:UPnP_Thomson TG782T-1_00-24-17-D1-37-43::upnp:rootdevice\r\n\r\n

            if (size < 5)
                return;
            if (addUPnPMappingCallback == null)
                return;

            // parse response for location of xml containing services
            string resp = Encoding.ASCII.GetString(buffer, 0, size);
            resp = resp.ToLower();
            Log(LogLevel.Info, "Processing UPnP broadcase response: " + resp);
            if (!(resp.Contains("upnp:rootdevice") && resp.Contains("location:")))
                return;
            string location = resp.Substring(resp.IndexOf("location:") + 9);
            location = location.Substring(0, resp.IndexOf("\r"));
            Uri locationUri = new Uri(location);

            // try create UPnPDevice which attmpts to download service url from location
            UPnPInternetGatewayDevice.BeginCreate(locationUri, device => 
            {
                if (addUPnPMappingCallback == null)
                    return;

                if (upnpDevice == null)
                {
                    EndAddUPnPMapping(AddUPnPMappingResult.FailedOther);
                }
                else
                {
                    upnpDevice = device;

                    // add forwarding rules for our the local addresse(s)
                    foreach (var addr in LocalAddresses)
                    {
                        if (IPAddress.IsLoopback(addr))
                            continue;
                        if (upnpDevice.TryAddForwardingRule(System.Net.Sockets.ProtocolType.Udp, addr, (ushort)Port, "FalconUDP" + Port.ToString()))
                        {
                            upnpMappingAdded = true;
                        }
                    }
                    EndAddUPnPMapping(upnpMappingAdded ? AddUPnPMappingResult.Success : AddUPnPMappingResult.FailedOther);
                }
            });

        }

        private void ProcessReceivedDatagram(IPEndPoint fromIPEndPoint, byte[] buffer, int size)
        {
            // check size
            if (size < Const.FALCON_PACKET_HEADER_SIZE)
            {
                Log(LogLevel.Error, String.Format("Datagram dropped from: {0}, smaller than min size.", fromIPEndPoint));
                return;
            }

            if (size > MaxDatagramSize)
            {
                Log(LogLevel.Error, String.Format("Datagram dropped from: {0}, greater than max size.", fromIPEndPoint));
                return;
            }

            // check not from self
#if NETFX_CORE
            if ((fromIPEndPoint.Address.RawName.StartsWith("127.") || LocalAddresses.Exists(hn => hn.IsEqual(fromIPEndPoint.Address)))
                && fromIPEndPoint.PortAsString == PortAsString)
#else
            if ((IPAddress.IsLoopback(fromIPEndPoint.Address) || LocalAddresses.Contains(fromIPEndPoint.Address))
                && fromIPEndPoint.Port == Port)
#endif
            {
                Log(LogLevel.Warning, "Dropped datagram received from self.");
                return;
            }


            // parse header
            byte packetDetail = buffer[0];
            SendOptions opts = (SendOptions)(byte)(packetDetail & Const.SEND_OPTS_MASK);
            PacketType type = (PacketType)(byte)(packetDetail & Const.PACKET_TYPE_MASK);
            bool isAckPacket = type == PacketType.ACK;
            ushort seq = BitConverter.ToUInt16(buffer, 1);
            ushort payloadSize = BitConverter.ToUInt16(buffer, 3);

            // check the header makes sense (anyone could send us UDP datagrams)
            if (!(opts == SendOptions.None || opts == SendOptions.InOrder || opts == SendOptions.Reliable || opts == SendOptions.ReliableInOrder)
                || (isAckPacket && ((opts & SendOptions.Reliable) != SendOptions.Reliable)))
            {
                Log(LogLevel.Warning, String.Format("Datagram dropped from peer: {0}, bad header.", fromIPEndPoint));
                return;
            }

            if (!isAckPacket && size < (payloadSize + Const.FALCON_PACKET_HEADER_SIZE))
            {
                Log(LogLevel.Warning, String.Format("Datagram dropped from peer: {0}, size: {1}, less than min purported: {2}.",
                    fromIPEndPoint,
                    size,
                    payloadSize + Const.FALCON_PACKET_HEADER_SIZE));
                return;
            }

            int count = size - Const.FALCON_PACKET_HEADER_SIZE;    // num of bytes remaining to be read
            int index = Const.FALCON_PACKET_HEADER_SIZE;           // index in args.Buffer to read from

            Log(LogLevel.Debug, String.Format("<- Processing received datagram seq {0}, channel: {1}, total size: {2}...", seq.ToString(), opts.ToString(), size.ToString()));

            RemotePeer rp;
            if (peersByIp.TryGetValue(fromIPEndPoint, out rp))
            {
                bool isFirstPacketInDatagram = true;
                do
                {
                    Log(LogLevel.Debug, String.Format("<-- Processing received packet type {0}, payload size: {1}...", type.ToString(), payloadSize.ToString()));

                    if (!rp.TryAddReceivedPacket(seq,
                        opts,
                        type,
                        buffer,
                        index,
                        payloadSize,
                        isFirstPacketInDatagram))
                    {
                        break;
                    }

                    // process any additional packets in datagram

                    if (!isAckPacket) // payloadSize is stopover time in ACKs
                    {
                        count -= payloadSize;
                        index += payloadSize;
                    }

                    if (count >= Const.ADDITIONAL_PACKET_HEADER_SIZE)
                    {
                        // parse additional packet header
                        packetDetail = buffer[index];
                        type = (PacketType)(packetDetail & Const.PACKET_TYPE_MASK);
                        isAckPacket = type == PacketType.ACK;
                        if (isAckPacket)
                        {
                            opts = (SendOptions)(packetDetail & Const.SEND_OPTS_MASK);
                            seq = BitConverter.ToUInt16(buffer, index + 1);
                            payloadSize = BitConverter.ToUInt16(buffer, index + 3);
                            index += Const.FALCON_PACKET_HEADER_SIZE;
                            count -= Const.FALCON_PACKET_HEADER_SIZE;
                        }
                        else
                        {
                            payloadSize = BitConverter.ToUInt16(buffer, index + 1);
                            index += Const.ADDITIONAL_PACKET_HEADER_SIZE;
                            count -= Const.ADDITIONAL_PACKET_HEADER_SIZE;

                            // validate size
                            if (payloadSize > count)
                            {
                                Log(LogLevel.Error, String.Format("Dropped last {0} bytes of datagram from {1}, less than purported packet size: {2}.",
                                    count.ToString(),
                                    fromIPEndPoint.ToString(),
                                    payloadSize.ToString()));
                                return;
                            }
                        }
                    }
                    else
                    {
                        return;
                    }

                    isFirstPacketInDatagram = false;

                } while (true);
            }
            else
            {
                Log(LogLevel.Debug, String.Format("<-- Processing received packet type {0}, payload size: {1}...", type.ToString(), payloadSize.ToString()));

                #region "Proccess datagram from unknown peer"

                // NOTE: Additional packets not possible in any of the valid messages from an 
                //       unknown peer.

                switch (type)
                {
                    case PacketType.JoinRequest:
                        {
                            if (!acceptJoinRequests)
                            {
                                Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, not accepting join requests.", fromIPEndPoint));
                                return;
                            }

                            if (payloadSize == 0)
                            {
                                Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, 0 payload size.", fromIPEndPoint));
                                return;
                            }

                            string pass = null;
                            byte passSize = buffer[index];
                            index++;
                            count--;
                            if (passSize > 0)
                            {
                                if (count < passSize)
                                {
                                    Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, has pass size {1} but remaining size is {2}.", fromIPEndPoint, passSize, count));
                                    return;
                                }
                                pass = TextEncoding.GetString(buffer, index, passSize);
                                index += passSize;
                                count -= passSize;
                            }

                            if (pass != joinPass)
                            {
                                Log(LogLevel.Warning, String.Format("Join request from: {0} dropped, bad pass.", fromIPEndPoint));
                            }
                            else
                            {
                                Log(LogLevel.Info, String.Format("Accepted Join Request from: {0}", fromIPEndPoint));

                                // If any remaining bytes included in payload they are for the user-application.
                                Packet joinUserData = null;
                                if (count > 0)
                                {
                                    joinUserData = PacketPool.Borrow();
                                    joinUserData.WriteBytes(buffer, index, count);
                                    count = 0;
                                    index += count;
                                    joinUserData.ResetAndMakeReadOnly(-1);
                                }

                                rp = AddPeer(fromIPEndPoint, joinUserData);
                                rp.Accept();
                            }
                        }
                        break;
                    case PacketType.AcceptJoin:
                        {
                            AwaitingAcceptDetail detail;
                            if (!TryGetAndRemoveWaitingAcceptDetail(fromIPEndPoint, out detail))
                            {
                                // Possible reasons we do not have detail are: 
                                //  1) Accept is too late,
                                //  2) Accept duplicated and we have already removed it, or
                                //  3) Accept was unsolicited.

                                Log(LogLevel.Warning, String.Format("Dropped Accept Packet from unknown peer: {0}.", fromIPEndPoint));
                            }
                            else
                            {
                                Log(LogLevel.Info, String.Format("Successfully joined: {0}", fromIPEndPoint));

                                // create the new peer, send ACK, call the callback
                                rp = AddPeer(fromIPEndPoint, detail.UserDataPacket);
                                rp.ACK(seq, opts);
                                rp.IsKeepAliveMaster = true; // the acceptor is the keep-alive-master
                                if (detail.Callback != null)
                                {
                                    FalconOperationResult<int> result = new FalconOperationResult<int>(true, null, null, rp.Id);
                                    detail.Callback(result);
                                }
                            }
                        }
                        break;
                    case PacketType.DiscoverRequest:
                        {
                            bool reply = false;

                            if (replyToAnyDiscoveryRequests)
                            {
                                reply = true;
                            }
                            else if (onlyReplyToDiscoveryRequestsWithToken.Count > 0 && count == Const.DISCOVERY_TOKEN_SIZE)
                            {
                                byte[] tokenBytes = new byte[Const.DISCOVERY_TOKEN_SIZE];
                                Buffer.BlockCopy(buffer, index, tokenBytes, 0, Const.DISCOVERY_TOKEN_SIZE);
                                Guid token = new Guid(tokenBytes);

                                if (onlyReplyToDiscoveryRequestsWithToken.Contains(token))
                                    reply = true;
                            }

                            if (reply)
                            {
                                Log(LogLevel.Info, String.Format("Received Discovery Request from: {0}, sending discovery reply...", fromIPEndPoint));
                                SendToUnknownPeer(fromIPEndPoint, PacketType.DiscoverReply, SendOptions.None, null);
                            }
                            else
                            {
                                Log(LogLevel.Info, String.Format("Received Discovery Request from: {0}, dropped - invalid token and set to not reply.", fromIPEndPoint));
                            }
                        }
                        break;
                    case PacketType.DiscoverReply:
                        {
                            // ASSUMPTION: There can only be one EmitDiscoverySignalTask at any one time that 
                            //             matches (inc. broadcast addresses) any one discovery reply.

                            foreach (EmitDiscoverySignalTask task in discoveryTasks)
                            {
                                if (task.IsAwaitingDiscoveryReply && task.IsForDiscoveryReply(fromIPEndPoint))
                                {
                                    task.AddDiscoveryReply(fromIPEndPoint);
                                    Log(LogLevel.Info, String.Format("Received Discovery Reply from: {0}", fromIPEndPoint));
                                    break;
                                }
                            }
                        }
                        break;
                    case PacketType.Ping:
                        {
                            if (!replyToAnonymousPings)
                                return;

                            SendToUnknownPeer(fromIPEndPoint, PacketType.Pong, SendOptions.None, null);
                        }
                        break;
                    case PacketType.Pong:
                        {
                            if (HasPingsAwaitingPong)
                            {
                                PingDetail detail = PingsAwaitingPong.Find(pd => pd.IPEndPointPingSentTo != null
                                    && pd.IPEndPointPingSentTo.FastEquals(fromIPEndPoint));

                                if (detail != null)
                                {
                                    RaisePongReceivedFromUnknownPeer(fromIPEndPoint, TimeSpan.FromSeconds(detail.EllapsedSeconds));
                                    RemovePingAwaitingPongDetail(detail);
                                }
                            }
                        }
                        break;
                    default:
                        {
                            Log(LogLevel.Warning, String.Format("{0} Datagram dropped from unknown peer: {1}.", type, fromIPEndPoint));
                        }
                        break;
                }
                #endregion
            }
        }

        private void PunchThroughDiscoveryCallback(IPEndPoint[] endPoints)
        {
            if (punchThroughCallback == null)
                return;

            if (endPoints != null && endPoints.Length > 0)
            {
                punchThroughCallback(true, endPoints[0]);
            }
            else
            {
                punchThroughCallback(false, null);
            }
        }

        private void RemovePeer(RemotePeer rp, bool sayBye)
        {
            if (sayBye)
            {
                // Enqueue Bye and flush send channels so Bye will be last packet peer receives and 
                // any outstanding sends are sent too.
                rp.Bye();
                rp.TryFlushSendQueues();
                Log(LogLevel.Info, String.Format("Removed and saying bye to: {0}.", rp.EndPoint));
            }
            else
            {
                Log(LogLevel.Info, String.Format("Removed: {0}.", rp.EndPoint));
            }

            peersById.Remove(rp.Id);
            peersByIp.Remove(rp.EndPoint);

            RaisePeerDropped(rp.Id);
        }

        internal RemotePeer AddPeer(IPEndPoint ip, Packet joinUserData)
        {
            peerIdCount++;
            RemotePeer rp = new RemotePeer(this, ip, peerIdCount);
            peersById.Add(peerIdCount, rp);
            peersByIp.Add(ip, rp);

            // raise PeerAdded event
            if (PeerAdded != null)
            {
                // set the peer id which we only just learnt on the user data packet
                if (joinUserData != null)
                    joinUserData.PeerId = rp.Id;

                PeerAdded(rp.Id, joinUserData);

                // return packet to pool 
                if (joinUserData != null)
                    ReturnPacketToPool(joinUserData);
            }

            return rp;
        }

        internal void RemovePeerOnNextUpdate(RemotePeer rp)
        {
            remotePeersToRemove.Add(rp);
        }

        internal void EnqueuePacketToProcessOnNextUpdate(IPEndPoint fromIPEndPoint, byte[] datagram)
        {
            dummyDatagramsToProcess.Enqueue(Tuple.Create(fromIPEndPoint, datagram));
        }

        [Conditional("DEBUG")]
        internal void Log(LogLevel lvl, string msg)
        {
#if DEBUG
            if (lvl >= logLvl)
            {
                string line = String.Format("{0}\t{1}\t{2}\t{3}",
                    DateTime.Now.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fffffff"),
                    Port,
                    lvl,
                    msg);

                if (logger != null)
                    logger(lvl, line);
                else

                    Debug.WriteLine(line);
            }
#endif
        }

        internal void RaisePeerDropped(int peerId)
        {
            if (PeerDropped != null)
                PeerDropped(peerId);
        }

        internal void RaisePongReceivedFromUnknownPeer(IPEndPoint ipEndPoint, TimeSpan rtt)
        {
            PongReceivedFromUnknownPeer pongReceivedFromUnknownPeer = PongReceivedFromUnknownPeer;
            if (pongReceivedFromUnknownPeer != null)
                pongReceivedFromUnknownPeer(ipEndPoint, rtt);
        }

        internal void RaisePongReceived(RemotePeer rp, TimeSpan rtt)
        {
            PongReceivedFromPeer pongReceived = PongReceivedFromPeer;
            if (pongReceived != null)
                pongReceived(rp.Id, rtt);
        }

        internal void RaisePeerDiscovered(IPEndPoint ep)
        {
            PeerDiscovered peerDiscovered = PeerDiscovered;
            if (peerDiscovered != null)
                peerDiscovered(ep);
        }

        internal void RemovePingAwaitingPongDetail(PingDetail pingDetail)
        {
            PingsAwaitingPong.Remove(pingDetail);
            pingPool.Return(pingDetail);
        }

        internal void Stop(bool sayBye)
        {
            if (sayBye)
            {
                // say bye to everyone
                foreach (KeyValuePair<int, RemotePeer> kv in peersById)
                {
                    kv.Value.EnqueueSend(PacketType.Bye, SendOptions.None, null);
                }
                SendEnquedPackets();
            }

            // remote UPnP mapping if one added
            if (upnpDevice != null && upnpMappingAdded)
            {
                if (upnpDevice.TryDeleteForwardingRule(System.Net.Sockets.ProtocolType.Udp, (ushort)port))
                {
                    upnpMappingAdded = false;
                }
            }

            Transceiver.Stop();

            stopped = true;
            peersById.Clear();
            peersByIp.Clear();
            Stopwatch.Reset();

            Log(LogLevel.Info, "Stopped");
        }

        internal float GetAckTimeout(int resendCount)
        {
            // Calculate ACK timout based on number of re-sends with 7s being the upper limit:
            //
            //      MIN(ACKTimeout + (ACKTimeout x re-send count), 7.0)
            //

            if (resendCount == 0)
                return AckTimeoutSeconds;

            return Math.Min(AckTimeoutSeconds + AckTimeoutSeconds * resendCount, MaxAckTimeoutSeconds);
        }

        /// <summary>
        /// Attempts to start this FalconPeer TODO improve
        /// </summary>
        public FalconOperationResult TryStart()
        {
            this.Transceiver = TransceiverFactory.Create(this);

            // Get local IPv4 address and while doing so broadcast addresses to use for discovery.
            LocalAddresses.Clear();
            broadcastEndPoints = new List<IPEndPoint>();

#if PS4
            // PS4 auto calcs correct broadcast
            broadcastEndPoints.Add(new IPEndPoint(new IPAddress(new byte[] { 255, 255, 255, 255 }), this.port));
#elif NETFX_CORE
            foreach (HostName localHostInfo in NetworkInformation.GetHostNames())
            {
                if (localHostInfo.Type != HostNameType.Ipv4)
                    continue;

                LocalAddresses.Add(localHostInfo);
                
                uint ip;
                if (IPEndPoint.TryParseIPv4Address(localHostInfo.RawName, out ip))
                {
                    uint mask = FalconHelper.GetNetMaskFromNumOfBits(24); // class C
                    if (localHostInfo.IPInformation != null 
                        && localHostInfo.IPInformation.PrefixLength.HasValue
                        && localHostInfo.IPInformation.PrefixLength.Value < 32)
                    {
                        var prefix = localHostInfo.IPInformation.PrefixLength.Value;
                        mask = FalconHelper.GetNetMaskFromNumOfBits(prefix);
                    }
                    var broadcast = ip | ~mask;
                    broadcastEndPoints.Add(new IPEndPoint(broadcast, (ushort)this.port));
                }
            
            }
#else
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation addrInfo in props.UnicastAddresses)
                    {
                        if (addrInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // i.e. IPv4
                        {
                            // local addr
                            LocalAddresses.Add(addrInfo.Address);

                            // broadcast addr

                            uint mask = FalconHelper.GetNetMaskFromNumOfBits(24); // class C
#pragma warning disable 0618
#if !LINUX
                            if (addrInfo.IPv4Mask != null)
                                mask = (uint)addrInfo.IPv4Mask.Address;
#endif
                            uint ip = (uint)addrInfo.Address.Address;
                            uint broadcast = ip | ~mask;
                            broadcastEndPoints.Add(new IPEndPoint(new IPAddress((long)broadcast), Port));
#pragma warning restore 0618
                        }
                    }
                }
            }
            catch (NetworkInformationException niex)
            {
                return new FalconOperationResult(niex);
            }
#endif

#if !PS4
            if (LocalAddresses.Count == 0)
                return new FalconOperationResult(false, "No operational IPv4 network interface found.");
#endif

            FalconOperationResult startResult = Transceiver.TryStart();
            if (!startResult.Success)
                return startResult;

            // start the Stopwatch
            Stopwatch.Start();

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", this.Port));

            stopped = false;

            return FalconOperationResult.SuccessResult;
        }

        /// <summary>
        /// Borrows a Packet from the Packet Pool to write to.
        /// </summary>
        /// <returns>Packet in a write only state.</returns>
        /// <remarks>Packet once sent and/or finished with must be returned to pool using
        /// <see cref="ReturnPacketToPool(Packet)"/> and NOT used again.</remarks>
        public Packet BorrowPacketFromPool()
        {
            return PacketPool.Borrow();
        }

        /// <summary>
        /// Returns a Packet to the Packet Pool. 
        /// </summary>
        /// <param name="packet">Packet to return to pool.</param>
        /// <remarks>Once packet has been returned to the pool it must NOT be used again.</remarks>
        public void ReturnPacketToPool(Packet packet)
        {
            PacketPool.Return(packet);
        }

        /// <summary>
        /// Stops this FalconPeer, will stop listening and be unable send. Connected remote peers 
        /// will be dropped.
        /// </summary>
        /// <remarks><see cref="PeerDropped"/> is NOT raised when this method is called. All peers 
        /// will always be dropped.
        ///     <para>It is possible to start this FalconPeer again using <see cref="TryStart()"/>.</para>
        /// </remarks>
        public void Stop()
        {
            CheckStarted();
            Stop(true);
        }

        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer and FalconOperationResult.Tag will be set to the Id for this remote peer 
        /// which can also be obtained in the <see cref="PeerAdded"/> event. This Method returns 
        /// immediately then calls the callback supplied when the operation completes.</summary>
        /// <param name="addr">IPv4 address of remote peer, e.g. "192.168.0.5"</param>
        /// <param name="port">Port number the remote peer is listening on, e.g. 30000</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        /// <param name="callback"><see cref="FalconOperationCallback{TReturnValue}"/> callback to call when 
        /// operation completes.</param>
        /// <param name="userData">Optional additional data to be included in the <see cref="PeerAdded"/> event/</param>
        public void TryJoinPeerAsync(string addr, int port, string pass = null, FalconOperationCallback<int> callback = null, Packet userData = null)
        {
            CheckStarted();

#if NETFX_CORE
            IPEndPoint endPoint = new IPEndPoint(addr, port.ToString());
#else
            IPAddress ip = IPAddress.Parse(addr);
            IPEndPoint endPoint = new IPEndPoint(ip, port);
#endif
            TryJoinPeerAsync(endPoint, pass, callback, userData);
        }

        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer and FalconOperationResult.ReturnValue will be set to the Id for this remote peer 
        /// which can also be obtained in the <see cref="PeerAdded"/> event. This Method returns 
        /// immediately then calls the callback supplied when the operation completes.</summary>
        /// <param name="endPoint"><see cref="System.Net.IPEndPoint"/> of remote peer.</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        /// <param name="callback"><see cref="FalconOperationCallback{TReturnValue}"/> callback to call when 
        /// operation completes.</param>
        /// <param name="userData">Optional additional data to be included in the <see cref="PeerAdded"/> event.</param>
        public void TryJoinPeerAsync(IPEndPoint endPoint, string pass = null, FalconOperationCallback<int> callback = null, Packet userData = null)
        {
            byte[] joinBytes = null;
            if (pass == null && userData == null)
            {
                joinBytes = new byte[1];
            }
            else
            {
                Packet joinPayload = PacketPool.Borrow();

                // write pass prepended with size as byte
                if (pass == null)
                {
                    joinPayload.WriteByte(0);
                }
                else
                {
                    byte[] passBytes = TextEncoding.GetBytes(pass);
                    if (passBytes.Length > byte.MaxValue)
                        throw new ArgumentException("pass too long - cannot exceed 256 bytes", "pass");
                    joinPayload.WriteByte((byte)passBytes.Length);
                    joinPayload.WriteBytes(passBytes);
                }

                // write user data
                if (userData != null)
                {
                    joinPayload.WriteBytes(userData, 0, userData.BytesWritten);
                }

                // get payload as byte[]
                joinBytes = joinPayload.ToBytes();

                PacketPool.Return(joinPayload);
            }

            AwaitingAcceptDetail detail = new AwaitingAcceptDetail(endPoint, callback, joinBytes);

            // Copy the user data into a packet which will be passed to the PeerAdded event, this 
            // if this operation is successful. This way user cannot modify the userData supplied.

            if (userData != null)
            {
                Packet userDataCopy = BorrowPacketFromPool();
                Packet.Clone(userData, userDataCopy, true);
                detail.UserDataPacket = userDataCopy;
            }

            awaitingAcceptDetails.Add(detail);
            TryJoinPeerAsync(detail);
        }

        /// <summary>
        /// Begins a discovery process by emitting discovery signals to connected subnet on port 
        /// and for the time supplied.
        /// </summary>
        /// <param name="timeSpan">Time span to wait for replies.</param>
        /// <param name="port">Port number to emit discovery signals to.</param>
        /// <param name="token">Optional <see cref="System.Guid"/> token remote peer requries</param>
        /// <param name="callback"><see cref="DiscoveryCallback"/> to invoke when the operation completes</param>
        /// <param name="signalsToEmit">The number or broadcast signals to emit over the period <paramref name="timeSpan"/>.</param>
        /// <remarks><paramref name="token"/> should be null if NOT to be included int the discovery requests.</remarks>
        public void DiscoverFalconPeersAsync(TimeSpan timeSpan, int port, Guid? token, DiscoveryCallback callback, int signalsToEmit = 3)
        {
            CheckStarted();

            List<IPEndPoint> endPoints = null;
            if (port == Port)
            {
                endPoints = broadcastEndPoints;
            }
            else
            {
                endPoints = new List<IPEndPoint>(broadcastEndPoints.Count);
                foreach (var ep in broadcastEndPoints)
                {
#if NETFX_CORE
                    endPoints.Add(new IPEndPoint(ep.Address.RawName, port.ToString()));
#else
                    endPoints.Add(new IPEndPoint(ep.Address, port));
#endif
                }
            }

            DiscoverFalconPeersAsync(true,
                timeSpan,
                signalsToEmit,
                ushort.MaxValue,
                endPoints,
                token,
                callback);
        }

        /// <summary>
        /// Begins a discovery process by emitting signals to <paramref name="publicEndPoint"/>
        /// </summary>
        /// <param name="publicEndPoint"><see cref="IPEndPoint"/> to send discovery signals to.</param>
        /// <param name="timeSpan">Time span to continue operation for.</param>
        /// <param name="numOfRequests">Number of signals to emit.</param>
        /// <param name="replyToDiscoveryRequestsWithToken"><see cref="Guid"/> token required to solicit a response to.</param>
        public void AssistPunchThroughFromAsync(IPEndPoint publicEndPoint,
            TimeSpan timeSpan,
            int numOfRequests,
            Guid? replyToDiscoveryRequestsWithToken)
        {
            CheckStarted();

            // TODO after period remove token and set state or leave that up to user-application?
            if (replyToDiscoveryRequestsWithToken.HasValue)
            {
                this.onlyReplyToDiscoveryRequestsWithToken.Add(replyToDiscoveryRequestsWithToken.Value);
            }
            else
            {
                replyToAnyDiscoveryRequests = true;
            }

            DiscoverFalconPeersAsync(false,
                timeSpan,
                numOfRequests,
                1,
                new[] { publicEndPoint },
                null, // NOTE: we are assisting punch though and are not listening for a reply therefore do not need to include token in our DiscoveryRequests
                null);
        }

        /// <summary>
        /// Begins a discover process by emitting signals to <paramref name="endPoints"/>.
        /// </summary>
        /// <param name="endPoints"><see cref="IPEndPoint"/>s to send discovery signals to.</param>
        /// <param name="timeSpan">Time span to continue operation for.</param>
        /// <param name="numOfRequests">Number of signals to emit.</param>
        /// <param name="token"><see cref="Guid"/> token</param>
        /// <param name="callback"><see cref="PunchThroughCallback"/> to invoke once process completes.</param>
        public void PunchThroughToAsync(IEnumerable<IPEndPoint> endPoints,
            TimeSpan timeSpan,
            int numOfRequests,
            Guid? token,
            PunchThroughCallback callback)
        {
            CheckStarted();

            punchThroughCallback = callback;
            DiscoverFalconPeersAsync(true, timeSpan, numOfRequests, 1, endPoints, token, PunchThroughDiscoveryCallback);
        }

        /// <summary>
        /// Sets all the visibility options for this FalconPeer on the network.
        /// </summary>
        /// <param name="acceptJoinRequests">Set to true to allow other FalconUDP peers to join this one.</param>
        /// <param name="joinPassword">Password FalconUDP peers require to join this one.</param>
        /// <param name="replyToDiscoveryRequests">Set to true to allow other FalconUDP peers discover this one with or without a token.</param>
        /// <param name="replyToAnonymousPings">Set to true to send reply pong to any FalconUDP Ping even if they have not joined.</param>
        /// <param name="replyToDiscoveryRequestsWithToken">Token incoming discovery requests require if we are to send reply to.</param>
        public void SetVisibility(bool acceptJoinRequests,
            string joinPassword,
            bool replyToDiscoveryRequests,
            bool replyToAnonymousPings = false,
            Guid? replyToDiscoveryRequestsWithToken = null)
        {
            if (joinPassword != null && !acceptJoinRequests)
                throw new ArgumentException("joinPassword must be null if not accepting join requests");
            if (replyToDiscoveryRequestsWithToken != null && !replyToDiscoveryRequests)
                throw new ArgumentException("replyToDiscoveryRequestsWithToken must be null if not to reply to discovery requests");

            this.acceptJoinRequests = acceptJoinRequests;
            this.joinPass = joinPassword;
            this.replyToAnyDiscoveryRequests = replyToDiscoveryRequests;
            this.replyToAnonymousPings = replyToAnonymousPings;

            if (replyToDiscoveryRequestsWithToken.HasValue)
            {
                this.onlyReplyToDiscoveryRequestsWithToken.Add(replyToDiscoveryRequestsWithToken.Value);
            }
        }

        /// <summary>
        /// Enqueues packet to be sent to <paramref name="peerId"/> next time <see cref="SendEnquedPackets"/> is called.
        /// </summary>
        /// <param name="peerId">Id of the remote peer.</param>
        /// <param name="opts"><see cref="SendOptions"/> to send packet with.</param>
        /// <param name="packet"><see cref="Packet"/> containing the data to send.</param>
        public void EnqueueSendTo(int peerId, SendOptions opts, Packet packet)
        {
            CheckStarted();

            RemotePeer rp;
            if (!peersById.TryGetValue(peerId, out rp))
            {
                Log(LogLevel.Error, "Attempt to SendTo unknown Peer ignored: " + peerId.ToString());
                return;
            }
            else
            {
                rp.EnqueueSend(PacketType.Application, opts, packet);
            }
        }

        /// <summary>
        /// Enqueues packet on be sent to all joint peers next time <see cref="SendEnquedPackets"/> is called.
        /// </summary>
        /// <param name="opts"><see cref="SendOptions"/> to send packet with.</param>
        /// <param name="packet"> containing the data to send.</param>
        public void EnqueueSendToAll(SendOptions opts, Packet packet)
        {
            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                kv.Value.EnqueueSend(PacketType.Application, opts, packet);
            }
        }

        /// <summary>
        /// Enqueues packet on be sent to all joint peers except <paramref name="peerIdToExclude"/> next time <see cref="SendEnquedPackets"/> is called.
        /// </summary>
        /// <param name="peerIdToExclude">Id of peer to NOT send <paramref name="packet"/> to.</param>
        /// <param name="opts"><see cref="SendOptions"/> to send packet with.</param>
        /// <param name="packet"> containing the data to send.</param>
        public void EnqueueSendToAllExcept(int peerIdToExclude, SendOptions opts, Packet packet)
        {
            CheckStarted();

            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                if (kv.Key == peerIdToExclude)
                    continue;

                kv.Value.EnqueueSend(PacketType.Application, opts, packet);
            }
        }

        /// <summary>
        /// Ping remote peer.
        /// </summary>
        /// <param name="peerId">Id of the of remote peer to ping.</param>
        /// <returns>True if ping successfully sent.</returns>
        /// <remarks><see cref="PongReceivedFromPeer"/>Will be raised, when/if reply Pong is received in time.</remarks>
        public bool PingPeer(int peerId)
        {
            CheckStarted();

            RemotePeer rp;
            if (!peersById.TryGetValue(peerId, out rp))
            {
                return false;
            }
            else
            {
                PingDetail detail = pingPool.Borrow();
                detail.Init(peerId);
                PingsAwaitingPong.Add(detail);
                rp.Ping();
                return true;
            }
        }

        /// <summary>
        /// Send ping <paramref name="ipEndPoint"/> which may not be joined.
        /// </summary>
        /// <param name="ipEndPoint"><see cref="IPEndPoint"/> to ping.</param>
        /// <remarks><see cref="PongReceivedFromUnknownPeer"/> Will be raised, when/if reply Pong is received in time.</remarks>
        public void PingEndPoint(IPEndPoint ipEndPoint)
        {
            CheckStarted();

            PingDetail detail = pingPool.Borrow();
            detail.Init(ipEndPoint);
            PingsAwaitingPong.Add(detail);
            SendToUnknownPeer(ipEndPoint, PacketType.Ping, SendOptions.None, null);
        }

        /// <summary>
        /// Attempts to get the <see cref="IPEndPoint"/> associated with the remote peer.
        /// </summary>
        /// <param name="peerId">Id of the remote peer.</param>
        /// <param name="ip"><see cref="IPEndPoint"/> associated with the remote peer. Set if found, i.e. returns true, otherwise null</param>
        /// <returns>True if remote peer with the <paramref name="peerId"/> connected.</returns>
        public bool TryGetPeerIPEndPoint(int peerId, out IPEndPoint ip)
        {
            CheckStarted();

            RemotePeer rp;
            if (peersById.TryGetValue(peerId, out rp))
            {
                ip = rp.EndPoint;
                return true;
            }

            ip = null;
            return false;
        }

        /// <summary>
        /// Attempts to get the Id of a connected remote peer with <paramref name="ip"/>.
        /// </summary>
        /// <param name="ip"><see cref="IPEndPoint"/> of the remote peer.</param>
        /// <param name="peerId">Id of the remote peer. Set if found, i.e. returns true, otherwise set to -1.</param>
        /// <returns>True if remote peer with the <paramref name="peerId"/> connected.</returns>
        public bool TryGetPeerId(IPEndPoint ip, out int peerId)
        {
            CheckStarted();

            RemotePeer rp;
            if (peersByIp.TryGetValue(ip, out rp))
            {
                peerId = rp.Id;
                return true;
            }

            peerId = -1;
            return false;
        }

        /// <summary>
        /// Removes remote peer if currently connected.
        /// </summary>
        /// <param name="peerId">Id of the remote peer.</param>
        public void RemovePeer(int peerId)
        {
            CheckStarted();

            RemotePeer rp;
            if (peersById.TryGetValue(peerId, out rp))
            {
                RemovePeer(rp, true);
            }
        }

        /// <summary>
        /// Removes all remote peers except one with <paramref name="peerId"/>
        /// </summary>
        /// <param name="peerId">Id of peer NOT to remove.</param>
        public void RemoveAllPeersExcept(int peerId)
        {
            CheckStarted();

            int[] ids = new int[peersById.Count]; // TODO garbage :-|
            peersById.Keys.CopyTo(ids, 0);

            foreach (int id in ids)
            {
                if (id == peerId)
                    continue;
                RemovePeer(peersById[id], true);
            }
        }

        /// <summary>
        /// Removes all remote peers.
        /// </summary>
        public void RemoveAllPeers()
        {
            CheckStarted();

            int[] ids = new int[peersById.Count]; // TODO garbage :-|
            peersById.Keys.CopyTo(ids, 0);

            foreach (int id in ids)
            {
                RemovePeer(peersById[id], true);
            }
        }

        /// <summary>
        /// Sends every <see cref="Packet"/> enqueud since this was last called.
        /// </summary>
        public void SendEnquedPackets()
        {
            CheckStarted();

            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                kv.Value.TryFlushSendQueues();
            }
        }

        /// <summary>
        /// Gets all current remote peers.
        /// </summary>
        /// <returns>Returns a Dictionary&lt;int, IPEndPoint&gt; of all current remote peers where the 
        /// key is the peer Id and the value is the end point of the remote peer</returns>
        public Dictionary<int, IPEndPoint> GetAllRemotePeers()
        {
            CheckStarted();

            Dictionary<int, IPEndPoint> peers = new Dictionary<int, IPEndPoint>(peersById.Count);
            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                peers.Add(kv.Key, kv.Value.EndPoint);
            }
            return peers;
        }

#if DEBUG
        /// <summary>
        /// Updates <see cref="LogLevel"/> at which to log at.
        /// </summary>
        /// <param name="lvl"><see cref="LogLevel"/> at which to log at</param>
        /// <remarks><paramref name="lvl"/> and more serious levels are logged.</remarks>
        public void SetLogLevel(LogLevel lvl)
        {
            logLvl = lvl;
        }
#endif


        /// <summary>
        /// Helper method to get all active IPv4 network interfaces.
        /// </summary>
        /// <returns>Returns list of <see cref="IPEndPoint"/> that are operational, non-loopback, 
        /// IPv4 and have sent and received at least one byte, with the same port as this 
        /// FalconPeer.</returns>
        public List<IPEndPoint> GetLocalIPEndPoints()
        {
            List<IPEndPoint> ipEndPoints = new List<IPEndPoint>();
#if NETFX_CORE
            foreach (HostName localHostInfo in NetworkInformation.GetHostNames())
            {
                if (localHostInfo.Type == HostNameType.Ipv4 && localHostInfo.IPInformation != null)
                {
                    ipEndPoints.Add(new IPEndPoint(localHostInfo.RawName, this.port.ToString()));
                }
            }
#else
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                    if (stats.BytesSent > 0 && stats.BytesReceived > 0)
                    {
                        IPInterfaceProperties props = nic.GetIPProperties();
                        foreach (UnicastIPAddressInformation info in props.UnicastAddresses)
                        {
                            if (info.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                ipEndPoints.Add(new IPEndPoint(info.Address, Port));
                            }
                        }
                    }
                }
            }
#endif
            return ipEndPoints;
        }


        /// <summary>
        /// Start collection <see cref="Statistics"/> or resets statistics if already started.
        /// </summary>
        public void StartCollectingStatistics()
        {
            if (Statistics == null)
            {
                Statistics = new Statistics();
            }
            else
            {
                Statistics.Reset();
            }
        }

        /// <summary>
        /// Stop collection statistics. <see cref="Statistics"/>
        /// </summary>
        public void StopCollectingStatistics()
        {
            Statistics = null;
        }

        /// <summary>
        /// Updates this FalconPeer. Performs message pumping and processes received packets. 
        /// </summary>
        /// <remarks>This must be called frequently at regular intervals (for example 30 times a 
        /// second) even when nothing may have been sent. </remarks>
        public void Update()
        {
            CheckStarted();

            // NOTE: Stopwatch with a frequency of 2338439 will only loop after 16935 days 14 mins 
            //       and 9 seconds!

            TimeSpan ellapsed = Stopwatch.Elapsed;
            float ellapsedSeconds = (float)ellapsed.TotalSeconds;
            float dt = ellapsedSeconds - ellapsedSecondsAtLastUpdate;
            ellapsedSecondsAtLastUpdate = ellapsedSeconds;

            // quick disconnect
            if (dt > quickDisconnectTimeout)
                RemoveAllPeers();

            // add UPnP mapping operation
            if (addUPnPMappingCallback != null)
            {
                if (ellapsed - addUPnPEllapsedAtStart > addUPnPMappingTimeout)
                {
                    EndAddUPnPMapping(AddUPnPMappingResult.FailedTimedOut);
                }
            }

            Update(dt);
        }

        /// <summary>
        /// Helper method which can be used by the user-application for measuring time.
        /// </summary>
        /// <returns><see cref="TimeSpan"/> since this FalconPeer started.</returns>
        public TimeSpan GetEllapsedSinceStarted()
        {
            CheckStarted();
            return Stopwatch.Elapsed;
        }

        /// <summary>
        /// Gets <see cref="QualityOfService"/> for <paramref name="peerId"/>.
        /// </summary>
        /// <param name="peerId">Id of the remote peer</param>
        /// <returns><see cref="QualityOfService"/> for remote peer <paramref name="peerId"/> if 
        /// peer joined. Otherwise <see cref="QualityOfService"/> with values zeroed out.</returns>
        public QualityOfService GetPeerQualityOfService(int peerId)
        {
            RemotePeer rp;
            if (peersById.TryGetValue(peerId, out rp))
            {
                return rp.QualityOfService;
            }

            // Instead of returning null if peer not found (may have just dropped) be kind and 
            // return zeroed out quality of service.
            return QualityOfService.ZeroedOutQualityOfService;
        }

#if !NETFX_CORE
        /// <summary>
        /// Helper method gets whether <paramref name="ip"/> is in the private address space as defined in 
        /// RFC3927.
        /// </summary>
        /// <param name="ip"><see cref="IPAddress"/> to check.</param>
        /// <returns>True if <paramref name="ip"/> is in the private address space, otherwise false.</returns>
        public static bool GetIsIPAddressPrivate(IPAddress ip)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new NotImplementedException("only IPv4 addresses implemented");

            byte[] bytes = ip.GetAddressBytes(); // TODO garbage :-|

            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            if (bytes[0] == 10)
                return true;

            if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31))
                return true;

            return false;
        }
#endif

        /// <summary>
        /// Helper method returns <see cref="System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()"/>.
        /// </summary>
        /// <returns><see cref="System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()"/></returns>
        public static bool GetIsNetworkAvaliable()
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }

        /// <summary>
        /// Uses Internet Gateway Device Protocol to attempt to configure port forwarding on 
        /// connected UPnP enabled router with NAT.<break></break>  
        /// <break></break>
        /// This FalconPeer instance must already be started and the UPnP port mapping request 
        /// will be for the port this peer is listening on.<break></break>
        /// <break></break>
        /// The mapping will be deleted, if it was succesfully added, when this FalconPeer stops.
        /// </summary>
        /// <remarks> 
        /// UPnP Internet Gateway Device Protocol port mapping is not the be all end all solution 
        /// to NAT traversal, indeed many routers do not have it enabled by default if they support 
        /// it at all. Futhermore it does not support scenarios where you are behind multiple NATs 
        /// or there are multiple peers requesting the same port to be forwarded behind the same 
        /// NAT.<break></break>  
        /// <break></break>
        /// A better more widely successful tenchnique is the one best described in RFC 5128 (https://tools.ietf.org/html/rfc5128#page-11)
        /// often called "UDP hole punching". <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint}, TimeSpan, int, Guid?, PunchThroughCallback)"/> and <see cref="AssistPunchThroughFromAsync(IPEndPoint, TimeSpan, int, Guid?)"/>
        /// use the hole punching techinque in conjuction with a 3rd party server that the peers must 
        /// communicate with first and get eachothers IP addresses for use with these methods.<break></break>
        /// <break></break>
        /// A combination of both techniques gives a higher success rate. Another technique I have not explored is Port Control Protocl (PCP).
        /// </remarks>
        /// <param name="callback"></param>
        /// <param name="timeout"></param>
        public void TryAddUPnPPortMapping(AddUPnPPortMappingCallback callback, TimeSpan timeout)
        {
            CheckStarted();
            if (callback == null)
                throw new ArgumentNullException("callback");
            if (addUPnPMappingCallback != null)
                throw new InvalidOperationException("Operation already in-progress");

            addUPnPMappingTimeout = timeout;
            addUPnPMappingCallback = callback;
            addUPnPEllapsedAtStart = Stopwatch.Elapsed;
            Transceiver.Send(Const.UPNP_DISCOVER_REQUEST, 0, Const.UPNP_DISCOVER_REQUEST.Length, Const.UPNP_DISCOVER_ENDPOINT, false);
        }

        /// <summary>
        /// Overload for <see cref="TryAddUPnPPortMapping(AddUPnPPortMappingCallback, TimeSpan)"/>.
        /// Timeout used is 5 seconds.
        /// </summary>
        /// <param name="callback">Delegate to callback once operation completes.</param>
        public void TryAddUPnPPortMapping(AddUPnPPortMappingCallback callback)
        {
            TryAddUPnPPortMapping(callback, TimeSpan.FromSeconds(5.0));
        }

        /// <summary>
        /// Overload for <see cref="TryAddUPnPPortMapping(AddUPnPPortMappingCallback)"/> when callback not needed.
        /// Timeout used is 5 seconds.
        /// </summary>
        public void TryAddUPnPPortMapping()
        {
            TryAddUPnPPortMapping(result => {  });
        }
    }
}

