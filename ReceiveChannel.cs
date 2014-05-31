using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class ReceiveChannel
    {
        private readonly SortedList<float,Packet> receivedPackets;
        private readonly List<Packet> packetsRead;
        private readonly SendOptions channelType;
        private readonly FalconPeer localPeer;
        private readonly RemotePeer remotePeer;
        private readonly bool isReliable;
        private readonly bool isInOrder;
        private float lastReceivedPacketSeq;
        private int maxReadDatagramSeq;

        /// <summary>
        /// Number of unread packets ready for reading
        /// </summary>
        internal int Count { get; private set; }

        internal ReceiveChannel(SendOptions channelType, FalconPeer localPeer, RemotePeer remotePeer)
        {
            this.channelType    = channelType;
            this.localPeer      = localPeer;
            this.remotePeer     = remotePeer;
            this.isReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;
            this.isInOrder      = (channelType & SendOptions.InOrder) == SendOptions.InOrder;
            this.receivedPackets = new SortedList<float,Packet>();
            this.packetsRead    = new List<Packet>();
        }

        // returns true if datagram is valid, otherwise it should be dropped any additional packets in it should not be processed
        internal bool TryAddReceivedPacket(ushort datagramSeq,
            PacketType type,
            byte[] buffer,
            int index,
            int count,
            bool isFirstPacketInDatagram,
            out bool applicationPacketAdded)
        {
            applicationPacketAdded = false; // until proven otherwise

            float ordinalPacketSeq;

            if (isFirstPacketInDatagram)
            {
                // validate seq in range

                ushort min = unchecked((ushort)(lastReceivedPacketSeq - localPeer.OutOfOrderTolerance));
                ushort max = unchecked((ushort)(lastReceivedPacketSeq + localPeer.OutOfOrderTolerance));

                // NOTE: Max could be less than min if exceeded MaxValue, likewise min could be 
                //       greater than max if less than 0. So have to check seq between min - max range 
                //       which is a loop, inclusive.

                if (datagramSeq > max && datagramSeq < min)
                {
                    localPeer.Log(LogLevel.Warning, String.Format("Out-of-order packet from: {0} dropped, out-of-order from last by: {1}.", remotePeer.PeerName, datagramSeq - lastReceivedPacketSeq));
                    return false;
                }

                ordinalPacketSeq = datagramSeq;
                int diff = Math.Abs(datagramSeq - (int)lastReceivedPacketSeq);
                if (diff > localPeer.OutOfOrderTolerance)
                {
                    if (datagramSeq < lastReceivedPacketSeq) // i.e. seq must have looped since we have already validated seq in range
                    {
                        ordinalPacketSeq += ushort.MaxValue;
                    }
                }

                // check not duplicate, this ASSUMES we haven't received 65534 datagrams between reads!
                if (receivedPackets.ContainsKey(ordinalPacketSeq))
                {
                    localPeer.Log(LogLevel.Warning, String.Format("Duplicate packet from: {0} dropped.", remotePeer.PeerName));
                    return false;
                }

                // if datagram required to be in order check after max read, if not drop it
                if (isInOrder)
                {
                    if (ordinalPacketSeq < maxReadDatagramSeq)
                    {
                        return false;
                    }
                }

                // if datagram requries ACK - send it!
                if (isReliable)
                {
                    remotePeer.ACK(datagramSeq, channelType);
                }
            }
            else // i.e. additional packet
            {
                // lastReceived Seq will be ordinal seq for previous packet in datagram
                ordinalPacketSeq = lastReceivedPacketSeq + 0.01f;
            }

            lastReceivedPacketSeq = ordinalPacketSeq;

            if (type == PacketType.KeepAlive)
            {
                if (!remotePeer.IsKeepAliveMaster)
                {
                    // To have received a KeepAlive from this peer who is not the KeepAlive master 
                    // is only valid when the peer never received a KeepAlive from us for 
                    // FalconPeer.KeepAliveProbeInterval for which the most common cause would be 
                    // we disappered though we must be back up again to have received it! 

                    localPeer.Log(LogLevel.Warning, String.Format("Received KeepAlive from: {0} who's not the KeepAlive master!", remotePeer.PeerName));
                }

                // nothing else to do - would have already ACKd this datagram

                return true;
            }

            if (type == PacketType.AcceptJoin)
            {
                // Probably our ACK did not get through so the remote peer is re-sending, 
                // nothing else to do - would have already ACKd this datagram.

                return true;
            }

            // Must be Application packet

            Packet packet = localPeer.PacketPool.Borrow();
            packet.WriteBytes(buffer, index, count);
            packet.ResetAndMakeReadOnly(remotePeer.Id);
            packet.DatagramSeq = datagramSeq;

            // Add packet
            receivedPackets.Add(ordinalPacketSeq, packet);

            if (isReliable)
            {
                // re-calc number of continuous seq from first
                Count = 1;
                int key = receivedPackets[receivedPackets.Keys[0]].DatagramSeq;
                for (int i = 1; i < receivedPackets.Count; i++)
                {
                    int next = receivedPackets[receivedPackets.Keys[i]].DatagramSeq;
                    if (next == key)
                    {
                        // NOTE: This must be an additional packet with the same 
                        //       datagram seq.

                        Count++;
                    }
                    else if (next == (key + 1))
                    {
                        Count++;
                        key = next;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                Count++;
            }

            applicationPacketAdded = true;

            return true;
        }

        internal List<Packet> Read()
        {
            packetsRead.Clear();

            if (Count > 0)
            {
                if (isReliable)
                {
                    while (Count > 0)
                    {
                        maxReadDatagramSeq = receivedPackets[receivedPackets.Keys[receivedPackets.Count - 1]].DatagramSeq;
                        packetsRead.Add(receivedPackets[receivedPackets.Keys[0]]);
                        receivedPackets.RemoveAt(0);
                        Count--;
                    }
                }
                else
                {
                    packetsRead.Capacity = receivedPackets.Count;
                    packetsRead.AddRange(receivedPackets.Values);
                    maxReadDatagramSeq = receivedPackets[receivedPackets.Keys[receivedPackets.Count - 1]].DatagramSeq;
                    receivedPackets.Clear();
                    Count = 0;
                }

                // If max read seq > (ushort.MaxValue + FalconPeer.OutOfOrderTolerance) no future 
                // datagram will be from the old loop (without being dropped), so reset max and 
                // ordinal seq to the same value as seq they are for.

                if (maxReadDatagramSeq > localPeer.MaxNeededOrindalSeq)
                {
                    maxReadDatagramSeq -= ushort.MaxValue;
                    lastReceivedPacketSeq -= ushort.MaxValue;
                }
            }

            return packetsRead;
        }
    }
}
