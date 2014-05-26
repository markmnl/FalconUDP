using System.Text;

namespace FalconUDP
{
    static class PoolSizes
    {
        internal static int InitalNumPacketsToPool = 32;
        internal static int InitalNumPingsToPool = 10;
        internal static int InitalNumEmitDiscoverySignalTaskToPool = 5;
        internal static int InitalNumSendBuffersToPool = 20;
        internal static int InitalNumAcksToPoolPerPeer = 20;
    }
}
