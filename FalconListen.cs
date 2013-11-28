using System;
using System.Net;

#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
#else
using System.Net.Sockets;
#endif


namespace FalconUDP
{
    public partial class FalconPeer
    {

#if NETFX_CORE

        private async void MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try // NOTE: accessing the properties of args could throw exceptions
            {
                if (args.RemoteAddress.Type != HostNameType.Ipv4)
                {
                    // TODO support other types once the rest of Falcon does
                    Log(LogLevel.Warning, String.Format("Dropped Message - Remote peer: {0} unsupported type: {1}.", args.RemoteAddress.RawName, args.RemoteAddress.Type));
                    return;
                }

                if (args.RemoteAddress.RawName.StartsWith("127.") && args.RemotePort == localPortAsString)
                {
                    Log(LogLevel.Warning, "Dropped Message received from self.");
                    return;
                }

                IPv4EndPoint remoteEndPoint = new IPv4EndPoint(args.RemoteAddress.RawName, args.RemotePort);

                DataReader dr = args.GetDataReader();
                int size = (int)dr.UnconsumedBufferLength;

                if (size == 0)
                {
                    // peer has closed 
                    TryRemovePeer(remoteEndPoint);
                    return;
                }
                else if (size < Const.NORMAL_HEADER_SIZE || size > Const.MAX_DATAGRAM_SIZE)
                {
                    Log(LogLevel.Error, String.Format("Message dropped from peer: {0}, bad size: {0}", remoteEndPoint, size));
                    return;
                }

                byte seq = dr.ReadByte();
                byte packetInfo = dr.ReadByte();

                // parse packet info byte
                HeaderPayloadSizeType hpst = (HeaderPayloadSizeType)(packetInfo & Const.PAYLOAD_SIZE_TYPE_MASK);
                SendOptions opts = (SendOptions)(packetInfo & Const.SEND_OPTS_MASK);
                PacketType type = (PacketType)(packetInfo & Const.PACKET_TYPE_MASK);

                // check the header makes sense
                if (!Enum.IsDefined(Const.HEADER_PAYLOAD_SIZE_TYPE_TYPE, hpst)
                    || !Enum.IsDefined(Const.SEND_OPTIONS_TYPE, opts)
                    || !Enum.IsDefined(Const.PACKET_TYPE_TYPE, type))
                {
                    Log(LogLevel.Warning, String.Format("Message dropped from peer: {0}, bad header.", remoteEndPoint));
                    return;
                }

                // parse payload size
                int payloadSize;
                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    payloadSize = dr.ReadByte();
                }
                else
                {
                    if (size < Const.LARGE_HEADER_SIZE)
                    {
                        Log(LogLevel.Error, String.Format("Message with large header dropped from peer: {0}, size: {1}.", remoteEndPoint, size));
                        return;
                    }

                    payloadSizeBytes[0] = dr.ReadByte();
                    payloadSizeBytes[1] = dr.ReadByte();
                    payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);
                }

                // validate payload size
                if (payloadSize != dr.UnconsumedBufferLength)
                {
                    Log(LogLevel.Error, String.Format("Message dropped from peer: {0}, payload size: {1}, not as specefied: {2}", remoteEndPoint, dr.UnconsumedBufferLength, payloadSize));
                    return;
                }

                // copy the payload
                byte[] payload = null;
                if (payloadSize > 0)
                {
                    payload = new byte[payloadSize];
                    dr.ReadBytes(payload);
                }

