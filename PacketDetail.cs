using System;

namespace FalconUDP
{
    // used when holding onto sent falcon packets awaiting ACK 
    internal class PacketDetail
    {
        internal ushort Sequence;   
        internal byte[] Bytes;
        internal int    Count;
        internal byte   ResentCount;
        internal float  EllapsedSecondsSincePacketSent;
        internal SendOptions ChannelType;

        public PacketDetail()
        {
            Bytes = new byte[Const.MAX_DATAGRAM_SIZE];
        }

        internal void CopyBytes(byte[] srcBuffer, int srcIndex, int count)
        {
            Buffer.BlockCopy(srcBuffer, srcIndex, Bytes, 0, count);
            Count = count;
        }
    }
}
