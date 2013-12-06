﻿using System.Text;


namespace FalconUDP
{
    static class Settings
    {
        internal static Encoding TextEncoding                   = Encoding.UTF8;                    // encoding to use on system messages

        internal const float ACKTimeout                         = 1.020f;                           // seconds (should be multiple of ACK_TICK_TIME)
        internal const int ACKRetryAttempts                     = 2;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        internal const int OutOfOrderTolerance                  = 8;                                // packets recveived out-of-order from last received greater than this are dropped indiscrimintly 
        internal const int LatencySampleSize                    = 2;
        internal const int DiscoverySignalsToEmit               = 3;
        internal const int MaxNumberPeersToDiscover             = 64;     
        internal const int MaxNeccessaryOrdinalSeq              = ushort.MaxValue + OutOfOrderTolerance;
        internal const float KeepAliveIfInterval                = 10.000f; // secounds
        internal const float KeepAliveIfNoKeepAliveReceived     = KeepAliveIfInterval + ACKTimeout + 1;
        internal const float AutoFlushInterval                  = ACKTimeout - 0.4f; // secounds
        internal const int PingTimeout                          = 2000; // milliseconds

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
