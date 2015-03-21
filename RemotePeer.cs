using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FalconUDP
{
    internal class RemotePeer
    {
        private readonly bool keepAliveAndAutoFlush;
        private readonly FalconPeer localPeer;                                  // local peer this remote peer has joined
        private readonly DatagramPool sendDatagramsPool;
        private readonly List<Datagram> sentDatagramsAwaitingACK;
        private readonly List<DelayedDatagram> delayedDatagrams;
        private readonly Queue<AckDetail> enqueudAcks;
        private readonly List<Packet> allUnreadPackets;
        private readonly SendChannel noneSendChannel, reliableSendChannel, inOrderSendChannel, reliableInOrderSendChannel;
        private readonly ReceiveChannel noneReceiveChannel, reliableReceiveChannel, inOrderReceiveChannel, reliableInOrderReceiveChannel;
        private readonly QualityOfService qualityOfService;
        private int unreadPacketCount;
        private IPEndPoint endPoint;
        private float ellapasedSecondsSinceLastRealiablePacket;                 // if this remote peer is the keep alive master; this is since last reliable packet sent to it, otherwise since the last reliable received from it
        private float ellapsedSecondsSinceSendQueuesLastFlushed;

        internal bool IsKeepAliveMaster;                                        // i.e. this remote peer is the master so it will send the KeepAlives, not us!
        internal int Id { get; private set; }
        internal IPEndPoint EndPoint { get { return endPoint; } }
        internal int UnreadPacketCount { get { return unreadPacketCount; } }    // number of received packets not yet read by application
        internal string PeerName { get; private set; }                          // e.g. IP end point, used for logging
        internal TimeSpan RoundTripTime { get { return qualityOfService.RoudTripTime;  } }
        internal QualityOfService QualityOfService { get { return qualityOfService; } }
             
        internal RemotePeer(FalconPeer localPeer, IPEndPoint endPoint, int peerId, bool keepAliveAndAutoFlush = true)
        {
            this.Id                         = peerId;
            this.localPeer                  = localPeer;
            this.endPoint                   = endPoint;
#if DEBUG
            this.PeerName                   = endPoint.ToString();
#else
            this.PeerName                   = String.Empty;
#endif
            this.unreadPacketCount          = 0;
            this.sendDatagramsPool          = new DatagramPool(FalconPeer.MaxDatagramSize, localPeer.PoolSizes.InitalNumSendDatagramsToPoolPerPeer);
            this.sentDatagramsAwaitingACK   = new List<Datagram>();
            this.qualityOfService           = new QualityOfService(localPeer.LatencySampleSize, localPeer.ResendRatioSampleSize);
            
            this.delayedDatagrams           = new List<DelayedDatagram>();
            this.keepAliveAndAutoFlush      = keepAliveAndAutoFlush;
            this.allUnreadPackets           = new List<Packet>();
            this.enqueudAcks                = new Queue<AckDetail>();
            this.noneSendChannel            = new SendChannel(SendOptions.None, sendDatagramsPool);
            this.inOrderSendChannel         = new SendChannel(SendOptions.InOrder, sendDatagramsPool);
            this.reliableSendChannel        = new SendChannel(SendOptions.Reliable, sendDatagramsPool);
            this.reliableInOrderSendChannel = new SendChannel(SendOptions.ReliableInOrder, sendDatagramsPool);
            this.noneReceiveChannel         = new ReceiveChannel(SendOptions.None, this.localPeer, this);
            this.inOrderReceiveChannel      = new ReceiveChannel(SendOptions.InOrder, this.localPeer, this);
            this.reliableReceiveChannel     = new ReceiveChannel(SendOptions.Reliable, this.localPeer, this);
            this.reliableInOrderReceiveChannel = new ReceiveChannel(SendOptions.ReliableInOrder, this.localPeer, this);
        }
        
        private bool TrySendDatagram(Datagram datagram, bool hasAlreadyBeenDelayed = false)
        {
            // If we are the keep alive master (i.e. this remote peer is not) and this 
            // packet is reliable: update ellpasedMilliseondsAtLastRealiablePacket[Sent]
            if (!IsKeepAliveMaster && datagram.IsReliable)
            {
                ellapasedSecondsSinceLastRealiablePacket = 0.0f;
            }

            // simulate packet loss
            if (localPeer.SimulatePacketLossProbability > 0.0)
            {
                if (SingleRandom.NextDouble() < localPeer.SimulatePacketLossProbability)
                {
                    localPeer.Log(LogLevel.Info, String.Format("DROPPED packet to send - simulate packet loss set at: {0}", localPeer.SimulatePacketLossChance));
                    return true;
                }
            }

            // if reliable and not already delayed update time sent used to measure RTT when ACK response received
            if (datagram.IsReliable && !hasAlreadyBeenDelayed)
            {
                datagram.EllapsedAtSent = localPeer.Stopwatch.Elapsed;
            }

            // simulate delay
            if (localPeer.SimulateLatencySeconds > 0.0f && !hasAlreadyBeenDelayed)
            {
                float delay = localPeer.SimulateLatencySeconds;

                // jitter
                if (localPeer.SimulateJitterSeconds > 0.0f)
                {
                    float jitter = localPeer.SimulateJitterSeconds * (float)SingleRandom.NextDouble();
                    if (SingleRandom.NextDouble() < 0.5)
                        jitter *= -1;

                    delay += jitter;
                }

                DelayedDatagram delayedDatagram = new DelayedDatagram(delay, datagram);

                localPeer.Log(LogLevel.Debug, String.Format("...DELAYED Sending datagram to: {0}, seq {1}, channel: {2}, total size: {3}; by {4}s...",
                    endPoint.ToString(),
                    datagram.Sequence.ToString(),
                    datagram.SendOptions.ToString(),
                    datagram.Count.ToString(),
                    delayedDatagram.EllapsedSecondsRemainingToDelay.ToString()));

                delayedDatagrams.Add(delayedDatagram);

                return true;
            }

            localPeer.Log(LogLevel.Debug, String.Format("--> Sending datagram to: {0}, seq {1}, channel: {2}, total size: {3}...", 
                endPoint.ToString(),
                datagram.Sequence.ToString(), 
                datagram.SendOptions.ToString(),
                datagram.Count.ToString()));

            //-----------------------------------------------------------------------------------------------------------------
            bool success = localPeer.Transceiver.Send(datagram.BackingBuffer, datagram.Offset, datagram.Count, endPoint, true);
            //-----------------------------------------------------------------------------------------------------------------
            
            if (success && localPeer.IsCollectingStatistics)
            {
                localPeer.Statistics.AddBytesSent(datagram.Count);
            }

            // return the datagram to pool for re-use if we are not waiting for an ACK
            if (success && !datagram.IsReliable)
            {
                sendDatagramsPool.Return(datagram);
            }

            return success;
        }

        // writes as many enqued as as can fit into datagram
        private void WriteEnquedAcksToDatagram(Datagram datagram, int index)
        {
            while (enqueudAcks.Count > 0 && (datagram.MaxSize - (index - datagram.Offset)) > Const.FALCON_PACKET_HEADER_SIZE)
            {
                AckDetail ack = enqueudAcks.Dequeue();
                FalconHelper.WriteAck(ack, datagram.BackingBuffer, index);
                localPeer.AckPool.Return(ack);
                index += Const.FALCON_PACKET_HEADER_SIZE;
            }
            datagram.Resize(index - datagram.Offset);
        }

        private bool TryFlushSendChannel(SendChannel channel)
        {
            if (!channel.HasDataToSend)
                return true;

            Queue<Datagram> queue = channel.GetQueue();

            while (queue.Count > 0)
            {
                Datagram datagram = queue.Dequeue();

                if (channel.IsReliable)
                {
                    if (datagram.IsResend)
                    {
                        // update the time sent TODO include bit in header to indicate is resend so if ACK for previous datagram latency calculated correctly could do packet loss stats too?
                        datagram.EllapsedSecondsSincePacketSent = 0.0f;
                    }
                    else // i.e. not a re-send
                    {
                        // Save to this reliable send to measure ACK response time and
                        // in case needs re-sending.
                        sentDatagramsAwaitingACK.Add(datagram);
                    }
                }

                // append any ACKs awaiting to be sent that will fit in datagram
                if (enqueudAcks.Count > 0)
                {
                    WriteEnquedAcksToDatagram(datagram, datagram.Offset + datagram.Count);
                }

                bool success = TrySendDatagram(datagram);
                if (!success)
                    return false;

            } // while

            return true;
        }

        private void Pong()
        {
            EnqueueSend(PacketType.Pong, SendOptions.None, null);
            ForceFlushSendChannelNow(SendOptions.None); // pongs must be sent immediatly as RTT is measured
        }

        private void DiscoverReply()
        {
            EnqueueSend(PacketType.DiscoverReply, SendOptions.None, null);
        }

        private void ReSend(Datagram datagram)
        {
            datagram.IsResend = true;

            qualityOfService.UpdateResentSample(true);

            switch (datagram.SendOptions)
            {
                case SendOptions.Reliable:
                    reliableSendChannel.EnqueueSend(datagram);
                    break;
                case SendOptions.ReliableInOrder:
                    reliableInOrderSendChannel.EnqueueSend(datagram);
                    break;
                default:
                    throw new InvalidOperationException(String.Format("{0} datagrams cannot be re-sent!", datagram.SendOptions));
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
                    detail.EllapsedSecondsSinceEnqueued += dt;
                }
            }
            //
            // Datagrams awaiting ACKs
            //
            if(sentDatagramsAwaitingACK.Count > 0)
            {
                for (int i = 0; i < sentDatagramsAwaitingACK.Count; i++)
                {
                    Datagram datagramAwaitingAck = sentDatagramsAwaitingACK[i];
                    datagramAwaitingAck.EllapsedSecondsSincePacketSent += dt;

                    if (datagramAwaitingAck.EllapsedSecondsSincePacketSent >= localPeer.AckTimeoutSeconds)
                    {
                        datagramAwaitingAck.ResentCount++;

                        if (datagramAwaitingAck.ResentCount > localPeer.MaxResends)
                        {
                            // give-up, assume the peer has disconnected (without saying bye) and drop it
                            sentDatagramsAwaitingACK.RemoveAt(i);
                            i--;

                            localPeer.Log(LogLevel.Warning, String.Format("Peer failed to ACK {0} re-sends of Reliable datagram seq {1}, channel {2}. total size: {3}, in time.",
                                localPeer.MaxResends,
                                datagramAwaitingAck.Sequence.ToString(),
                                datagramAwaitingAck.SendOptions.ToString(),
                                datagramAwaitingAck.Count.ToString()));

                            sendDatagramsPool.Return(datagramAwaitingAck);
                            localPeer.RemovePeerOnNextUpdate(this);
                            return;
                        }
                        else
                        {
                            // try again..
                            datagramAwaitingAck.EllapsedSecondsSincePacketSent = 0.0f;
                            ReSend(datagramAwaitingAck);
                            ellapasedSecondsSinceLastRealiablePacket = 0.0f; // NOTE: this is reset again when packet actually sent but another Update() may occur before then

                            localPeer.Log(LogLevel.Info, String.Format("Datagram to: {0}, seq {1}, channel: {2}, total size: {3} re-sent as not ACKnowledged in time.",
                                PeerName,
                                datagramAwaitingAck.Sequence.ToString(),
                                datagramAwaitingAck.SendOptions.ToString(),
                                datagramAwaitingAck.Count.ToString()));
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
                    if (ellapasedSecondsSinceLastRealiablePacket >= localPeer.KeepAliveProbeAfterSeconds)
                    {
                        // This remote peer has not sent a reliable message for too long, send a 
                        // KeepAlive to probe them and they are alive!

                        reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                        ellapasedSecondsSinceLastRealiablePacket = 0.0f;
                    }
                }
                else if (ellapasedSecondsSinceLastRealiablePacket >= localPeer.KeepAliveIntervalSeconds)
                {
                    reliableSendChannel.EnqueueSend(PacketType.KeepAlive, null);
                    ellapasedSecondsSinceLastRealiablePacket = 0.0f; // NOTE: this is reset again when packet actually sent but another Update() may occur before then
                }

                if (localPeer.AutoFlushIntervalSeconds > 0.0f)
                {
                    if (ellapsedSecondsSinceSendQueuesLastFlushed >= localPeer.AutoFlushIntervalSeconds)
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
                        bool success = TrySendDatagram(delayedDatagram.Datagram, true);
                        if(!success)
                        {
                            localPeer.RemovePeerOnNextUpdate(this);
                            return;
                        }
                        delayedDatagrams.RemoveAt(i);
                        i--;
                    }
                }
            }
        }

        internal void UpdateEndPoint(IPEndPoint ip)
        {
            endPoint = ip;
#if DEBUG 
            PeerName = ip.ToString();
#else
            // PeerName is not used in Release builds so do don't create the garbage 
            PeerName = String.Empty;
#endif
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

        internal void ACK(ushort seq, SendOptions channelType)
        {
            AckDetail ack = localPeer.AckPool.Borrow();
            ack.Init(seq, channelType);
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

        internal void Bye()
        {
            EnqueueSend(PacketType.Bye, SendOptions.None, null);
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

            bool success = TryFlushSendChannel(channel);

            if(!success)    
                localPeer.RemovePeerOnNextUpdate(this);
        }

        internal void FlushSendQueues()
        {
            bool success = true;

            success = TryFlushSendChannel(noneSendChannel);
            if (success)
                success = TryFlushSendChannel(inOrderSendChannel);
            if(success)
                success = TryFlushSendChannel(reliableSendChannel);
            if (success)
                success = TryFlushSendChannel(reliableInOrderSendChannel);

            // send any outstanding ACKs
            if (enqueudAcks.Count > 0)
            {
                while (enqueudAcks.Count > 0)
                {    
                    Datagram datagram = sendDatagramsPool.Borrow();
                    datagram.SendOptions = SendOptions.None;
                    WriteEnquedAcksToDatagram(datagram, datagram.Offset);
                    success = TrySendDatagram(datagram);
                    if (!success)
                        break;
                }
            }

            if(!success)
                localPeer.RemovePeerOnNextUpdate(this);

            ellapsedSecondsSinceSendQueuesLastFlushed = 0.0f;
        }

        // returns true if caller should continue adding any additional packets in datagram
        internal bool TryAddReceivedPacket(
            ushort      seq, 
            SendOptions opts, 
            PacketType  type, 
            byte[]      buffer,
            int         index,
            int         payloadSize,
            bool        isFirstPacketInDatagram)
        {
            // If we are not the keep alive master, i.e. this remote peer is, and this packet was 
            // sent reliably reset ellpasedMilliseondsAtLastRealiablePacket[Received].
            if (IsKeepAliveMaster && ((opts & SendOptions.Reliable) == SendOptions.Reliable))
            {
                ellapasedSecondsSinceLastRealiablePacket = 0.0f;
            }

            switch (type)
            {
                case PacketType.Application:
                case PacketType.KeepAlive:
                case PacketType.AcceptJoin:
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
                case PacketType.ACK:
                    {
                        // Look for the oldest datagram with the same seq AND channel type
                        // seq is for which we ASSUME the ACK is for.
                        int datagramIndex;
                        Datagram sentDatagramAwaitingAck = null;
                        for (datagramIndex = 0; datagramIndex < sentDatagramsAwaitingACK.Count; datagramIndex++)
                        {
                            Datagram datagram = sentDatagramsAwaitingACK[datagramIndex];
                            if (datagram.Sequence == seq && datagram.SendOptions == opts)
                            {
                                sentDatagramAwaitingAck = datagram;
                                break;
                            }
                        }

                        if (sentDatagramAwaitingAck == null)
                        {
                            // Possible reasons:
                            // 1) ACK has arrived too late and the datagram must have already been removed.
                            // 2) ACK duplicated and has already been processed
                            // 3) ACK was unsolicited (i.e. malicious or buggy peer)

                            localPeer.Log(LogLevel.Warning, "Datagram for ACK not found - too late?");
                        }
                        else
                        {
                            // remove datagram
                            sentDatagramsAwaitingACK.RemoveAt(datagramIndex);

                            // Update QoS

                            // If the datagram was not re-sent update latency, otherwise we do not 
                            // know which send this ACK is for so cannot determine latency.

                            // Also update re-sends sample if datagram was not re-sent with: not 
                            // re-sent. If was re-sent sample would have already been updated when 
                            // re-sent.

                            if (sentDatagramAwaitingAck.ResentCount == 0)
                            {
                                TimeSpan rtt = localPeer.Stopwatch.Elapsed - sentDatagramAwaitingAck.EllapsedAtSent;
                                qualityOfService.UpdateLatency(rtt);
                                qualityOfService.UpdateResentSample(false);

                                localPeer.Log(LogLevel.Debug, String.Format("ACK from: {0}, channel: {1}, seq: {2}, RTT: {3}s", PeerName, opts.ToString(), seq.ToString(), rtt.TotalSeconds.ToString()));
                                localPeer.Log(LogLevel.Debug, String.Format("Latency now: {0}s", qualityOfService.RoudTripTime.TotalSeconds.ToString()));
                            }
                            else
                            {
                                localPeer.Log(LogLevel.Info, String.Format("ACK for re-sent datagram: {0}, channel: {1}, seq: {2}", PeerName, opts.ToString(), seq.ToString()));
                            }


                            // return datagram
                            sendDatagramsPool.Return(sentDatagramAwaitingAck);
                        }

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
                                localPeer.RaisePongReceived(this, TimeSpan.FromSeconds(detail.EllapsedSeconds));
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
                default:
                    {
                        localPeer.Log(LogLevel.Error, String.Format("Datagram dropped - unexpected type: {0}, received from authenticated peer: {1}.", type, PeerName));
                        return false;
                    }
            }
        }

        // ASSUMPTION: Caller has checked UnreadPacketCount > 0, otherwise calling this would be 
        //             unneccessary.
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

        internal void ReturnLeasedObjects()
        {
            // return objects leased from FalconPeer's pools
            
            foreach (var enqueudAck in enqueudAcks)
            {
                localPeer.AckPool.Return(enqueudAck);
            }

            allUnreadPackets.Clear(); // NOTE: would have alread been returned to pool when processed in FalconPeer.
        }
    }
}
