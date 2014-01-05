using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace FalconUDP
{
    /// <summary>
    /// Represents a FalconUDP peer which can discover, join and commuincate with other 
    /// compatible FalconUDP peers connected to the same network.
    /// </summary>
    public class FalconPeer
    {
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
        /// Event raised during a discovery operation started by calling either <see cref="DiscoverFalconPeersAsync(int, int, Guid?, DiscoveryCallback)"/>
        /// or <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint},int,int,Guid?,PunchThroughCallback)"/>
        /// </summary>
        /// <remarks>This event is raised as soon as reply is received from a discovery request. The callback to <see cref="DiscoverFalconPeersAsync(int, int, Guid?, DiscoveryCallback)"/>
        /// will have the details of any other peers discovered. The callback to <see cref="PunchThroughToAsync(IEnumerable{IPEndPoint},int,int,Guid?,PunchThroughCallback)"/>
        /// will have the details of the first peer that responded which will be the same as the details in this event.</remarks>
        public event PeerDiscovered PeerDiscovered;

        /// <summary>
        /// Event raised when Ping received in reply to a Ping sent to a known Peer using <see cref="PingPeer(int)"/>.
        /// </summary>
        public event PongReceivedFromPeer PongReceivedFromPeer;

        /// <summary>
        /// Event raised when Ping received in reply to a Ping sent to a known or unknown Peer using <see cref="PingEndPoint(IPEndPoint)"/>.
        /// </summary>
        public event PongReceivedFromUnknownPeer PongReceivedFromUnknownPeer;

        /// <summary>
        /// Port this FalconPeer is or will be listening on.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// <see cref="Statistics"/> structure containing total bytes sent and recieved in the last secound.
        /// </summary>
        public Statistics Statistics { get; private set; }

        /// <summary>
        /// Gets whether this <see cref="FalconPeer"/> is started.
        /// </summary>
        public bool IsStarted { get { return !stopped; } }

        internal float SimulateDelaySecounds { get; private set; }
        internal int SimulateDelayJitterMillisecuonds { get; set; }
        internal double SimulatePacketLossChance { get; private set; }
        internal bool IsCollectingStatistics { get { return Statistics != null; } }
        internal bool HasPingsAwaitingPong { get { return PingsAwaitingPong.Count > 0; } }
 
        internal Socket             Socket;
        internal Stopwatch          Stopwatch;
        internal PacketPool         PacketPool;
        internal HashSet<IPAddress> LocalAddresses;             // TODO is it possible to have the same addr on diff interface? even so does it matter?
        internal List<PingDetail>   PingsAwaitingPong;

        private ProcessReceivedPacket processReceivedPacketDelegate;
        private IPEndPoint          anyAddrEndPoint;            // end point to receive on (combined with port to create IPEndPoint)
        private byte[]              receiveBuffer;
        private int                 peerIdCount;
        private Dictionary<IPEndPoint, RemotePeer> peersByIp;   // same RemotePeers as peersById
        private Dictionary<int, RemotePeer> peersById;          // same RemotePeers as peersByIp
        private LogLevel            logLvl;
#if DEBUG
        private LogCallback         logger;
#endif
        private string              joinPass; 
        private PunchThroughCallback punchThroughCallback;
        private  bool               stopped;
        private  bool               acceptJoinRequests;
        private  bool               replyToAnonymousPings;
        private List<AwaitingAcceptDetail> awaitingAcceptDetails;
        private long                ellapsedTicksAtLastUpdate;
        private List<Packet>        readPacketsList;
        private List<RemotePeer>    remotePeersToRemove;
        
        // pools
        private GenericObjectPool<EmitDiscoverySignalTask> emitDiscoverySignalTaskPool; 
        private GenericObjectPool<PingDetail> pingPool;

        // discovery
        private List<EmitDiscoverySignalTask> discoveryTasks;
        private bool                replyToDiscoveryRequests;           // i.e. reply unconditionally without a token
        private List<Guid>          onlyReplyToDiscoveryRequestsWithToken;

        // helper
        private RemotePeer          unknownPeer;                        // peer re-used to send unsolicited messages to
        private List<IPEndPoint>    broadcastEndPoints;

        /// <summary>
        /// Creates a new FalconPeer.
        /// </summary>
        /// <param name="port">Port to listen on.</param>
        /// <param name="processReceivedPacketDelegate">Callback invoked when 
        /// <see cref="ProcessReceivedPackets()"/> called for each packet received.</param>
        /// <param name="logCallback">Callback to use for logging, if not supplied logs written to Debug.</param>
        /// <param name="logLevel">Severtiy level and more serious levels which to log.</param>
#if DEBUG
        public FalconPeer(int port, 
            ProcessReceivedPacket processReceivedPacketDelegate, 
            LogCallback logCallback = null, 
            LogLevel logLevel = LogLevel.Warning)
#else
        public FalconPeer(int port, ProcessReceivedPacket processReceivedPacketDelegate)
#endif
        {
            if (!BitConverter.IsLittleEndian)
                new PlatformNotSupportedException("CPU architecture not supported: BigEndian reading and writing to and from FalconUDP packets has not been implemented");

            this.Port                       = port;
            this.processReceivedPacketDelegate = processReceivedPacketDelegate;
            this.peersByIp                  = new Dictionary<IPEndPoint, RemotePeer>();
            this.peersById                  = new Dictionary<int, RemotePeer>();
            this.anyAddrEndPoint            = new IPEndPoint(IPAddress.Any, this.Port);
            this.peerIdCount                = 0;
            this.awaitingAcceptDetails      = new List<AwaitingAcceptDetail>();
            this.acceptJoinRequests         = false;
            this.PingsAwaitingPong          = new List<PingDetail>();
            this.receiveBuffer              = new byte[Const.MAX_DATAGRAM_SIZE];
            this.readPacketsList            = new List<Packet>();
            this.stopped                    = false;
            this.remotePeersToRemove        = new List<RemotePeer>();

            // pools
            this.PacketPool                 = new PacketPool(Const.MAX_PAYLOAD_SIZE, Settings.InitalNumPacketsToPool);
            this.emitDiscoverySignalTaskPool= new GenericObjectPool<EmitDiscoverySignalTask>(Settings.InitalNumEmitDiscoverySignalTaskToPool);
            this.pingPool                   = new GenericObjectPool<PingDetail>(Settings.InitalNumPingsToPool);

            // discovery
            this.discoveryTasks             = new List<EmitDiscoverySignalTask>();
            this.onlyReplyToDiscoveryRequestsWithToken = new List<Guid>();

            // helper
            this.unknownPeer                = new RemotePeer(this, new IPEndPoint(IPAddress.Broadcast, this.Port), 0, false);
            
#if DEBUG
            // log
            this.logLvl = logLevel;
            if (logLevel != LogLevel.NoLogging)
            {
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
                Log(LogLevel.Info, "Stopwatch will loop after " + (new TimeSpan(0, 0, (int)(long.MaxValue / Stopwatch.Frequency))).ToString());
            }
#endif
        }

        private void CheckStarted()
        {
            if (stopped)
                throw new InvalidOperationException("FalconPeer is not started!");
        }

        private void ProcessReceivedPackets()
        {
            // clear the list of previously read packets
            readPacketsList.Clear();

            // move received packets ready for reading from remote peers into readPacketList
            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                if (kv.Value.UnreadPacketCount > 0)
                {
                    readPacketsList.AddRange(kv.Value.Read());
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

            // read received datagrams
            while (Socket.Available > 0)
            {
                EndPoint fromIPEndPoint = anyAddrEndPoint;
                int size = 0;

                try
                {
                    size = Socket.ReceiveFrom(receiveBuffer, ref fromIPEndPoint);
                }
                catch (SocketException se)
                {
                    Log(LogLevel.Error, String.Format("Socket Exception {0} {1}, while receiving from {2}.", se.ErrorCode, se.Message, (IPEndPoint)fromIPEndPoint));
                    TryRemovePeer((IPEndPoint)fromIPEndPoint, false, false);
                }

                Log(LogLevel.Debug, String.Format("Received {0} bytes from: {1}", size, (IPEndPoint)fromIPEndPoint));

                if (size == 0)
                {
                    // the connection has closed, if peer joined remove TODO is this possible in UDP?
                    TryRemovePeer((IPEndPoint)fromIPEndPoint, false, false);
                }
                else
                {
                    ProcessReceivedDatagram((IPEndPoint)fromIPEndPoint, receiveBuffer, size);
                }
            }

            // process received packets
            ProcessReceivedPackets();

            // unknown peer
            unknownPeer.Update(dt);

            // remote peers
            foreach (KeyValuePair<int, RemotePeer> kv in peersById)
            {
                kv.Value.Update(dt);
            }

            // pings awaiting pong
            if (PingsAwaitingPong.Count > 0)
            {
                for (int i = 0; i < PingsAwaitingPong.Count; i++)
                {
                    PingDetail detail = PingsAwaitingPong[i];
                    if ((Stopwatch.ElapsedMilliseconds - detail.EllapsedMillisecondsAtSend) > Settings.PingTimeout)
                    {
                        PingsAwaitingPong.RemoveAt(i);
                        --i;
                        pingPool.Return(detail);
                    }
                }
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
                    if (aad.EllapsedMillisecondsSinceStart >= Settings.ACKTimeout)
                    {
                        aad.EllapsedMillisecondsSinceStart = 0;
                        aad.RetryCount++;
                        if (aad.RetryCount == Settings.ACKRetryAttempts)
                        {
                            // give up, peer has not been added yet so no need to drop
                            awaitingAcceptDetails.RemoveAt(i);
                            i--;
                            aad.Callback(new FalconOperationResult<int>(false, "Remote peer never responded to join request.", -1));
                        }
                        else
                        {
                            // try again
                            TryJoinPeerAsync(aad);
                        }
                    }
                }
            }
        }

        private void SendToUnknownPeer(IPEndPoint ep, PacketType type, SendOptions opts, byte[] payload)
        {
            Debug.Assert(!opts.HasFlag(SendOptions.Reliable), "cannot send reliable messages to unknown peer");

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
            detail = awaitingAcceptDetails.Find(aad => aad.EndPoint.Address.Equals(ep.Address) && aad.EndPoint.Port == ep.Port);
            if (detail != null)
            {
                awaitingAcceptDetails.Remove(detail);
                return true;
            }
            return false;
        }
        
        private void DiscoverFalconPeersAsync(bool listenForReply,
            float durationInSecounds,
            int numOfRequests, 
            int maxPeersToDiscover,
            IEnumerable<IPEndPoint> endPoints,
            Guid? token,
            DiscoveryCallback callback)
        {
            EmitDiscoverySignalTask task = emitDiscoverySignalTaskPool.Borrow();
            task.Init(this, listenForReply, durationInSecounds, numOfRequests, maxPeersToDiscover, endPoints, token, callback);
            lock (discoveryTasks)
            {
                task.EmitDiscoverySignal(); // emit first signal now
                discoveryTasks.Add(task);
            }
        }

        private void TryRemovePeer(IPEndPoint ip, bool logFailure, bool sayBye) 
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
            SendToUnknownPeer(detail.EndPoint, PacketType.JoinRequest, SendOptions.None, detail.Pass);
        }

        private void ProcessReceivedDatagram(IPEndPoint fromIPEndPoint, byte[] buffer, int size)
        {
            // check size
            if (size < Const.FALCON_PACKET_HEADER_SIZE)
            {
                Log(LogLevel.Error, String.Format("Datagram dropped from: {0}, smaller than min size.", fromIPEndPoint));
                return;
            }
            if (size > Const.MAX_DATAGRAM_SIZE)
            {
                Log(LogLevel.Error, String.Format("Datagram dropped from: {0}, greater than max size.", fromIPEndPoint));
                return;
            }

            // check not from self
            if ((IPAddress.IsLoopback(fromIPEndPoint.Address) || LocalAddresses.Contains(fromIPEndPoint.Address))
                && fromIPEndPoint.Port == Port)
            {
                Log(LogLevel.Warning, "Dropped datagram received from self.");
                return;
            }

            // parse header
            byte packetDetail   = buffer[0];
            SendOptions opts    = (SendOptions)(byte)(packetDetail & Const.SEND_OPTS_MASK);
            PacketType type     = (PacketType)(byte)(packetDetail & Const.PACKET_TYPE_MASK);
            bool isAckPacket    = (type == PacketType.ACK || type == PacketType.AntiACK);
            ushort seq          = BitConverter.ToUInt16(buffer, 1);
            ushort payloadSize  = BitConverter.ToUInt16(buffer, 3);
            
            // check the header makes sense (anyone could send us UDP datagrams)
            if (!Enum.IsDefined(Const.SEND_OPTIONS_TYPE, opts) || !Enum.IsDefined(Const.PACKET_TYPE_TYPE, type))
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

            RemotePeer rp;
            bool isFirstPacketInDatagram = true;

            do
            {
                Log(LogLevel.Debug, String.Format("Processing received packet type: {0}, channel: {1}, seq {2}, payload size: {3}...", type, opts, seq, payloadSize));

                if (peersByIp.TryGetValue(fromIPEndPoint, out rp))
                {
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
                }
                else
                {
                    #region Proccess Unauthenticated Datagram
                    switch (type)
                    {
                        case PacketType.JoinRequest:
                            {
                                if (!acceptJoinRequests)
                                {
                                    Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, not accepting join requests.", fromIPEndPoint));
                                    return;
                                }

                                string pass = null;
                                if (payloadSize > 0)
                                {
                                    pass = Settings.TextEncoding.GetString(buffer, index, payloadSize);
                                    count -= payloadSize;
                                    index += payloadSize;
                                }

                                if (pass != joinPass)
                                {
                                    Log(LogLevel.Warning, String.Format("Join request from: {0} dropped, bad pass.", fromIPEndPoint));
                                }
                                else if (peersByIp.ContainsKey(fromIPEndPoint))
                                {
                                    Log(LogLevel.Warning, String.Format("Cannot add peer again: {0}, peer is already added!", fromIPEndPoint));
                                }
                                else
                                {
                                    Log(LogLevel.Info, String.Format("Accepted Join Request from: {0}", fromIPEndPoint));

                                    rp = AddPeer(fromIPEndPoint);
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
                                    rp = AddPeer(fromIPEndPoint);
                                    rp.ACK(seq, PacketType.ACK, opts);
                                    rp.IsKeepAliveMaster = true; // the acceptor of our join request is the keep-alive-master by default
                                    FalconOperationResult<int> tr = new FalconOperationResult<int>(true, null, null, rp.Id);
                                    detail.Callback(tr);
                                }
                            }
                            break;
                        case PacketType.DiscoverRequest:
                            {
                                bool reply = false;

                                if (replyToDiscoveryRequests)
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
                                        && pd.IPEndPointPingSentTo.Address.Equals(fromIPEndPoint.Address)
                                        && pd.IPEndPointPingSentTo.Port == fromIPEndPoint.Port);

                                    if (detail != null)
                                    {
                                        RaisePongReceived(fromIPEndPoint, (int)(Stopwatch.ElapsedMilliseconds - detail.EllapsedMillisecondsAtSend));
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

                // process any additional packets in datagram

                if (!isAckPacket) // payloadSize is stopover time in ACKs
                {
                    count -= payloadSize;
                    index += payloadSize;
                }

                if (count >= Const.ADDITIONAL_PACKET_HEADER_SIZE)
                {
                    // parse additional packet header
                    packetDetail    = buffer[index];
                    type            = (PacketType)(packetDetail & Const.PACKET_TYPE_MASK);
                    isAckPacket     = (type == PacketType.ACK || type == PacketType.AntiACK);
                    if (isAckPacket)
                    {
                        seq         = BitConverter.ToUInt16(buffer, index + 1);
                        payloadSize = BitConverter.ToUInt16(buffer, index + 3);
                        index       += Const.FALCON_PACKET_HEADER_SIZE;
                        count       -= Const.FALCON_PACKET_HEADER_SIZE;
                    }
                    else
                    {
                        payloadSize = BitConverter.ToUInt16(buffer, index + 1);
                        index       += Const.ADDITIONAL_PACKET_HEADER_SIZE;
                        count       -= Const.ADDITIONAL_PACKET_HEADER_SIZE;

                        // validate size
                        if (payloadSize > count)
                        {
                            Log(LogLevel.Error, String.Format("Dropped last {0} bytes of datagram from {1}, additional size less than min purported: {2}.",
                                count,
                                fromIPEndPoint,
                                payloadSize));
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
                
        private void PunchThroughDiscoveryCallback(IPEndPoint[] endPoints)
        {
            if(punchThroughCallback == null)
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
            peersById.Remove(rp.Id);
            peersByIp.Remove(rp.EndPoint);

            if (sayBye)
            {
                SendToUnknownPeer(rp.EndPoint, PacketType.Bye, SendOptions.None, null);
                Log(LogLevel.Info, String.Format("Removed and saying bye to: {0}.", rp.EndPoint));
            }
            else
            {
                Log(LogLevel.Info, String.Format("Removed: {0}.", rp.EndPoint));
            }

            RaisePeerDropped(rp.Id);
        }

        internal RemotePeer AddPeer(IPEndPoint ip)
        {
            peerIdCount++;
            RemotePeer rp = new RemotePeer(this, ip, peerIdCount);
            peersById.Add(peerIdCount, rp);
            peersByIp.Add(ip, rp);

            // raise PeerAdded event
            if (PeerAdded != null)
                PeerAdded(rp.Id);

            return rp;
        }

        internal void RemovePeerOnNextUpdate(RemotePeer rp)
        {
            remotePeersToRemove.Add(rp);
        }

        [Conditional("DEBUG")]
        internal void Log(LogLevel lvl, string msg)
        {
            if (lvl >= logLvl)
            {
                string line = String.Format("{0}\t{1}\t{2}\t{3}", 
                    DateTime.Now.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fffffff"), 
                    Port, 
                    lvl, 
                    msg);
#if DEBUG
                if (logger != null)
                    logger(lvl, line);
                else
#endif
                    Debug.WriteLine(line);
            }
        }

        internal void RaisePeerDropped(int peerId)
        {
            if (PeerDropped != null)
                PeerDropped(peerId);
        }

        internal void RaisePongReceived(IPEndPoint ipEndPoint, int rtt)
        {
            PongReceivedFromUnknownPeer pongReceivedFromUnknownPeer = PongReceivedFromUnknownPeer;
            if (pongReceivedFromUnknownPeer != null)
                pongReceivedFromUnknownPeer(ipEndPoint, rtt);
        }

        internal void RaisePongReceived(RemotePeer rp, int rtt)
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

            try
            {
                Socket.Close();
            }
            catch { }

            stopped = true;
            Socket = null;
            peersById.Clear();
            peersByIp.Clear();
            Stopwatch.Reset();

            Log(LogLevel.Info, "Stopped");
        }

        /// <summary>
        /// Attempts to start this FalconPeer TODO improve
        /// </summary>
        public FalconOperationResult<object> TryStart()
        {
            // Get local IPv4 address and while doing so broadcast addresses to use for discovery.

            LocalAddresses = new HashSet<IPAddress>();
            broadcastEndPoints = new List<IPEndPoint>();

            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if(nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation addrInfo in props.UnicastAddresses)
                    {
                        if (addrInfo.Address.AddressFamily == AddressFamily.InterNetwork) // i.e. IPv4
                        {
                            // local addr
                            LocalAddresses.Add(addrInfo.Address);

                            // broadcast addr
                            byte[] mask = addrInfo.IPv4Mask == null ? Const.CLASS_C_SUBNET_MASK : addrInfo.IPv4Mask.GetAddressBytes();
                            byte[] addr = addrInfo.Address.GetAddressBytes();
                            for (int i = 0; i < mask.Length; i++)
                                addr[i] = mask[i] == 255 ? addr[i] : (byte)255;
                            broadcastEndPoints.Add(new IPEndPoint(new IPAddress(addr), Port));
                        }
                    }
                }
            }
            catch (NetworkInformationException niex)
            {
                return new FalconOperationResult<object>(niex, null);
            }

            if (LocalAddresses.Count == 0)
                return new FalconOperationResult<object>(false, "No operational IPv4 network interface found.", null);
            
            try
            {
                // create a new socket when starting
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket.SetIPProtectionLevel(IPProtectionLevel.EdgeRestricted);
                Socket.IOControl(-1744830452, new byte[] { 0 }, new byte[] { 0 }); // http://stackoverflow.com/questions/10332630/connection-reset-on-receiving-packet-in-udp-server
                Socket.Bind(anyAddrEndPoint);
                Socket.Blocking = false;
                Socket.EnableBroadcast = true;
            }
            catch (SocketException se)
            {
                // e.g. address already in use
                return new FalconOperationResult<object>(se, null);
            }

            // start the Stopwatch
            Stopwatch = new Stopwatch();
            Stopwatch.Start();

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", this.Port));

            stopped = false;

            return new FalconOperationResult<object>(true, null);
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
        /// <param name="callback"><see cref="FalconOperationCallback"/> callback to call when 
        /// operation completes.</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        public void TryJoinPeerAsync(string addr, int port, FalconOperationCallback<int> callback, string pass = null)
        {
            CheckStarted();

            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                callback(new FalconOperationResult<int>(false, "Invalid IP address supplied.", -1));
            }
            else
            {
                IPEndPoint endPoint = new IPEndPoint(ip, port);
                TryJoinPeerAsync(endPoint, pass, callback);
            }
        }

        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer and FalconOperationResult.Tag will be set to the Id for this remote peer 
        /// which can also be obtained in the <see cref="PeerAdded"/> event. This Method returns 
        /// immediately then calls the callback supplied when the operation completes.</summary>
        /// <param name="endPoint"><see cref="System.Net.IPEndPoint"/> of remote peer.</param>
        /// <param name="callback"><see cref="FalconOperationCallback"/> callback to call when 
        /// operation completes.</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        public void TryJoinPeerAsync(IPEndPoint endPoint, string pass, FalconOperationCallback<int> callback)
        {
            AwaitingAcceptDetail detail = new AwaitingAcceptDetail(endPoint, callback, pass);
            awaitingAcceptDetails.Add(detail);
            TryJoinPeerAsync(detail);
        }

        /// <summary>
        /// Begins a discovery process by emitting discovery signals to connected subnet on port 
        /// and for the time supplied.
        /// </summary>
        /// <param name="millisecondsToWait">Time in milliseconds to wait for replies.</param>
        /// <param name="port">Port number to emit discovery signals to.</param>
        /// <param name="token">Optional <see cref="System.Guid"/> token remote peer requries</param>
        /// <param name="callback"><see cref="DiscoveryCallback"/> to invoke when the operation completes</param>
        /// <remarks><paramref name="token"/> should be null if NOT to be included int the discovery requests.</remarks>
        public void DiscoverFalconPeersAsync(int millisecondsToWait, int port, Guid? token, DiscoveryCallback callback)
        {
            CheckStarted();

            List<IPEndPoint> endPoints = broadcastEndPoints;
            if(port != Port)
            {
                endPoints = new List<IPEndPoint>(broadcastEndPoints.Count);
                broadcastEndPoints.ForEach(ep => endPoints.Add(new IPEndPoint(ep.Address, port)));
            }

            DiscoverFalconPeersAsync(true,
                millisecondsToWait / 1000.0f,
                Settings.DiscoverySignalsToEmit,
                Settings.MaxNumberPeersToDiscover,
                endPoints,
                token,
                callback);
        }

        /// <summary>
        /// Begins a discovery process by emitting signals to <paramref name="publicEndPoint"/>
        /// </summary>
        /// <param name="publicEndPoint"><see cref="IPEndPoint"/> to send discovery signals to.</param>
        /// <param name="millisecondsToWait">Time in millisconds to continue operation for.</param>
        /// <param name="numOfRequests">Number of signals to emit.</param>
        /// <param name="replyToDiscoveryRequestsWithToken"><see cref="Guid"/> token required to solicit a response to.</param>
        public void AssistPunchThroughFromAsync(IPEndPoint publicEndPoint,
            int millisecondsToWait,
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
                replyToDiscoveryRequests = true;
            }

            DiscoverFalconPeersAsync(false,
                millisecondsToWait,
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
        /// <param name="millisecondsToWait">Time in millisconds to continue operation for.</param>
        /// <param name="numOfRequests">Number of signals to emit.</param>
        /// <param name="token"><see cref="Guid"/> token</param>
        /// <param name="callback"><see cref="PunchThroughCallback"/> to invoke once process completes.</param>
        public void PunchThroughToAsync(IEnumerable<IPEndPoint> endPoints, 
            int millisecondsToWait, 
            int numOfRequests, 
            Guid? token,
            PunchThroughCallback callback)
        {
            CheckStarted();

            punchThroughCallback = callback;
            DiscoverFalconPeersAsync(true, millisecondsToWait, numOfRequests, 1, endPoints, token, PunchThroughDiscoveryCallback);
        }

        /// <summary>
        /// Sets all the visibility options for this FalconPeer on the network.
        /// </summary>
        /// <param name="acceptJoinRequests">Set to true to allow other FalconUDP peers to join this one.</param>
        /// <param name="joinPassword">Password FalconUDP peers require to join this one.</param>
        /// <param name="replyToDiscoveryRequests">Set to true to allow other FalconUDP peers discover this one.</param>
        /// <param name="replyToAnonymousPings">Set to true to send reply pong to any FalconUDP even if they have not joined.</param>
        /// <param name="replyToDiscoveryRequestsWithToken">Token incoming discovery requests require if we are to send reply to.</param>
        public void SetVisibility(bool acceptJoinRequests, 
            string joinPassword, 
            bool replyToDiscoveryRequests, 
            bool replyToAnonymousPings = false,
            Guid? replyToDiscoveryRequestsWithToken = null)
        {
            if(joinPassword != null && !acceptJoinRequests)
                throw new ArgumentException("joinPassword must be null if not accepting join requests");
            if(replyToDiscoveryRequestsWithToken != null && !replyToDiscoveryRequests)
                throw new ArgumentException("replyToDiscoveryRequestsWithToken must be null if not to reply to discovery requests");

            this.acceptJoinRequests = acceptJoinRequests;
            this.joinPass = joinPassword;
            this.replyToDiscoveryRequests = replyToDiscoveryRequests;
            this.replyToAnonymousPings = replyToAnonymousPings;

            if (replyToDiscoveryRequestsWithToken.HasValue)
            {
                this.onlyReplyToDiscoveryRequestsWithToken.Add(replyToDiscoveryRequestsWithToken.Value);
            }
        }
        
        /// <summary>
        /// Enqueues packet on be sent to <paramref name="peerId"/> next time <see cref="SendEnquedPackets"/> is called.
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
                detail.Init(peerId, Stopwatch.ElapsedMilliseconds);
                rp.Ping();
                PingsAwaitingPong.Add(detail);
                return true;
            }
        }

        /// <summary>
        /// Ping remote peer.
        /// </summary>
        /// <param name="ipEndPoint"><see cref="IPEndPoint"/> of the of remote peer to ping.</param>
        /// <remarks><see cref="PongReceivedFromUnknownPeer"/>Will be raised, when/if reply Pong is received in time.</remarks>
        public void PingEndPoint(IPEndPoint ipEndPoint)
        {
            CheckStarted();

            PingDetail detail = pingPool.Borrow();
            detail.Init(ipEndPoint, Stopwatch.ElapsedMilliseconds);
            SendToUnknownPeer(ipEndPoint, PacketType.Ping, SendOptions.None, null);
            lock (PingsAwaitingPong)
            {
                PingsAwaitingPong.Add(detail);
            }
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
        /// Removes remote peer.
        /// </summary>
        /// <param name="peerId">Id of the remote peer.</param>
        /// <param name="logFailure">Log failure to remove peer, i.e. if peer is not connected.</param>
        /// <param name="sayBye">Say bye to the remote peer? So can drop us straight away instead 
        /// of waiting to find out we have disconnected.</param>
        public void TryRemovePeer(int peerId, bool logFailure, bool sayBye)
        {
            CheckStarted();

            RemotePeer rp;
            if (!peersById.TryGetValue(peerId, out rp))
            {
                if (logFailure)
                {
                    Log(LogLevel.Error, String.Format("Failed to remove peer with id: {0}, peer unknown.", peerId));
                }
            }
            else
            {
                RemovePeer(rp, sayBye);
            }
        }

        /// <summary>
        /// Removes all remote peers except one with <paramref name="peerId"/>
        /// </summary>
        /// <param name="peerId">Id of peer NOT to remove.</param>
        /// <param name="sayBye">Say bye to the remote peer? So can drop us straight away instead 
        /// of waiting to find out we have disconnected.</param>
        public void RemoveAllPeersExcept(int peerId, bool sayBye)
        {
            CheckStarted();

            int[] ids = new int[peersById.Count]; // TODO garbage :-|
            peersById.Keys.CopyTo(ids, 0);

            foreach(int id in ids)
            {
                if(id == peerId)
                    continue;
                RemovePeer(peersById[id], sayBye);
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
                kv.Value.FlushSendQueues();
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
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                    if (stats.BytesSent > 0 && stats.BytesReceived > 0)
                    {
                        IPInterfaceProperties props = nic.GetIPProperties();
                        foreach(UnicastIPAddressInformation info in props.UnicastAddresses)
                        {
                            if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ipEndPoints.Add(new IPEndPoint(info.Address, Port));
                            }
                        }
                    }
                }
            }
            return ipEndPoints;
        }

        /// <summary>
        /// Simulates latency by delaying outgoing packets <paramref name="milliseondsToDelay"/> plus or minus <paramref name="jitterAboveOrBelowDelay"/>
        /// </summary>
        /// <param name="milliseondsToDelay">Milliseconds to delay outgoing packets for</param>
        /// <param name="jitterAboveOrBelowDelay">Milliseonds plus or minus (chosen randomly) to add to <paramref name="milliseondsToDelay"/></param>
        public void SetSimulateLatency(int milliseondsToDelay, int jitterAboveOrBelowDelay)
        {
            if(milliseondsToDelay < 0)
                throw new ArgumentOutOfRangeException("milliseondsToDelay", "must be equal to 0 (for no delay) or greater than 0 (to simulate delay)");
            if(jitterAboveOrBelowDelay < 0 || jitterAboveOrBelowDelay > milliseondsToDelay)
                throw new ArgumentOutOfRangeException("jitterAboveOrBelowDelay", "cannot be less than 0 or greater than milliseondsToDelay");

            SimulateDelaySecounds = milliseondsToDelay / 1000.0f;
            SimulateDelayJitterMillisecuonds = jitterAboveOrBelowDelay;
        }

        /// <summary>
        /// Simulate packet loss by dropping random outgoing packets.
        /// </summary>
        /// <param name="percentageOfPacketsToDrop">A percentage (from 0.0 to 100.0 inclusive) chance to drop an outgoing packet.</param>
        public void SetSimulatePacketLoss(double percentageOfPacketsToDrop)
        {
            if(percentageOfPacketsToDrop < 0 || percentageOfPacketsToDrop > 100)
                throw new ArgumentOutOfRangeException("percentageOfPacketsToDrop", "must be from 0 to 100, inclusive");

            SimulatePacketLossChance = percentageOfPacketsToDrop / 100;
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
        /// secound) even when nothing may have been sent. </remarks>
        public void Update()
        {
            CheckStarted();

            // NOTE: Stopwatch with a frequency of 2338439 will only loop after 16935 days 14 mins 
            //       and 9 secounds!

            long ellapsedTicks = Stopwatch.ElapsedTicks;
            float ellapsedSeconds = (float)(ellapsedTicks - ellapsedTicksAtLastUpdate) / Stopwatch.Frequency;
            ellapsedTicksAtLastUpdate = ellapsedTicks;
            Update(ellapsedSeconds);
        }
    }
}

