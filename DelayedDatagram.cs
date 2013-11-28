using System.Net.Sockets;

namespace FalconUDP
{
    internal class DelayedDatagram
    {
        internal int TicksTillSend;
        internal SocketAsyncEventArgs Datagram;
    }
}
