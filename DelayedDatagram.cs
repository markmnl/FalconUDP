using System.Net.Sockets;

namespace FalconUDP
{
    // used when simulating delay
    internal class DelayedDatagram
    {
        internal long EllapsedMillisecondsWhenDelayed;
        internal SocketAsyncEventArgs Datagram;
    }
}
