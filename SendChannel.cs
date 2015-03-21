using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class SendChannel
    {
        private readonly Queue<Datagram> queue;
        private readonly SendOptions channelType;
        private readonly DatagramPool datagramPool;
        private Datagram currentDatagram;
        private int currentDatagramTotalBufferOffset;
        private ushort seqCount;

        internal bool IsReliable { get; private set; }
        internal bool HasDataToSend { get { return queue.Count > 0 || currentDatagramTotalBufferOffset > currentDatagram.Offset; } }
        
        public SendChannel(SendOptions channelType, DatagramPool sendDatagramPool)
        {
            this.channelType    = channelType;
            this.queue          = new Queue<Datagram>();
            this.datagramPool   = sendDatagramPool;
            this.IsReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;

            GetNewDatagram();
        }

        private void GetNewDatagram()
        {
            currentDatagram = datagramPool.Borrow();
            currentDatagram.SendOptions = channelType;
            seqCount++;
            currentDatagram.Sequence = seqCount;
            currentDatagramTotalBufferOffset = currentDatagram.Offset;
        }

        private void EnqueueCurrentDatagram()
        {
            // Enqueue current datagram setting relevant fields.
            currentDatagram.Resize(currentDatagramTotalBufferOffset - currentDatagram.Offset);
            queue.Enqueue(currentDatagram);

            // get a new one
            GetNewDatagram();
        }

        // used when datagram already constructed, e.g. re-sending unACKnowledged datagram
        internal void EnqueueSend(Datagram datagram)
        {
            queue.Enqueue(datagram);
        }

        internal void EnqueueSend(PacketType type, Packet packet)
        {
            // NOTE: packet may be null in the case of Falcon system messages.

            if (packet != null && packet.BytesWritten > FalconPeer.MaxPayloadSize)
            {
                throw new InvalidOperationException(String.Format("Packet size: {0}, greater than max: {1}", packet.BytesWritten, FalconPeer.MaxPayloadSize));
            }

            bool isFalconHeaderWritten = currentDatagramTotalBufferOffset > currentDatagram.Offset;

            if (isFalconHeaderWritten)
            {
                if (packet != null && (packet.BytesWritten + Const.ADDITIONAL_PACKET_HEADER_SIZE) > (currentDatagram.Count - (currentDatagramTotalBufferOffset - currentDatagram.Offset))) // i.e. cannot fit
                {
                    // enqueue the current args and get a new one
                    EnqueueCurrentDatagram();
                    isFalconHeaderWritten = false;
                }
            }

            if (!isFalconHeaderWritten)
            {
                // write the falcon header
                FalconHelper.WriteFalconHeader(currentDatagram.BackingBuffer,
                    currentDatagram.Offset,
                    type,
                    channelType,
                    seqCount,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentDatagramTotalBufferOffset += Const.FALCON_PACKET_HEADER_SIZE;
            }
            else
            {
                // TODO limit max additional to 100 so receive channel can distinguish ordinal seq

                // write additional header
                FalconHelper.WriteAdditionalFalconHeader(currentDatagram.BackingBuffer,
                    currentDatagramTotalBufferOffset,
                    type,
                    channelType,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentDatagramTotalBufferOffset += Const.ADDITIONAL_PACKET_HEADER_SIZE;
            }

            if (packet != null)
            {
                //---------------------------------------------------------------------------------------------------
                packet.CopyBytes(0, currentDatagram.BackingBuffer, currentDatagramTotalBufferOffset, packet.BytesWritten);
                //---------------------------------------------------------------------------------------------------

                currentDatagramTotalBufferOffset += packet.BytesWritten;
            }
        }

        // Get everything inc. current args if anything written to it
        internal Queue<Datagram> GetQueue()
        {
            if (currentDatagramTotalBufferOffset > currentDatagram.Offset) // i.e. something written
            {
                EnqueueCurrentDatagram();
            }
            
            return queue;
        }

        // NOTE: This makes the channel unusuable.
        internal void ReturnLeasedDatagrams()
        {
            var queue = GetQueue();
            while (queue.Count > 0)
            {
                datagramPool.Return(queue.Dequeue());
            }
            datagramPool.Return(currentDatagram);
        }
    }
}
