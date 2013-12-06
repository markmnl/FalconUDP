using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FalconUDP
{
    // token assigned to each SocketAsyncEventArgs.UserToken
    internal class SendToken
    {
        internal SendOptions SendOptions;
        internal bool IsReSend;
    }

    internal class RemotePeer
    {
        internal int Id { get; private set; }
        internal IPEndPoint EndPoint { get { return endPoint; } }
        internal int UnreadPacketCount { get { return unreadPacketCount; } }    // number of received packets not yet read by application ASSUMPTION lock on ReceiveLock obtained
        internal string PeerName { get; private set; }                          // e.g. IP end point, used for logging
        internal int Latency { get; private set; }

        internal bool IsKeepAliveMaster;                            // i.e. this remote peer is the master so it will send the KeepAlives, not us!

        private int unreadPacketCount;
        private readonly bool keepAliveAndAutoFlush;
        private IPEndPoint endPoint;
        private FalconPeer localPeer;                               // local peer this remote peer has joined
        private GenericObjectPool<PacketDetail> packetDetailPool;
        private List<PacketDetail> sentPacketsAwaitingACK;
        private float ellapasedSecondsSinceLastRealiablePacket;     // if this remote peer is the keep alive master; this is since last reliable packet sent to it, otherwise since the last reliable received from it
        private float ellapsedSecondsSinceSendQueuesLastFlushed;
        private bool hasEndPointChanged;
        private List<DelayedDatagram> delayedDatagrams;
        private Queue<AckDetail> enqueudAcks;
        private List<Packet> allUnreadPackets;

        // pools
        private SocketAsyncEventArgsPool sendArgsPool;
        private GenericObjectPool<SendToken> tokenPool;
        private GenericObjectPool<AckDetail> ackPool;
        
        // channels
        private SendChannel noneSendChannel;
        private SendChannel reliableSendChannel;
        private SendChannel inOrderSendChannel;
        private SendChannel reliableInOrderSendChannel;
        private ReceiveChannel noneReceiveChannel;
        private ReceiveChannel reliableReceiveChannel;
        private ReceiveChannel inOrderReceiveChannel;
        private ReceiveChannel reliableInOrderReceiveChannel;
             
        internal RemotePeer(FalconPeer localPeer, IPEndPoint endPoint, int peerId, bool keepAliveAndAutoFlush = true)
        {
            this.Id                     = peerId;
            this.localPeer              = localPeer;
            this.endPoint               = endPoint;
            this.unreadPacketCount      = 0;
            this.sentPacketsAwaitingACK = new List<PacketDetail>();
            this.PeerName               = endPoint.ToString();
            this.roundTripTimes         = new int[Settings.LatencySampleSize];
            this.delayedDatagrams       = new List<DelayedDatagram>();
            this.keepAliveAndAutoFlush  = keepAliveAndAutoFlush;
            this.allUnreadPackets       = new List<Packet>();
            this.enqueudAcks            = new Queue<AckDetail>();

            // pools
            this.packetDetailPool       = new GenericObjectPool<PacketDetail>(Settings.InitalNumPacketDetailPerPeerToPool);
            this.sendArgsPool           = new SocketAsyncEventArgsPool(Const.MAX_DATAGRAM_SIZE, Settings.InitalNumSendArgsToPoolPerPeer, GetNewSendArgs);
            this.tokenPool              = new GenericObjectPool<SendToken>(Settings.InitalNumSendArgsToPoolPerPeer);
            this.ackPool                = new GenericObjectPool<AckDetail>(Settings.InitalNumAcksToPoolPerPeer);

            // channels
            this.noneSendChannel            = new SendChannel(SendOptions.None, this.sendArgsPool, this.tokenPool, this.localPeer);
            this.inOrderSendChannel         = new SendChannel(SendOptions.InOrder, this.sendArgsPool, this.tokenPool, this.localPeer);
            this.reliableSendChannel        = new SendChannel(SendOptions.Reliable, this.sendArgsPool, this.tokenPool, this.localPeer);
            this.reliableInOrderSendChannel = new SendChannel(SendOptions.ReliableInOrder, this.sendArgsPool, this.tokenPool, this.localPeer);
            this.noneReceiveChannel         = new ReceiveChannel(SendOptions.None, this.localPeer, this);
            this.inOrderReceiveChannel      = new ReceiveChannel(SendOptions.InOrder, this.localPeer, this);
            this.reliableReceiveChannel     = new ReceiveChannel(SendOptions.Reliable, this.localPeer, this);
            this.reliableInOrderReceiveChannel = new ReceiveChannel(SendOptions.ReliableInOrder, this.localPeer, this);
        }

        #region Latency Calc
        private bool hasUpdateLatencyBeenCalled = false;
        private int[] roundTripTimes;
        private int roundTripTimesIndex;
        private int runningRTTTotal;
        private void UpdateLantency(int rtt)
        {
            // If this is the first time this is being called seed entire sample with inital value
            // and set latency to RTT / 2, it's all we have!

            if (!hasUpdateLatencyBeenCalled)
            {
                for (int i = 0; i < roundTripTimes.Length; i++)
                {
                    roundTripTimes[i] = rtt;
                }
                runningRTTTotal = rtt * roundTripTimes.Length;
                Latency = rtt / 2;
                roundTripTimesIndex++;
                hasUpdateLatencyBeenCalled = true;
                return;
            }

            runningRTTTotal -= roundTripTimes[roundTripTimesIndex]; // subtract oldest RTT from running total
            roundTripTimes[roundTripTimesIndex] = rtt;              // replace oldest RTT in sample with new RTT
            runningRTTTotal += rtt;                                 // add new RTT to running total
            Latency = runningRTTTotal / (roundTripTimes.Length * 2);// re-calc average one-way latency

            // increment index for next time this is called
            roundTripTimesIndex++;
            if (roundTripTimesIndex == roundTripTimes.Length)
                roundTripTimesIndex = 0;
        }
        #endregion
        
        // callback used by SocketAsyncEventArgsPool
        private SocketAsyncEventArgs GetNewSendArgs()
        {
            SocketAsyncEventArgs args   = new SocketAsyncEventArgs();
            return args;    
        }

        private void SendDatagram(SocketAsyncEventArgs args, bool hasAlreadyBeenDelayed = false)
        {
            // simulate packet loss
            if (localPeer.SimulatePacketLossChance > 0)
            {
                if (SingleRandom.NextDouble() < localPeer.SimulatePacketLossChance)
                {
                    localPeer.Log(LogLevel.Info, String.Format("Dropped packet to send - simulate packet loss set at: {0}", localPeer.SimulatePacketLossChance));
                    return;
                }
            }

            // simulate delay
            if (localPeer.SimulateDelaySecounds > 0.0f && !hasAlreadyBeenDelayed)
            {
                float delay = localPeer.SimulateDelaySecounds;
                if (localPeer.SimulateDelayJitterMillisecuonds > 0.0f)
                    delay += (SingleRandom.Next(0, localPeer.SimulateDelayJitterMillisecuonds * 2) - localPeer.SimulateDelayJitterMillisecuonds) / 1000.0f;

                DelayedDatagram delayedDatagram = new DelayedDatagram
                    {
                        EllapsedSecondsRemainingToDelay = delay,
                        Datagram = args
                    };
                delayedDatagrams.Add(delayedDatagram);

                return;
            }

            localPeer.Log(LogLevel.Debug, String.Format("Sending {0} bytes to {1}", args.Count, args.RemoteEndPoint));

            try
            {
                //----------------------------------------------------------------------------------------
                localPeer.Socket.SendTo(args.Buffer, args.Offset, args.Count, SocketFlags.None, endPoint);
                //----------------------------------------------------------------------------------------
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Error: {0} {1}, sending to peer: {2}", se.ErrorCode, se.Message, PeerName));
                localPeer.RemovePeerOnNextUpdate(this); 
            }

            if (localPeer.IsCollectingStatistics)
            {
                localPeer.Statistics.AddBytesSent(args.Count);
            }

            // return args and it's token to pools
            SendToken token = args.UserToken as SendToken;
            if (token != null)
                tokenPool.Return(token);
            sendArgsPool.Return(args);
        }

        // writes as many enqued as as can fit into datagram
        private void WriteEnquedAcksToDatagram(SocketAsyncEventArgs args, int index)
        {
            while (enqueudAcks.Count > 0 && (Const.MAX_DATAGRAM_SIZE - (index - args.Offset)) > Const.FALCON_PACKET_HEADER_SIZE)
            {
                AckDetail ack = enqueudAcks.Dequeue();
                FalconHelper.WriteAck(ack, args.Buffer, index);
                ackPool.Return(ack);
                index += Const.FALCON_PACKET_HEADER_SIZE;
                args.SetBuffer(args.Offset, index - args.Offset);
            }
        }

        private void FlushSendChannel(SendChannel channel)
        {
            Queue<SocketAsyncEventArgs> queue = channel.GetQueue();

            while (queue.Count > 0)
            {
                SocketAsyncEventArgs args = queue.Dequeue();
                SendToken token = (SendToken)args.UserToken;

                if (channel.IsReliable && token != null) // we know only ACKs do not have a token
                {
                    if (token.IsReSend)
                    {
                        // update the time sent TODO include bit in header to indicate is resend so if ACK for previous datagram latency calculated correctly could do packet loss stats too?
                        ushort seq = BitConverter.ToUInt16(args.Buffer, args.Offset + 1);
                        for (int i = 0; i < sentPacketsAwaitingACK.Count; i++)
                        {
                            PacketDetail detail = sentPacketsAwaitingACK[i];
                            if (detail.Sequence == seq)
                            {
                                detail.EllapsedSecoundsSincePacketSent = 0.0f;
                                break;
                            }
                        }
                    }
                    else // i.e. not a re-send
                    {
                        // Save detail of this reliable send to measure ACK response time and
                        // in case needs re-sending.

                        PacketDetail detail = packetDetailPool.Borrow();
                        detail.ChannelType = token.SendOptions;
                        detail.EllapsedSecoundsSincePacketSent = 0.0f;
                        detail.Sequence = BitConverter.ToUInt16(args.Buffer, args.Offset + 1);
                        detail.CopyBytes(args.Buffer, args.Offset, args.Count);
                        sentPacketsAwaitingACK.Add(detail);                        
                    }
                }

                // If we are the keep alive master (i.e. this remote peer is not) and this 
                // packet is reliable: update ellpasedMilliseondsAtLastRealiablePacket[Sent]

                if (!IsKeepAliveMaster && channel.IsReliable)
                {
                    ellapasedSecondsSinceLastRealiablePacket = 0.0f;
                }

                // Update the RemoteEndPoint if it has changed (possible when for unknown peer)
                if (hasEndPointChanged)
                {
                    args.RemoteEndPoint = this.endPoint;
                }

                // append any ACKs awaiting to be sent that will fit in datagram
                if (enqueudAcks.Count > 0)
                {
                    WriteEnquedAcksToDatagram(args, args.Offset + args.Count);
                }

                SendDatagram(args);

            } // while

            channel.ResetCount();
        }

        private void Pong()
        {
            EnqueueSend(PacketType.Pong, SendOptions.Reliable, null);
            ForceFlushSendChannelNow(SendOptions.Reliable); // pongs must be sent immediatly as RTT is measured
        }

        private void DiscoverReply()
        {
            EnqueueSend(PacketType.DiscoverReply, SendOptions.None, null);
        }

        private void ReSend(PacketDetail detail)
        {
            SocketAsyncEventArgs args = sendArgsPool.Borrow();
            args.SetBuffer(detail.Bytes, 0, detail.Count);

            SendToken token = tokenPool.Borrow();
            token.IsReSend = true;

            args.UserToken = token;

            switch (detail.ChannelType)
            {
                case SendOptions.Reliable:
                    reliableSendChannel.EnqueueSend(args);
                    break;
                case SendOptions.ReliableInOrder:
                    reliableInOrderSendChannel.EnqueueSend(args);
                    break;
                default:
                    throw new InvalidOperationException(String.Format("{0} packets cannot be re-sent!", detail.ChannelType));
            }
        }

        internal void Update(float dt)
        {
            //
            // Update counters
            //
            ellapasedSecondsSinceLastRealiablePacket += dt;
            ellapsedSecondsSinceSendQueuesLastFlushed += dt;
            //
            // Update enqued ACKs stopover time
            //
            if (enqueudAcks.Count > 0)
            {
                foreach (AckDetail detail in enqueudAcks)
                {
                    detail.EllapsedSecondsSinceEnqueud += dt;
                }
            }
            //
            // Packets awaiting ACKs
            //
            if(sentPacketsAwaitingACK.Count > 0)
            {
                for (int i = 0; i < sentPacketsAwaitingACK.Count; i++)
                {
                    PacketDetail pd = sentPacketsAwaitingACK[i];
                    pd.EllapsedSecoundsSincePacketSent += dt;
                    if (pd.EllapsedSecoundsSincePacketSent >= Settings.ACKTimeout)
                    {                        
                        pd.ResentCount++;

                        if (pd.ResentCount > Settings.ACKRetryAttempts)
                        {
                            // give-up, assume the peer has disconnected and drop it
                            sentPacketsAwaitingACK.RemoveAt(i);
                            i--;
                            localPeer.Log(LogLevel.Warning, String.Format("Peer failed to ACK {0} re-sends of Reliable packet in time.", Settings.ACKRetryAttempts));
                            localPeer.RemovePeerOnNextUpdate(this);
                            packetDetailPool.Return(pd);
                        }
                        else
                        {
                            // try again..
                            pd.EllapsedSecoundsSincePacketSent = 0.0f;
                            ReSend(pd);
                            localPeer.Log(LogLevel.Info, String.Format("Packet to: {0} re-sent as not ACKnowledged in time.", PeerName));
                        }
                    }
                }
            }
            //
            // KeepAlives and AutoFlush
            //
            if (keepAliveAndAutoFlush)
            {
                if (IsKeepAliveMaster) // i.e. this remote peer is the keep alive master, not us
                {
                    if (ellapasedSecondsSinceLastRealiablePacket >= Settings.KeepAliveIfNoKeepAliveReceived)
                    {
                        // This remote peer has not sent a KeepAlive for too long, send a KeepAlive to 
                        // them to see if they are alive!

                        reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                        ellapasedSecondsSinceLastRealiablePacket = 0.0f;
                    }
                }
                else if (ellapasedSecondsSinceLastRealiablePacket >= Settings.KeepAliveIfInterval)
                {
                    reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                    ellapasedSecondsSinceLastRealiablePacket = 0.0f; // NOTE: this is reset again when packet actually sent but another Update() may occur before then
                }

                if (Settings.AutoFlushInterval > 0.0f)
                {
                    if (ellapsedSecondsSinceSendQueuesLastFlushed >= Settings.AutoFlushInterval)
                    {
                        localPeer.Log(LogLevel.Info, "AutoFlush");
                        FlushSendQueues(); // resets ellapsedSecondsSinceSendQueuesLastFlushed
                    }
                }
            }
            //
            // Simulate Delay
            // 
            if (delayedDatagrams.Count > 0)
            {
                for (int i = 0; i < delayedDatagrams.Count; i++)
                {
                    DelayedDatagram delayedDatagram = delayedDatagrams[i];
                    delayedDatagram.EllapsedSecondsRemainingToDelay -= dt;
                    if (delayedDatagram.EllapsedSecondsRemainingToDelay <= 0.0f)
                    {
                        SendDatagram(delayedDatagram.Datagram, true);
                        delayedDatagrams.RemoveAt(i);
                        i--;
                    }
                }
            }
        }

        internal void UpdateEndPoint(IPEndPoint ip)
        {
            endPoint = ip;
            PeerName = ip.ToString();
            hasEndPointChanged = true;
        }

        internal void EnqueueSend(PacketType type, SendOptions opts, Packet packet)
        {
            if(packet != null)
                packet.IsReadOnly = true;
            
            switch (opts)
            {
                case SendOptions.None:
                    noneSendChannel.EnqueueSend(type, packet);
                    break;
                case SendOptions.InOrder:
                    inOrderSendChannel.EnqueueSend(type, packet);
                    break;
                case SendOptions.Reliable:
                    reliableSendChannel.EnqueueSend(type, packet);
                    break;
                case SendOptions.ReliableInOrder:
                    reliableInOrderSendChannel.EnqueueSend(type, packet);
                    break;
            }
        }

        internal void ACK(ushort seq, PacketType type, SendOptions channelType)
        {
            AckDetail ack = ackPool.Borrow();
            ack.Init(seq, channelType, type);
            enqueudAcks.Enqueue(ack);
        }

        internal void Accept()
        {
            EnqueueSend(PacketType.AcceptJoin, SendOptions.Reliable, null);
            ForceFlushSendChannelNow(SendOptions.Reliable);
        }

        internal void Ping()
        {
            EnqueueSend(PacketType.Ping, SendOptions.None, null);
        }

        // used for internal sends that need to be sent immediatly only
        internal void ForceFlushSendChannelNow(SendOptions channelType)
        {
            SendChannel channel = null;
            switch (channelType)
            {
                case SendOptions.None: channel = noneSendChannel; break;
                case SendOptions.InOrder: channel = inOrderSendChannel; break;
                case SendOptions.Reliable: channel = reliableSendChannel; break;
                case SendOptions.ReliableInOrder: channel = reliableInOrderSendChannel; break;
            }

            try
            {
                FlushSendChannel(channel);
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Exception: {0}, sending to peer: {1}", se.Message, PeerName));
                localPeer.RemovePeerOnNextUpdate(this);
            }
        }

        internal void FlushSendQueues()
        {
            try
            {
                if (noneSendChannel.Count > 0)
                    FlushSendChannel(noneSendChannel);
                if (inOrderSendChannel.Count > 0)
                    FlushSendChannel(inOrderSendChannel);
                if (reliableSendChannel.Count > 0)
                    FlushSendChannel(reliableSendChannel);
                if (reliableInOrderSendChannel.Count > 0)
                    FlushSendChannel(reliableInOrderSendChannel);

                // send any outstanding ACKs
                if (enqueudAcks.Count > 0)
                {
                    while (enqueudAcks.Count > 0)
                    {    
                        SocketAsyncEventArgs args = sendArgsPool.Borrow();
                        WriteEnquedAcksToDatagram(args, args.Offset);
                        SendDatagram(args);
                    }
                }
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Exception: {0}, sending to peer: {1}", se.Message, PeerName));
                localPeer.RemovePeerOnNextUpdate(this);
            }

            ellapsedSecondsSinceSendQueuesLastFlushed = 0.0f;
        }

        // returns true if caller should continue adding any additional packets in datagram
        internal bool TryAddReceivedPacket(ushort seq, 
            SendOptions opts, 
            PacketType type, 
            byte[] buffer,
            int index,
            int payloadSize,
            bool isFirstPacketInDatagram)
        {
            // If we are not the keep alive master, i.e. this remote peer is, and this packet was 
            // sent reliably reset ellpasedMilliseondsAtLastRealiablePacket[Received].

            if (IsKeepAliveMaster && opts.HasFlag(SendOptions.Reliable))
            {
                ellapasedSecondsSinceLastRealiablePacket = 0.0f;
            }

            switch (type)
            {
                case PacketType.Application:
                case PacketType.KeepAlive:
                    {
                        bool wasAppPacketAdded;

                        ReceiveChannel channel = noneReceiveChannel;
                        switch (opts)
                        {
                            case SendOptions.InOrder:           channel = inOrderReceiveChannel;            break;
                            case SendOptions.Reliable:          channel = reliableReceiveChannel;           break;
                            case SendOptions.ReliableInOrder:   channel = reliableInOrderReceiveChannel;    break;
                        }

                        if(!channel.TryAddReceivedPacket(seq, type, buffer, index, payloadSize, isFirstPacketInDatagram, out wasAppPacketAdded))
                            return false;

                        if (wasAppPacketAdded)
                            unreadPacketCount++;

                        return true;
                    }
                case PacketType.Ping:
                    {
                        Pong();
                        return true;
                    }
                case PacketType.Pong:
                    {
                        if(localPeer.HasPingsAwaitingPong)
                        {
                            PingDetail detail = localPeer.PingsAwaitingPong.Find(pd => pd.PeerIdPingSentTo == Id);
                            if(detail != null)
                            {
                                localPeer.RaisePongReceived(this, (int)(localPeer.Stopwatch.ElapsedMilliseconds - detail.EllapsedMillisecondsAtSend));
                                localPeer.RemovePingAwaitingPongDetail(detail);
                            }
                        }
                        
                        return true;
                    }
                case PacketType.JoinRequest:
                    {
                        // Must be hasn't received Accept yet or is joining again! (silly peer)
                        Accept();
                        return true;
                    }
                case PacketType.DiscoverRequest:
                    {
                        DiscoverReply();
                        return true;
                    }
                case PacketType.DiscoverReply:
                    {
                        // do nothing, DiscoveryReply only relevant when peer not added
                        return true;
                    }
                case PacketType.Bye:
                    {
                        localPeer.Log(LogLevel.Info, String.Format("Bye received from: {0}.", PeerName));
                        localPeer.RemovePeerOnNextUpdate(this);
                        return false;
                    }
                case PacketType.ACK:
                case PacketType.AntiACK:
                    {
                        // Look for the oldest PacketDetail with the same seq AND channel type
                        // seq is for which we ASSUME the ACK is for.

                        int detailIndex;
                        PacketDetail detail = null;

                        for (detailIndex = 0; detailIndex < sentPacketsAwaitingACK.Count; detailIndex++)
                        {
                            PacketDetail pd = sentPacketsAwaitingACK[detailIndex];
                            if (pd.Sequence == seq && pd.ChannelType == opts)
                            {
                                detail = pd;
                                break;
                            }
                        }   

                        if (detail == null)
                        {
                            // Possible reasons in order of likelyhood:
                            // 1) ACK has arrived too late and the packet must have already been removed.
                            // 2) ACK duplicated and has already been processed
                            // 3) ACK was unsolicited (i.e. malicious or buggy peer)

                            localPeer.Log(LogLevel.Warning, "Packet for ACK not found - too late?");
                            return true;
                        }

                        if (type == PacketType.ACK)
                        {
                            // remove packet detail
                            sentPacketsAwaitingACK.RemoveAt(detailIndex);

                            // update latency estimate (payloadSize is stopover time on remote peer)
                            UpdateLantency((int)(detail.EllapsedSecoundsSincePacketSent - payloadSize));
                        }
                        else // must be AntiACK
                        {
                            // Re-send the unACKnowledged packet right away NOTE: we are not 
                            // incrementing resent count, we are resetting it, because the remote
                            // peer must be alive to have sent the AntiACK.

                            detail.EllapsedSecoundsSincePacketSent = 0.0f;
                            detail.ResentCount = 0;
                            ReSend(detail);
                        }

                        return true;
                    }
                default:
                    {
                        localPeer.Log(LogLevel.Warning, String.Format("Packet dropped - unexpected type: {0}, received from authenticated peer: {1}.", type, PeerName));
                        return true; // the packet is valid just unexpected..
                    }
            }
        }

        // ASSUMPTION: Caller has checked UnreadPacketCount > 0
        internal List<Packet> Read()
        {
            allUnreadPackets.Clear();

            allUnreadPackets.Capacity = UnreadPacketCount;

            if (noneReceiveChannel.Count > 0)
                allUnreadPackets.AddRange(noneReceiveChannel.Read());
            if (inOrderReceiveChannel.Count > 0)
                allUnreadPackets.AddRange(inOrderReceiveChannel.Read());
            if (reliableReceiveChannel.Count > 0)
                allUnreadPackets.AddRange(reliableReceiveChannel.Read());
            if (reliableInOrderReceiveChannel.Count > 0)
                allUnreadPackets.AddRange(reliableInOrderReceiveChannel.Read());

            unreadPacketCount = 0;
            
            return allUnreadPackets;
        }
    }
}