                RemotePeer rp;
                if (!peersByIp.TryGetValue(remoteEndPoint, out rp))
                {
                    // Could be:
                    // 1) Remote peer has not been added yet and is requesting to be added,
                    // 2) We are asking to be added and peer is accepting or
                    // 3) DiscoverRequest or DiscoverReply

                    switch (type)
                    {
                        case PacketType.AddPeer:
                            {
                                if (!acceptJoinRequests)
                                {
                                    Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, not accepting join requests.", remoteEndPoint));
                                    return;
                                }

                                string pass = null;
                                if (payloadSize > 0)
                                    pass = Settings.TextEncoding.GetString(payload, 0, payloadSize);

                                if (pass != joinPass) // TODO something else?
                                {
                                    // TODO send reject and reason
                                    Log(LogLevel.Info, String.Format("Join request dropped from peer: {0}, bad pass.", remoteEndPoint));
                                }
                                else if (peersByIp.ContainsKey(remoteEndPoint))
                                {
                                    // TODO send reject and reason
                                    Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, peer is already added!", remoteEndPoint));
                                }
                                else
                                {
                                    rp = await TryAddPeerAsync(remoteEndPoint);
                                    if (rp != null)
                                        rp.BeginSend(SendOptions.Reliable, PacketType.AcceptJoin, null, null);
                                }
                            }
                            break;
                        case PacketType.AcceptJoin:
                            {
                                AwaitingAcceptDetail detail;
                                if (!TryGetAndRemoveWaitingAcceptDetail(remoteEndPoint, out detail))
                                {
                                    // Possible reasons we do not have detail are: 
                                    //  1) Accept is too late,
                                    //  2) Accept duplicated and we have already removed it, or
                                    //  3) Accept was unsolicited.

                                    Log(LogLevel.Warning, String.Format("Accept dropped from peer: {0}, join request not found.", remoteEndPoint));
                                }
                                else
                                {
                                    // create the new peer, add the datagram to send ACK, call the callback
                                    rp = await TryAddPeerAsync(remoteEndPoint);
                                    if (rp != null)
                                    {
                                        rp.AddReceivedPacket(seq, opts, type, payload);
                                        TryResult tr = new TryResult(true, null, null, rp.Id);
                                        detail.Callback(tr);
                                    }
                                }
                            }
                            break;
                        case PacketType.DiscoverRequest:
                            {
                                SendToUnknownPeer(remoteEndPoint, PacketType.DiscoverReply, null);
                            }
                            break;
                        case PacketType.DiscoverReply:
                            {
                                if (isAwaitingDiscoverReply)
                                {
                                    discoveredPeers.Add(remoteEndPoint);
                                }
                            }
                            break;
                        default:
                            {
                                Log(LogLevel.Warning, String.Format("Message dropped from peer: {0}, peer unknown.", remoteEndPoint));
                            }
                            break;
                    }
                }
                else
                {
                    // Let the RemotePeer handle it
                    rp.AddReceivedPacket(seq, opts, type, payload);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, String.Format("Exception in MessageReceived handler: {0}.", ex.Message));
            }
        }
