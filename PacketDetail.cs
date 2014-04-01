using System;

namespace FalconUDP
{
    // used when holding onto sent falcon packets awaiting ACK 
    internal class PacketDetail
    {
        internal ushort Sequence;   
        internal FalconBuffer Datagram;
        internal int    Count;
        internal byte   ResentCount;
        internal float  EllapsedSecondsSincePacketSent;
        internal SendOptions ChannelType;

        public PacketDetail()
        {
        }

        
    }
}
