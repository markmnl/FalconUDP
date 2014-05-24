using System.Text;

namespace FalconUDP
{
    static class PoolSizes
    {
        internal static int InitalNumPacketsToPool = 32;
        internal static int InitalNumPingsToPool = 32;
        internal static int InitalNumEmitDiscoverySignalTaskToPool = 5;
        internal static int InitalNumSendArgsToPoolPerPeer = 10;
        internal static int InitalNumPacketDetailPerPeerToPool = 8;
        internal static int InitalNumAcksToPoolPerPeer = 8;
    }
}
