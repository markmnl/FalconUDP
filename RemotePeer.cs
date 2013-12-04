﻿using System;
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

        internal bool IsKeepAliveMaster;                        // i.e. this remote peer is the master so it will send the KeepAlives, not us!

        private int unreadPacketCount;
        private readonly bool keepAliveAndAutoFlush;
        private IPEndPoint endPoint;
        private FalconPeer localPeer;                           // local peer this remote peer has joined
        private ConcurrentGenericObjectPool<PacketDetail> packetDetailPool;
        private List<PacketDetail> sentPacketsAwaitingACK;
        private float ellapasedMilliseondsSinceLastRealiablePacket;  // if this remote peer is the keep alive master; this is since last reliable packet sent to it, otherwise since the last reliable received from it
        private long ellapsedMillisecondsSinceSendQueuesLastFlushed;
        private bool hasEndPointChanged;
        private List<DelayedDatagram> delayedDatagrams;
        private Queue<AckDetail> enqueudAcks;
        private List<Packet> allUnreadPackets;

        // pools
        private SocketAsyncEventArgsPool sendArgsPool;
        private ConcurrentGenericObjectPool<SendToken> tokenPool;
        private ConcurrentGenericObjectPool<AckDetail> ackPool;
        
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
            this.packetDetailPool       = new ConcurrentGenericObjectPool<PacketDetail>(Settings.InitalNumPacketDetailPerPeerToPool);
            this.sendArgsPool           = new SocketAsyncEventArgsPool(Const.MAX_PACKET_SIZE, Settings.InitalNumSendArgsToPoolPerPeer, GetNewSendArgs);
            this.tokenPool              = new ConcurrentGenericObjectPool<SendToken>(Settings.InitalNumSendArgsToPoolPerPeer);
            this.ackPool                = new ConcurrentGenericObjectPool<AckDetail>(Settings.InitalNumAcksToPoolPerPeer);

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
            args.Completed              += OnSendCompleted;
            args.RemoteEndPoint         = endPoint;
            return args;    
        }

        private void OnSendCompleted(object sender, SocketAsyncEventArgs args)
        {
            SendToken token = args.UserToken as SendToken;

            if (args.SocketError != SocketError.Success)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Error: {0}, sending to peer: {1}", args.SocketError, this.PeerName));
                localPeer.RemovePeer(this, false);
            }
            else if (args.BytesTransferred == 0)
            {
                // the remote end has closed the connection TODO is this possible with UDP?
                localPeer.RemovePeer(this, false);
            }

            if (localPeer.IsCollectingStatistics)
            {
                localPeer.Statistics.AddBytesSent(args.Count);
            }

            // return args and it's token to pools
            if (token != null)
                tokenPool.Return(token);
            sendArgsPool.Return(args);
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
            if (localPeer.SimulateDelayMilliseconds > 0 && !hasAlreadyBeenDelayed)
            {
                int delay = localPeer.SimulateDelayMilliseconds;
                if (localPeer.SimulateDelayJitter > 0)
                    delay += SingleRandom.Next(0, localPeer.SimulateDelayJitter * 2) - localPeer.SimulateDelayJitter;

                DelayedDatagram delayedDatagram = new DelayedDatagram
                    {
                        EllapsedMillisecondsSinceDelayed = localPeer.Stopwatch.ElapsedMilliseconds,
                        Datagram = args
                    };

                lock (delayedDatagrams)
                {
                    delayedDatagrams.Add(delayedDatagram);
                }

                return;
            }

            localPeer.Log(LogLevel.Debug, String.Format("Sending {0} bytes to {1}", args.Count, args.RemoteEndPoint));

            if (!localPeer.Socket.SendToAsync(args))
            {
                OnSendCompleted(null, args);
            }
        }

        // writes as many enqued as as can fit into datagram ASSUMPTION lock on enquedAcks obtained
        private void WriteEnquedAcksToDatagram(SocketAsyncEventArgs args, int index)
        {
            while (enqueudAcks.Count > 0 && (Const.MAX_PACKET_SIZE - (index - args.Offset)) > Const.FALCON_PACKET_HEADER_SIZE)
            {
                AckDetail ack = enqueudAcks.Dequeue();
                FalconHelper.WriteAck(ack, args.Buffer, index);
                ackPool.Return(ack);
                index += Const.FALCON_PACKET_HEADER_SIZE;
                args.SetBuffer(args.Offset, index - args.Offset);
            }
        }

        private unsafe void FlushSendChannel(SendChannel channel) 
        {
            lock (channel.ChannelLock)
            {
                Queue<SocketAsyncEventArgs> queue = channel.GetQueue();
                while (queue.Count > 0)
                {
                    SocketAsyncEventArgs args = queue.Dequeue();
                    SendToken token = (SendToken)args.UserToken;
                    long ellapsed = -1;

                    if (channel.IsReliable && token != null) // we know only ACKs do not have a token
                    {
                        ellapsed = localPeer.Stopwatch.ElapsedMilliseconds;

                        if (!token.IsReSend)
                        {
                            // Save detail of this reliable send to measure ACK response time and
                            // in case needs re-sending.

                            PacketDetail detail         = packetDetailPool.Borrow();
                            detail.ChannelType          = token.SendOptions;
                            detail.EllapsedMilliseconds    = ellapsed;
                            detail.Sequence             = BitConverter.ToUInt16(args.Buffer, args.Offset + 1);
                            detail.CopyBytes(args.Buffer, args.Offset, args.Count);

                            lock (sentPacketsAwaitingACK)
                            {
                                sentPacketsAwaitingACK.Add(detail);
                            }
                        }
                        else
                        {
                            // update the time sent TODO include bit in header to indicate is resend so if ACK for previous datagram latency calculated correctly could do packet loss stats too?
                            ushort seq = BitConverter.ToUInt16(args.Buffer, args.Offset + 1);
                            
                            PacketDetail detail;
                            lock(sentPacketsAwaitingACK)
                            {
                                detail = sentPacketsAwaitingACK.Find(pd => pd.Sequence == seq);
                            }

                            if (detail != null)
                            {
                                detail.EllapsedMilliseconds = ellapsed;
                            }
                        }
                    }

                    // If we are the keep alive master (i.e. this remote peer is not) and this 
                    // packet is reliable: update ellpasedMilliseondsAtLastRealiablePacket[Sent]

                    if (!IsKeepAliveMaster && channel.IsReliable)
                    {
                        if(ellapsed == -1)
                            ellapsed = localPeer.Stopwatch.ElapsedMilliseconds;
                        Interlocked.Exchange(ref ellapasedMilliseondsSinceLastRealiablePacket, ellapsed);
                    }

                    // Update the RemoteEndPoint if it has changed (possible when for unknown peer)
                    if (hasEndPointChanged)
                    {
                        args.RemoteEndPoint = this.endPoint;
                    }

                    // append any ACKs awaiting to be sent that will fit in datagram
                    if (enqueudAcks.Count > 0)
                    {
                        if (ellapsed == -1)
                            ellapsed = localPeer.Stopwatch.ElapsedMilliseconds;
                        WriteEnquedAcksToDatagram(args, args.Offset + args.Count);
                    }

                    SendDatagram(args);

                } // while
            } // lock
        }

        private void Pong()
        {
            EnqueueSend(PacketType.Pong, SendOptions.Reliable, null);
            FlushSendChannel(SendOptions.Reliable); // pongs must be sent immediatly as RTT is measured
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
            // NOTE: This method is called by some arbitary thread in the ThreadPool by 
            //       FalconPeer's Timer.

            // counters
            ellapasedMilliseondsSinceLastRealiablePacket += dt;
            ellapsedMillisecondsSinceSendQueuesLastFlushed += dt;

            //
            // ACKs
            //
            if(sentPacketsAwaitingACK.Count > 0)
            {
                for (int i = 0; i < sentPacketsAwaitingACK.Count; i++)
                {
                    PacketDetail pd = sentPacketsAwaitingACK[i];
                    pd.EllapsedMilliseconds += dt;
                    if (pd.EllapsedMilliseconds >= Settings.ACKTimeout)
                    {                        
                        pd.ResentCount++;

                        if (pd.ResentCount > Settings.ACKRetryAttempts)
                        {
                            // give-up, assume the peer has disconnected and drop it
                            sentPacketsAwaitingACK.RemoveAt(i);
                            i--;
                            localPeer.Log(LogLevel.Warning, String.Format("Peer failed to ACK {0} re-sends of Reliable packet in time.", Settings.ACKRetryAttempts));
                            localPeer.RemovePeer(this, false);
                            packetDetailPool.Return(pd);
                        }
                        else
                        {
                            // try again..
                            pd.EllapsedMilliseconds = 0;
                            ReSend(pd);
                            localPeer.Log(LogLevel.Info, String.Format("Packet to: {0} re-sent as not ACKnowledged in time.", PeerName));
                        }
                    }
                }
            }
            //
            // KeepAlives 
            //
            if (keepAliveAndAutoFlush)
            {
                if (IsKeepAliveMaster) // i.e. this remote peer is the keep alive master, not us
                {
                    if (ellapasedMilliseondsSinceLastRealiablePacket >= Settings.KeepAliveIfNoKeepAliveReceived)
                    {
                        // This remote peer has not sent a KeepAlive for too long, send a KeepAlive to 
                        // them to see if they are alive!

                        reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                        ellapasedMilliseondsSinceLastRealiablePacket = 0;
                    }
                }
                else if (ellapasedMilliseondsSinceLastRealiablePacket >= Settings.KeepAliveIfInterval)
                {
                    reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                    ellapasedMilliseondsSinceLastRealiablePacket = 0; // NOTE: this is reset again when packet actually sent but another Update() may occur before then
                }
                //
                // AutoFlush
                //
                if (Settings.AutoFlushInterval > 0)
                {
                    if (ellapsedMillisecondsSinceSendQueuesLastFlushed >= Settings.AutoFlushInterval)
                    {
                        localPeer.Log(LogLevel.Info, "AutoFlush");
                        FlushSendQueues();
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
                    delayedDatagram.EllapsedMillisecondsSinceDelayed += dt;
                    if (delayedDatagram.EllapsedMillisecondsSinceDelayed >= localPeer.SimulateDelayMilliseconds)
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
            lock (enqueudAcks)
            {
                enqueudAcks.Enqueue(ack);
            }
        }

        internal void Accept()
        {
            EnqueueSend(PacketType.AcceptJoin, SendOptions.Reliable, null);
            FlushSendChannel(SendOptions.Reliable);
        }

        internal void Ping()
        {
            EnqueueSend(PacketType.Ping, SendOptions.None, null);
        }

        // used for internal sends that need to be sent immediatly only
        internal void FlushSendChannel(SendOptions channelType)
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
                localPeer.RemovePeer(this, false);
            }
        }

        internal void FlushSendQueues()
        {
            try
            {
                FlushSendChannel(noneSendChannel);
                FlushSendChannel(inOrderSendChannel);
                FlushSendChannel(reliableSendChannel);
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
                localPeer.RemovePeer(this, false);
            }

            ellapsedMillisecondsSinceSendQueuesLastFlushed = 0;
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
                Interlocked.Exchange(ref ellapasedMilliseondsSinceLastRealiablePacket, localPeer.Stopwatch.ElapsedMilliseconds);
            }

            switch (type)
            {
                case PacketType.Application:
                case PacketType.KeepAlive:
                    {
                        lock (ReceiveLock)
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
                            lock(localPeer.PingsAwaitingPong)
                            {
                                PingDetail detail = localPeer.PingsAwaitingPong.Find(pd => pd.PeerIdPingSentTo == Id);
                                if(detail != null)
                                {
                                    localPeer.RaisePongReceived(this, (int)(localPeer.Stopwatch.ElapsedMilliseconds - detail.EllapsedMillisecondsAtSend));
                                    localPeer.RemovePingAwaitingPongDetail(detail);
                                }
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
                        localPeer.RemovePeer(this, false);
                        return false;
                    }
                case PacketType.ACK:
                case PacketType.AntiACK:
                    {
                        lock (sentPacketsAwaitingACK)   // Tick() also uses this collection
                        {
                            // Look for the oldest PacketDetail with the same seq AND channel type
                            // seq is for which we ASSUME the ACK is for.

                            int detailIndex;
                            PacketDetail detail = null;
                            
                            for(detailIndex = 0; detailIndex < sentPacketsAwaitingACK.Count; detailIndex++)
                            {
                                PacketDetail pd = sentPacketsAwaitingACK[detailIndex];
                                if(pd.Sequence == seq && pd.ChannelType == opts)
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
                                UpdateLantency((int)(localPeer.Stopwatch.ElapsedMilliseconds - detail.EllapsedMilliseconds - payloadSize));
                            }
                            else // must be AntiACK
                            {
                                // Re-send the unACKnowledged packet right away NOTE: we are not 
                                // incrementing resent count, we are resetting it, because the remote
                                // peer must be alive to have sent the AntiACK.

                                detail.EllapsedMilliseconds = localPeer.Stopwatch.ElapsedMilliseconds;
                                detail.ResentCount = 0;
                                ReSend(detail);
                            }
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
