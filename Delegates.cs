﻿using System;
using System.Collections.Generic;
using System.Net;

namespace FalconUDP
{
    /// <summary>
    /// Delegate for the <see cref="FalconPeer.PeerAdded"/> event.
    /// </summary>
    /// <param name="id">
    /// Id of peer added</param>
    /// <param name="userData">
    /// Additional data sent by the peer sent in the join request. 
    /// 
    /// NOTE: Packet must NOT be held and used outside of event handler as it autmatically 
    /// returned to the pool.</param>
    public delegate void PeerAdded(int id, Packet userData);

    /// <summary>
    /// Delegate for the <see cref="FalconPeer.PeerDropped"/> event.
    /// </summary>
    /// <param name="id">
    /// Id or peer dropped</param>
    public delegate void PeerDropped(int id);

    /// <summary>
    /// Delegate for the <see cref="FalconPeer.PeerDiscovered"/> event.
    /// </summary>
    /// <param name="ipEndPoint">IPEndPoint of the remote FalconUDP peer just discovered</param>
    public delegate void PeerDiscovered(IPEndPoint ipEndPoint);

    /// <summary>
    /// Delegate for the <see cref="FalconPeer.PongReceivedFromPeer"/> event.
    /// </summary>
    /// <param name="peerId">Id of peer that send the Pong</param>
    /// <param name="roundTripTime">Ellapsed milliseconds since Ping sent.</param>
    public delegate void PongReceivedFromPeer(int peerId, TimeSpan roundTripTime);

    /// <summary>
    /// Delegate for the <see cref="FalconPeer.PongReceivedFromUnknownPeer"/> event.
    /// </summary>
    /// <param name="ipEndPoint">IPEndPoint pong received from.</param>
    /// <param name="roundTripTime">Ellapsed seconds since Ping sent.</param>
    public delegate void PongReceivedFromUnknownPeer(IPEndPoint ipEndPoint, TimeSpan roundTripTime);

#if DEBUG
    /// <summary>
    /// Delegate to invoke when Falcon logs a line instead of logging to Debug.
    /// </summary>
    /// <param name="lvl">LogLevel <see cref="LogLevel"/>.</param>
    /// <param name="line">Line to be logged.</param>
    public delegate void LogCallback(LogLevel lvl, string line);
#endif

    /// <summary>
    /// Callback to call once discovery operation initated by 
    /// <see cref="FalconPeer.DiscoverFalconPeersAsync(TimeSpan, int, Guid?, DiscoveryCallback, int)"/> has completed.
    /// </summary>
    /// <param name="discoveredPeers">Array of all IPEndPoints that replied to discovery request.</param>
    public delegate void DiscoveryCallback(IPEndPoint[] discoveredPeers);

    /// <summary>
    /// Callback passed to 
    /// <see cref="FalconPeer.DiscoverFalconPeersAsync(TimeSpan, int, Guid?, DiscoveryCallback, int)"/> 
    /// called back once the punch through operation completes.
    /// </summary>
    /// <param name="success">True if received a reply from any one of the end points supplied, 
    /// otherwise false.</param>
    /// <param name="endPoint">The first end point from which a reply was recived, only set if 
    /// successfult i.e. <paramref name="success"/> is true.</param>
    public delegate void PunchThroughCallback(bool success, IPEndPoint endPoint);

    /// <summary>
    /// Delegate to process a packet received from a remote peer.
    /// </summary>
    /// <param name="packet">Packet to read data sent from peer.</param>
    /// <remarks>Packet will be in a read-only state and must not be held onto to be used later (it
    ///  will be returned to the packet pool once the method this delegate points to completes.</remarks>
    public delegate void ProcessReceivedPacket(Packet packet);

    /// <summary>
    /// Callbask supplied to <see cref="FalconPeer.TryAddUPnPPortMapping(AddUPnPPortMappingCallback)"/> 
    /// called back once operation completes.
    /// </summary>
    /// <param name="result">TODO</param>
    public delegate void AddUPnPPortMappingCallback(AddUPnPMappingResult result);
}