#else

        private void Listen()
        {
            while (true)
            {
                if (stop)
                    return;

                int sizeReceived = 0;

                try
                {
                    //-------------------------------------------------------------------------
                    sizeReceived = Socket.ReceiveFrom(receiveBuffer, ref lastRemoteEndPoint);
                    //-------------------------------------------------------------------------

                    IPEndPoint ip = (IPEndPoint)lastRemoteEndPoint;

                    // Do not listen to packets sent by us to us. This only drops packets from the 
                    // same instance of Falcon (i.e. on the same port) so we can still send/recv 
                    // packets from another instance of Falcon (i.e. on different port) on the 
                    // same host.

                    if (((IPAddress.IsLoopback(ip.Address) || (LocalAddress.Find(lip => lip.Equals(ip)) != null)) && ip.Port == localPort))
                    {
                        Log(LogLevel.Warning, "Dropped datagram received from self.");
                        continue;
                    }
                    
                    if (sizeReceived == 0)
                    {
                        // peer closed connection
                        TryRemovePeer(ip);
                        continue;
                    }
                    else if (sizeReceived < Const.FALCON_HEADER_SIZE || sizeReceived > Const.MAX_DATAGRAM_SIZE)
                    {
                        Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, bad size: {0}", lastRemoteEndPoint, sizeReceived));
                        continue;
                    }

                    byte seq            = receiveBuffer[0];
                    byte packetDetail   = receiveBuffer[1];

                    // parse packet detail byte
                    PacketInfo pi       = (PacketInfo)(packetDetail & Const.PACKET_INFO_MASK);
                    SendOptions opts    = (SendOptions)(packetDetail & Const.SEND_OPTS_MASK);
                    PacketType type     = (PacketType)(packetDetail & Const.PACKET_TYPE_MASK);

                    // check the header makes sense
                    if (!Enum.IsDefined(Const.PACKET_INFO_TYPE, pi)
                        || !Enum.IsDefined(Const.SEND_OPTIONS_TYPE, opts)
                        || !Enum.IsDefined(Const.PACKET_TYPE_TYPE, type))
                    {
                        Log(LogLevel.Warning, String.Format("Datagram dropped from peer: {0}, bad header.", lastRemoteEndPoint));
                        continue;
                    }

                    // read packet
                    int packetSize = BitConverter.ToUInt16(receiveBuffer, 2);
                    int payloadSize = BitConverter.ToUInt16(receiveBuffer, 4);
                    recvBuffer.WriteBytes(receiveBuffer, Const.FALCON_HEADER_SIZE, payloadSize);

                    if (hpst == HeaderPayloadSizeType.Byte)
                    {
                        payloadSize = receiveBuffer[2];

                        // validate payload size
                        if (payloadSize != (sizeReceived - Const.NORMAL_HEADER_SIZE))
                        {
                            Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, payload size: {1}, not as specefied: {2}", lastRemoteEndPoint, (sizeReceived - Const.NORMAL_HEADER_SIZE), payloadSize));
                            continue;
                        }

                        if (payloadSize > 0)
                        {
                            payload = new byte[payloadSize];
                            System.Buffer.BlockCopy(receiveBuffer, Const.NORMAL_HEADER_SIZE, payload, 0, payloadSize);
                        }
                    }
                    else
                    {
                        if (sizeReceived < Const.FALCON_HEADER_SIZE)
                        {
                            Log(LogLevel.Error, String.Format("Datagram with large header dropped from peer: {0}, size: {1}.", lastRemoteEndPoint, sizeReceived));
                            continue;
                        }

                        payloadSizeBytes[0] = receiveBuffer[2];
                        payloadSizeBytes[1] = receiveBuffer[3];
                        payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);

                        // validate payload size
                        if (payloadSize != (sizeReceived - Const.FALCON_HEADER_SIZE))
                        {
                            Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, payload size: {1}, not as specefied: {2}", lastRemoteEndPoint, (sizeReceived - Const.FALCON_HEADER_SIZE), payloadSize));
                            continue;
                        }

                        payload = new byte[payloadSize];
                        System.Buffer.BlockCopy(receiveBuffer, Const.NORMAL_HEADER_SIZE, payload, 0, payloadSize);
                    }

                    RemotePeer rp;
                    if (!peersByIp.TryGetValue(ip, out rp))
                    {
                        switch (type)
                        {
                            case PacketType.JoinRequest:
                                {
                                    if (!acceptJoinRequests)
                                    {
                                        Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, not accepting join requests.", lastRemoteEndPoint));
                                        continue;
                                    } 

                                    string pass = null;
                                    if (payloadSize > 0)
                                        pass = Settings.TextEncoding.GetString(receiveBuffer, Const.NORMAL_HEADER_SIZE, payloadSize);

                                    if (pass != joinPass) // TODO something else?
                                    {
                                        // TODO send reject and reason
                                        Log(LogLevel.Warning, String.Format("Join request from: {0} dropped, bad pass.", ip));
                                    }
                                    else if (peersByIp.ContainsKey(ip))
                                    {
                                        // TODO send reject and reason
                                        Log(LogLevel.Warning, String.Format("Cannot add peer again: {0}, peer is already added!", ip));
                                    }
                                    else
                                    {
                                        Log(LogLevel.Info, String.Format("Accepted Join Request from: {0}", ip));

                                        rp = AddPeer(ip);
                                        rp.Send(SendOptions.Reliable, PacketType.AcceptJoin, null, null);
                                    }
                                }
                                break;
                            case PacketType.AcceptJoin:
                                {
                                    AwaitingAcceptDetail detail;
                                    if (!TryGetAndRemoveWaitingAcceptDetail(ip, out detail))
                                    {
                                        // Possible reasons we do not have detail are: 
                                        //  1) Accept is too late,
                                        //  2) Accept duplicated and we have already removed it, or
                                        //  3) Accept was unsolicited.

                                        Log(LogLevel.Warning, String.Format("Dropped Accept Packet from unknown peer: {0}.", ip));
                                    }
                                    else
                                    {
                                        Log(LogLevel.Info, String.Format("Successfully joined: {0}", ip));

                                        // create the new peer, add the datagram to send ACK, call the callback
                                        rp = AddPeer(ip);
                                        rp.AddReceivedDatagram(seq, opts, type, payload);
                                        TryResult tr = new TryResult(true, null, null, rp.Id);
                                        detail.Callback(tr);
                                    }
                                }
                                break;
                            case PacketType.DiscoverRequest:
                                {
                                    string msg = String.Format("Received Discovery Request from: {0}", ip);
                                    if (replyToDiscoveryRequests)
                                    {
                                        msg += ", sending discovery reply...";
                                        SendToUnknownPeer(ip, PacketType.DiscoverReply, null);
                                    }
                                    else
                                    {
                                        msg += ", dropped - set to not reply to discovery requests.";
                                    }
                                    Log(LogLevel.Info, msg);
                                }
                                break;
                            case PacketType.DiscoverReply:
                                {
                                    lock (discoveredPeersLockObject) // acquire lock to access discoveredPeers which is also accessed in Timer Tick and by client-application
                                    {
                                        if (isAwaitingDiscoverReply)
                                        {
                                            Log(LogLevel.Info, String.Format("Received Discovery Reply from: {0}.", ip));

                                            if (discoveredPeers.Find(ep => ep.Address.Equals(ip.Address)) == null)
                                            {
                                                discoveredPeers.Add(ip);
                                            }
                                        }
                                        else
                                        {
                                            Log(LogLevel.Info, "Dropped DiscoveryReply - not waiting for a reply.");
                                        }
                                    }
                                }
                                break;
                            default:
                                {
                                    Log(LogLevel.Warning, String.Format("Datagram dropped - unknown peer: {0}.", ip));
                                }
                                break;
                        }
                    }
                    else
                    {
                        if (type == PacketType.Bye)
                        {
                            TryRemovePeer(rp.EndPoint);
                            Log(LogLevel.Info, String.Format("Bye from: {0}, peer dropped.", ip));
                        }
                        else
                        {
                            rp.AddReceivedDatagram(seq, opts, type, payload);
                        }
                    }
                }
                catch (SocketException se)
                {
                    Log(LogLevel.Error, String.Format("SocketException: {0}.", se.Message));
                }
            }
        }

#endif
    }  
}
