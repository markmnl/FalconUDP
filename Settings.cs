using System.Text;


namespace FalconUDP
{
    static class Settings
    {
        internal static Encoding TextEncoding                   = Encoding.UTF8;                    // encoding to use on system text messages

        internal const float ACKTimeout                         = 1.020f;                           // seconds
        internal const int ACKRetryAttempts                     = 2;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        internal const int OutOfOrderTolerance                  = 8;                                // packets recveived out-of-order from last received greater than this are dropped indiscrimintly 
        internal const int LatencySampleSize                    = 2;
        internal const int DiscoverySignalsToEmit               = 3;
        internal const int MaxNumberPeersToDiscover             = 64;     
        internal const int MaxNeccessaryOrdinalSeq              = ushort.MaxValue + OutOfOrderTolerance;
        internal const float KeepAliveInterval                  = 5.0f;                             // seconds
        internal const float KeepAliveIfNoKeepAliveReceived     = KeepAliveInterval + ACKTimeout + 1;
        internal const float AutoFlushInterval                  = ACKTimeout - 0.4f;                // seconds
        internal const int PingTimeout                          = 2000;                             // milliseconds
        internal const int ReciveBufferSize                     = 8192;
        internal const int SendBufferSize                       = ReciveBufferSize;

        internal const int InitalNumDatagramDetailPerPeerToPool = 6;
        internal const int InitalNumDatagramsPerPeerToPool      = 8;
        internal const int InitalNumPacketsToPool               = 32;  
        internal const int InitalNumEmitDiscoverySignalTaskToPool = 5;  
        internal const int InitalNumAcksToPoolPerPeer           = 5;
        internal const int InitalNumPingsToPool                 = 10;
    }
}
