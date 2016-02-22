﻿using System;
using System.Net;
using System.Text;

namespace FalconUDP
{
    static class Const
    {
        internal const int                  CARRIER_PROTOCOL_HEADER_SIZE        = 28;                               // used for stats, total additional size in bytes sent on wire in additiona to falcon packet size
        internal const int                  FALCON_PACKET_HEADER_SIZE           = 5;
        internal const int                  ADDITIONAL_PACKET_HEADER_SIZE       = 3;                                // seq num is not included
        internal const int                  DISCOVERY_TOKEN_SIZE                = 16;
        internal const byte                 SEND_OPTS_MASK                      = 112;                              // 0111 0000 AND'd with packet detail byte returns SendOptions
        internal const byte                 PACKET_TYPE_MASK                    = 15;                               // 0000 1111 AND'd with packet detail byte returns PacketType
        internal const byte                 ACK_PACKET_DETAIL                   = (byte)((byte)PacketType.ACK | (byte)SendOptions.None);
        internal static readonly Type       PACKET_TYPE_TYPE                    = typeof(PacketType);
        internal static readonly Type       SEND_OPTIONS_TYPE                   = typeof(SendOptions);
        internal static readonly byte[]     CLASS_C_SUBNET_MASK                 = new byte[] { 255, 255, 255, 0 };
        internal static readonly byte[]     DISCOVER_PACKET                     = new byte[] { ((byte)PacketType.DiscoverRequest | (byte)SendOptions.None), 0, 0, 0, 0 };
        internal static readonly byte[]     DISCOVER_PACKET_WITH_TOKEN_HEADER   = new byte[] { ((byte)PacketType.DiscoverRequest | (byte)SendOptions.None), 0, 0, DISCOVERY_TOKEN_SIZE, 0 };
        internal static readonly byte[]     UPNP_DISCOVER_REQUEST               = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST:upnp:rootdevice\r\nMAN:\"ssdp:discover\"\r\nMX:3\r\n\r\n");
        internal static readonly IPEndPoint UPNP_DISCOVER_ENDPOINT              = new IPEndPoint(IPAddress.Broadcast, 1900);
    }
}

 