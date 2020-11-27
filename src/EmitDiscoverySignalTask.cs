﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using FalconUDP;
#if !NETFX_CORE
using System.Net.Sockets;
#endif

namespace FalconUDP
{
    internal class EmitDiscoverySignalTask
    {
        private readonly List<IPEndPoint> endPointsToSendTo;
        private readonly List<IPEndPoint> endPointsReceivedReplyFrom;
        private readonly byte[] signal;
        private bool listenForReply; // it is possible to emit discovery signals without bothering about a reply, e.g. to aid another peer joining us in an attempt to traverse NAT 
        private DiscoveryCallback callback;
        private float secondsBetweenEmits;
        private int totalEmits;
        private int maxNumberPeersToDiscover;
        private FalconPeer falconPeer;
        private int emitCount;
        private Guid? token;
        private float ellapsedSecondsSinceLastEmit;
        
        public bool IsAwaitingDiscoveryReply { get { return listenForReply; } }
        public bool TaskEnded { get; private set; }

        public EmitDiscoverySignalTask()
        {
            endPointsToSendTo = new List<IPEndPoint>();
            endPointsReceivedReplyFrom = new List<IPEndPoint>();
            signal = new byte[Const.DISCOVER_PACKET_WITH_TOKEN_HEADER.Length + Const.DISCOVERY_TOKEN_SIZE];
        }

        internal void EmitDiscoverySignal()
        {
            foreach (IPEndPoint ep in endPointsToSendTo)
            {
                // check we haven't already discovered the peer we are about to try discover!
                if (endPointsReceivedReplyFrom.Exists(dp => dp.FastEquals(ep)))
                    continue;

                falconPeer.Log(LogLevel.Debug, String.Format("Emitting discovery signal to: {0}, with token: {1}.", ep, token.HasValue ? token.Value.ToString() : "None"));

                int count = token.HasValue ? signal.Length : Const.DISCOVER_PACKET.Length;

                //-------------------------------------------------------
                falconPeer.Transceiver.Send(signal, 0, count, ep, false);
                //-------------------------------------------------------
            }
        }

        internal void Update(float dt)
        {
            ellapsedSecondsSinceLastEmit += dt;

            if (ellapsedSecondsSinceLastEmit >= secondsBetweenEmits)
            {
                ++emitCount;
                ellapsedSecondsSinceLastEmit = 0.0f;

                if (emitCount == totalEmits) // an emit is sent when started
                {
                    if (callback != null)
                    {
                        callback(endPointsReceivedReplyFrom.ToArray());
                    }
                    this.TaskEnded = true;
                }
                else
                {
                    EmitDiscoverySignal();
                }
            }
        }

        internal void Init(FalconPeer falconPeer,
            bool listenForReply,
            float durationSeconds,
            int numOfSignalsToEmit,
            int maxNumOfPeersToDiscover,
            IEnumerable<IPEndPoint> endPointsToSendTo,
            Guid? token,
            DiscoveryCallback callback)
        {
            // NOTE: This class is re-used from a pool so this method needs to fully reset 
            //       the class.

            if (listenForReply)
                Debug.Assert(callback != null, "callback required if listening for a reply");
            else
                Debug.Assert(callback == null, "callback must be null if not listening for a reply");
            Debug.Assert(maxNumOfPeersToDiscover > 0, "max no. of peers to receive a reply must be greater than 0");

            this.TaskEnded                      = false;
            this.falconPeer                     = falconPeer;
            this.emitCount                      = 0;
            this.listenForReply                 = listenForReply;
            this.callback                       = callback;
            this.secondsBetweenEmits            = (durationSeconds / numOfSignalsToEmit);
            this.totalEmits                     = numOfSignalsToEmit;
            this.maxNumberPeersToDiscover       = maxNumOfPeersToDiscover;
            this.token                          = token;
            this.ellapsedSecondsSinceLastEmit   = 0.0f;

            if (token.HasValue)
            {
                Buffer.BlockCopy(Const.DISCOVER_PACKET_WITH_TOKEN_HEADER, 0, signal, 0, Const.DISCOVER_PACKET_WITH_TOKEN_HEADER.Length);
                Buffer.BlockCopy(token.Value.ToByteArray(), 0, signal, Const.DISCOVER_PACKET.Length, Const.DISCOVERY_TOKEN_SIZE);
            }
            else
            {
                Buffer.BlockCopy(Const.DISCOVER_PACKET, 0, signal, 0, Const.DISCOVER_PACKET.Length);
            }

            this.endPointsToSendTo.Clear();
            this.endPointsToSendTo.AddRange(endPointsToSendTo);
            this.endPointsReceivedReplyFrom.Clear();
        }

        internal void AddDiscoveryReply(IPEndPoint endPointReceivedFrom)
        {
            // check we haven't already discovered this peer
            if (endPointsReceivedReplyFrom.Exists(ep => ep.FastEquals(endPointReceivedFrom)))
                return;

            // raise PeerDiscovered event
            falconPeer.RaisePeerDiscovered(endPointReceivedFrom);

            // add to list of end points received reply from
            endPointsReceivedReplyFrom.Add(endPointReceivedFrom);
            if (endPointsReceivedReplyFrom.Count == maxNumberPeersToDiscover)
            {
                callback(endPointsReceivedReplyFrom.ToArray());
                callback = null; // prevent possible subsequent Tick() calling the callback again
                TaskEnded = true;
            }
        }

        internal bool IsForDiscoveryReply(IPEndPoint endPointDiscoveryReplyReceivedFrom)
        {
            // ASSUMPTION: There can only be one EmitDiscoverySignalTask at any one time that 
            //             matches (inc. broadcast addresses) any one discovery reply.

#if !NETFX_CORE
            Debug.Assert(endPointDiscoveryReplyReceivedFrom.AddressFamily == AddressFamily.InterNetwork);
#endif

            if (TaskEnded)
                return false;

            foreach (IPEndPoint endPointToSendTo in endPointsToSendTo)
            {
                if (endPointToSendTo.Port == endPointDiscoveryReplyReceivedFrom.Port)
                {
                    if (endPointToSendTo.Address.Equals(endPointDiscoveryReplyReceivedFrom.Address))
                    {
                        return true;
                    }

#if NETFX_CORE
                    bool matches = endPointToSendTo.Address.Type == endPointDiscoveryReplyReceivedFrom.Address.Type;
                    // TODO
#else
                    byte[] bytesFrom = endPointDiscoveryReplyReceivedFrom.Address.GetAddressBytes();
                    byte[] bytesTo = endPointToSendTo.Address.GetAddressBytes();

                    bool matches = ((bytesTo[0] == 255 || bytesFrom[0] == bytesTo[0])
                        && (bytesTo[1] == 255 || bytesFrom[1] == bytesTo[1])
                        && (bytesTo[2] == 255 || bytesFrom[2] == bytesTo[2])
                        && (bytesTo[3] == 255 || bytesFrom[3] == bytesTo[3]));
#endif

                    if (matches)
                        return true;
                }
            }

            return false;
        }
    }
}
