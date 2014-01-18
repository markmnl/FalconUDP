using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace FalconUDP
{
    internal class SendChannel
    {
        internal bool IsReliable { get { return isReliable; } }
        internal int Count { get { return count; } } // number of packets ready for sending

        private Queue<SocketAsyncEventArgs> queue;
        private SendOptions channelType;
        private SocketAsyncEventArgs currentArgs;
        private int currentArgsTotalBufferOffset;
        private SocketAsyncEventArgsPool argsPool;
        private ushort seqCount;
        private GenericObjectPool<SendToken> tokenPool;
        private SendToken currentToken;
        private FalconPeer localPeer;
        private int count;
        private bool isReliable;
        
        public SendChannel(SendOptions channelType, SocketAsyncEventArgsPool argsPool, GenericObjectPool<SendToken> tokenPool, FalconPeer localPeer)
        {
            this.channelType    = channelType;
            this.argsPool       = argsPool;
            this.queue          = new Queue<SocketAsyncEventArgs>();
            this.currentArgs    = argsPool.Borrow();
            this.currentArgsTotalBufferOffset = this.currentArgs.Offset;
            this.isReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;
            this.tokenPool      = tokenPool;
            this.localPeer      = localPeer;
            this.count          = 0;

            SetCurrentArgsToken();
        }

        private void SetCurrentArgsToken()
        {
            currentToken = tokenPool.Borrow();
            currentToken.SendOptions = this.channelType;
            currentArgs.UserToken = currentToken; 
        }

        private void EnqueueCurrentArgs()
        {
            // queue current one setting Count to actual number of bytes written
            currentArgs.SetBuffer(currentArgs.Offset, currentArgsTotalBufferOffset - currentArgs.Offset);
            queue.Enqueue(currentArgs);

            // get a new one
            currentArgs = argsPool.Borrow();
            currentArgsTotalBufferOffset = currentArgs.Offset;

            // assign it a new token
            SetCurrentArgsToken();

            seqCount++;
        }

        internal void ResetCount()
        {
            count = 0;
        }

        // used when args already constructed, e.g. re-sending unACKnowledged packet
        internal void EnqueueSend(SocketAsyncEventArgs args)
        {
            queue.Enqueue(args);
        }

        internal void EnqueueSend(PacketType type, Packet packet)
        {
            // NOTE: packet may be null in the case of Falcon system messages.

            if (packet != null && packet.BytesWritten > Const.MAX_PAYLOAD_SIZE)
            {
                throw new InvalidOperationException(String.Format("Packet size: {0}, greater than max: {1}", packet.BytesWritten, Const.MAX_PAYLOAD_SIZE));
            }

            bool isFalconHeaderWritten = currentArgsTotalBufferOffset > currentArgs.Offset;

            if (isFalconHeaderWritten)
            {
                if (packet != null && (packet.BytesWritten + Const.ADDITIONAL_PACKET_HEADER_SIZE) > (currentArgs.Count - (currentArgsTotalBufferOffset - currentArgs.Offset))) // i.e. cannot fit
                {
                    // enqueue the current args and get a new one
                    EnqueueCurrentArgs();
                    isFalconHeaderWritten = false;
                }
            }

            if (!isFalconHeaderWritten)
            {
                // write the falcon header
                FalconHelper.WriteFalconHeader(currentArgs.Buffer,
                    currentArgs.Offset,
                    type,
                    channelType,
                    seqCount,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentArgsTotalBufferOffset += Const.FALCON_PACKET_HEADER_SIZE;
            }
            else
            {
                // write additional header
                FalconHelper.WriteAdditionalFalconHeader(currentArgs.Buffer,
                    currentArgsTotalBufferOffset,
                    type,
                    channelType,
                    packet == null ? (ushort)0 : (ushort)packet.BytesWritten);
                currentArgsTotalBufferOffset += Const.ADDITIONAL_PACKET_HEADER_SIZE;
            }

            if (packet != null)
            {
                //----------------------------------------------------------------------------------------
                packet.CopyBytes(0, currentArgs.Buffer, currentArgsTotalBufferOffset, packet.BytesWritten);
                //----------------------------------------------------------------------------------------

                currentArgsTotalBufferOffset += packet.BytesWritten;
            }

            count++;
        }

        // Get everything inc. current args if anything written to it
        // ASSUMPTION lock on this.ChannelLock held
        internal Queue<SocketAsyncEventArgs> GetQueue()
        {
            if (currentArgsTotalBufferOffset > currentArgs.Offset) // i.e. something written
            {
                EnqueueCurrentArgs();
            }
            
            return queue;
        }
    }
}
