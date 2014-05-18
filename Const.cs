using System;

namespace FalconUDP
{
    static class Const
    {
        internal const int      CARRIER_PROTOCOL_HEADER_SIZE        = 28;                               // used for stats, total additional size in bytes sent on wire in additiona to falcon packet size
        internal const int      FALCON_PACKET_HEADER_SIZE           = 5;
        internal const int      MAX_DATAGRAM_SIZE                   = 4096 - CARRIER_PROTOCOL_HEADER_SIZE;
        internal const int      ADDITIONAL_PACKET_HEADER_SIZE       = 3;                                // seq num is not included
        internal const int      MAX_PAYLOAD_SIZE                    = MAX_DATAGRAM_SIZE - FALCON_PACKET_HEADER_SIZE;
        internal const int      DISCOVERY_TOKEN_SIZE                = 16;
        internal const byte     SEND_OPTS_MASK                      = 112;                              // 0111 0000 AND'd with packet detail byte returns SendOptions
        internal const byte     PACKET_TYPE_MASK                    = 15;                               // 0000 1111 AND'd with packet detail byte returns PacketType
        internal const byte     ACK_PACKET_DETAIL                   = (byte)((byte)PacketType.ACK | (byte)SendOptions.None);
        internal const byte     ANTI_ACK_PACKET_DETAIL              = (byte)((byte)PacketType.AntiACK | (byte)SendOptions.None);
        internal static Type    PACKET_TYPE_TYPE                    = typeof(PacketType);
        internal static Type    SEND_OPTIONS_TYPE                   = typeof(SendOptions);
        internal static byte[]  CLASS_C_SUBNET_MASK                 = new byte[] { 255, 255, 255, 0 };
        internal static byte[]  DISCOVER_PACKET                     = new byte[] { (byte)((byte)PacketType.DiscoverRequest | (byte)SendOptions.None), 0, 0, 0, 0};
        internal static byte[]  DISCOVER_PACKET_WITH_TOKEN_HEADER   = new byte[] { (byte)((byte)PacketType.DiscoverRequest | (byte)SendOptions.None), 0, 0, DISCOVERY_TOKEN_SIZE, 0 };

    }
}

 