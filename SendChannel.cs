using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class SendChannel
    {
        internal bool IsReliable { get { return isReliable; } }
        internal int Count { get { return count; } } // number of packets ready for sending

        private readonly BufferPool datagramPool;
        private readonly GenericObjectPool<SendToken> tokenPool;
        private readonly Queue<FalconBuffer> queue;
        private readonly bool isReliable;

        private SendOptions channelType;
        private FalconBuffer currentDatagram;
        private int currentBufferBufferOffset;
        private ushort seqCount;
        private SendToken currentToken;
        private int count;
        
        public SendChannel(SendOptions channelType, 
            BufferPool datagramPool, 
            GenericObjectPool<SendToken> tokenPool)
        {
            this.channelType    = channelType;
            this.datagramPool   = datagramPool;
            this.queue          = new Queue<FalconBuffer>();
            this.currentDatagram  = datagramPool.Borrow();
            this.currentBufferBufferOffset = this.currentDatagram.Offset;
            this.isReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;
            this.tokenPool      = tokenPool;
            this.count          = 0;

            SetCurrentDatagramToken();
        }

        private void SetCurrentDatagramToken()
        {
            currentToken = tokenPool.Borrow();
            currentToken.SendOptions = this.channelType;
            currentDatagram.UserToken = currentToken; 
        }

        private void EnqueueCurrentArgs()
        {
            // queue current one setting Count to actual number of bytes written
            currentDatagram.AdjustCount(currentBufferBufferOffset - currentDatagram.Offset);
            queue.Enqueue(currentDatagram);

            // get a new one
            currentDatagram = datagramPool.Borrow();
            currentBufferBufferOffset = currentDatagram.Offset;

            // assign it a new token
            SetCurrentDatagramToken();

            seqCount++;
        }

        internal void ResetCount()
        {
            count = 0;
        }

        // used when datagram already constructed, e.g. re-sending unACKnowledged packet
        internal void EnqueueSend(FalconBuffer datagram)
        {
            queue.Enqueue(datagram);
        }

        internal void EnqueueSend(PacketType type, Packet packet)
        {
            // NOTE: packet may be null in the case of Falcon system messages.

            if (packet != null && packet.BytesWritten > Const.MAX_PAYLOAD_SIZE)
            {
                throw new InvalidOperationException(String.Format("Packet size: {0}, greater than max: {1}", packet.BytesWritten, Const.MAX_PAYLOAD_SIZE));
            }

            bool isFalconHeaderWritten = currentBufferBufferOffset > currentDatagram.Offset;

            if (isFalconHeaderWritten)
            {
                if (packet != null && (packet.BytesWritten + Const.ADDITIONAL_PACKET_HEADER_SIZE) > (currentDatagram.Count - (currentBufferBufferOffset - currentDatagram.Offset))) // i.e. cannot fit
                {
                    // enqueue the current datagram and get a new one
                    EnqueueCurrentArgs();
                    isFalconHeaderWritten = false;
                }
            }

            if (!isFalconHeaderWritten)
            {
                // write the falcon header
                FalconHelper.WriteFalconHeader(currentDatagram.Buffer,
                    currentDatagram.Offset,
                    type,
                    channelType,
                    seqCount,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentBufferBufferOffset += Const.FALCON_PACKET_HEADER_SIZE;
            }
            else
            {
                // write additional header
                FalconHelper.WriteAdditionalFalconHeader(currentDatagram.Buffer,
                    currentBufferBufferOffset,
                    type,
                    channelType,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentBufferBufferOffset += Const.ADDITIONAL_PACKET_HEADER_SIZE;
            }

            if (packet != null)
            {
                //----------------------------------------------------------------------------------------
                packet.CopyBytes(0, currentDatagram.Buffer, currentBufferBufferOffset, packet.BytesWritten);
                //----------------------------------------------------------------------------------------

                currentBufferBufferOffset += packet.BytesWritten;
            }

            count++;
        }

        // Get everything inc. current args if anything written to it
        internal Queue<FalconBuffer> GetQueue()
        {
            if (currentBufferBufferOffset > currentDatagram.Offset) // i.e. something written
            {
                EnqueueCurrentArgs();
            }
            
            return queue;
        }
    }
}
