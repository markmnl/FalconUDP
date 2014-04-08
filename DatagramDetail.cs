using System;

namespace FalconUDP
{
    // used when holding onto sent datagrams awaiting ACK 
    internal class DatagramDetail
    {
        internal Datagram   Datagram;
        internal byte       ResentCount;
        internal float      EllapsedSecondsSincePacketSent;

        internal void Init(Datagram datagram)
        {
            Datagram = datagram;
            ResentCount = 0;
            EllapsedSecondsSincePacketSent = 0.0f;
        }
    }
}
