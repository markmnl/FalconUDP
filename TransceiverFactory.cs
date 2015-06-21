
namespace FalconUDP
{
    internal static class TransceiverFactory
    {

        internal static IFalconTransceiver Create(FalconPeer localPeer)
        {
#if NETFX_CORE
            return new DatagramSocketTransceiver(localPeer);
#elif PS4
            return new AutonomousTransciever(localPeer);
#else
            return new SocketTransceiver(localPeer);
#endif
        }
    }
}
