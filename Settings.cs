using System.Text;

namespace FalconUDP
{
    static class Settings
    {
        internal const int InitalNumRecvArgsToPool              = 32;
        internal const int InitalNumSendArgsToPoolPerPeer       = InitalNumRecvArgsToPool;
        internal const int InitalNumPacketDetailPerPeerToPool   = 6;
        internal const int InitalNumPacketsToPool               = 320;  
        internal const int InitalNumEmitDiscoverySignalTaskToPool = 5;  
        internal const int InitalNumDiscoverySendArgsToPool     = InitalNumEmitDiscoverySignalTaskToPool * 2;
        internal const int InitalNumAcksToPoolPerPeer           = InitalNumPacketDetailPerPeerToPool;
        internal const int InitalNumPingsToPool                 = 10;
    }
}
