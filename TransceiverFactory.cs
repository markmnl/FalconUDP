
namespace FalconUDP
{
    public static class TransceiverFactory
    {
        public static bool UseAutonomousTranceiver = false;

        internal static IFalconTransceiver Create(FalconPeer localPeer)
        {
#if NETFX_CORE
            return new DatagramSocketTransceiver(localPeer);
#else
            if (UseAutonomousTranceiver)
            {
                return new AutonomousTransciever(localPeer);
            }
            else
            {
                return new SocketTransceiver(localPeer);
            }
#endif
        }
    }
}
