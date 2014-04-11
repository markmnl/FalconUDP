using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class SendChannel
    {
        private readonly BufferPool<Datagram> datagramPool;
        private readonly Queue<Datagram> queue;
        private readonly bool isReliable;
        private SendOptions channelType;
        private Datagram currentDatagram;
        private int currentDatagramBytesWritten;
        private ushort seqCount;
        private bool isFalconHeaderWritten; // to the current datagram

        internal bool IsReliable { get { return isReliable; } }
        internal bool HasDataToSend
        {
            get { return queue.Count > 0 || currentDatagramBytesWritten > 0; }
        }

        public SendChannel(SendOptions channelType, BufferPool<Datagram> datagramPool)
        {
            this.channelType    = channelType;
            this.datagramPool   = datagramPool;
            this.queue          = new Queue<Datagram>();
            this.isReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;
            this.currentDatagram = datagramPool.Borrow();
            currentDatagram.SendOptions = channelType;
            currentDatagram.IsResend = false;
            this.currentDatagramBytesWritten = 0;
        }

        private void EnqueueCurrentDatagram()
        {
            // queue current one setting Count to actual number of bytes written
            currentDatagram.SetCount(currentDatagramBytesWritten);
            queue.Enqueue(currentDatagram);

            // get a new one and reset count and isFalconHeaderWritten
            currentDatagram = datagramPool.Borrow();
            currentDatagram.SendOptions = channelType;
            currentDatagram.IsResend = false;
            currentDatagramBytesWritten = 0;
            isFalconHeaderWritten = false;

            seqCount++;
        }

        internal void EnqueueSend(Datagram datagram)
        {
            queue.Enqueue(datagram);
        }

        internal void EnqueueSend(PacketType type, Packet packet)
        {
            // NOTE: packet may be null in the case of Falcon system messages.

            if (packet != null && packet.BytesWritten > currentDatagram.Count)
            {
                throw new InvalidOperationException(String.Format("Packet size: {0}, greater than max: {1}", packet.BytesWritten, Const.MAX_PAYLOAD_SIZE));
            }

            if (isFalconHeaderWritten)
            {
                if (packet != null && (packet.BytesWritten + Const.ADDITIONAL_PACKET_HEADER_SIZE) > currentDatagram.Count) // i.e. cannot fit
                {
                    // Enqueue the current datagram and get a new one, NOTE: falcon header is not 
                    // written to new one now).
                    EnqueueCurrentDatagram();
                }
            }

            ushort size;
            if (packet == null)
                size = 0;
            else
                size = (ushort)packet.BytesWritten;

            if (!isFalconHeaderWritten)
            {
                // write the falcon header and set isFalconHeaderWritten
                currentDatagramBytesWritten += FalconHelper.WriteFalconHeader(currentDatagram.BackingBuffer,
                    currentDatagram.Offset,
                    type,
                    channelType,
                    seqCount,
                    size);
                isFalconHeaderWritten = true;
                currentDatagram.Seq = seqCount;
            }
            else
            {
                // write additional header
                currentDatagramBytesWritten += FalconHelper.WriteAdditionalFalconHeader(currentDatagram.BackingBuffer,
                    currentDatagramBytesWritten,
                    type,
                    channelType,
                    size);
            }

            if (packet != null)
            {
                Buffer.BlockCopy(packet.BackingBuffer, 
                    packet.Offset, 
                    currentDatagram.BackingBuffer, 
                    currentDatagram.Offset + currentDatagramBytesWritten, 
                    packet.BytesWritten);
                currentDatagramBytesWritten += packet.BytesWritten;
            }
        }

        internal bool TryDequeDatagram(out Datagram datagram)
        {
            if (queue.Count > 0)
            {
                datagram = queue.Dequeue();
                return true;
            }
            
            if (currentDatagramBytesWritten > 0) // i.e. something written
            {
                EnqueueCurrentDatagram();
                datagram = queue.Dequeue();
                return true;
            }

            datagram = null;
            return false;
        }
    }
}
