using System.Net.Sockets;

namespace FalconUDP
{
    // used when simulating delay
    internal class DelayedDatagram
    {
        internal float EllapsedSecondsSinceDelayed;
        internal SocketAsyncEventArgs Datagram;
    }
}
