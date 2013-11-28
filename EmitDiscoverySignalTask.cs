using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FalconUDP
{
    internal class EmitDiscoverySignalTask
    {
        private List<IPEndPoint> endPointsToSendTo;
        private List<IPEndPoint> endPointsReceivedReplyFrom;
        private bool listenForReply; // it is possible to emit discovery signals without bothering about a reply, e.g. to aid another peer joining us in an attempt to traverse NAT 
        private DiscoveryCallback callback;
        private int ticksBetweenEmits;
        private int totalEmits;
        private int maxNumberPeersToDiscover;
        private FalconPeer falconPeer;
        private int tickCount;
        private int emitCount;
        private Guid? token;
        private byte[] tokenBytes;

        private static SocketAsyncEventArgsPool sendArgsPool;

        public bool IsAwaitingDiscoveryReply { get { return listenForReply; } }
        public bool TaskEnded { get; private set; }

        public EmitDiscoverySignalTask()
        {
            endPointsToSendTo = new List<IPEndPoint>();
            endPointsReceivedReplyFrom = new List<IPEndPoint>();

            if (sendArgsPool == null)
            {
                sendArgsPool = new SocketAsyncEventArgsPool(Const.DISCOVER_PACKET.Length + Const.DISCOVERY_TOKEN_SIZE, Settings.InitalNumDiscoverySendArgsToPool, GetNewSendArgs);
            }
        }

        private SocketAsyncEventArgs GetNewSendArgs()
        {
            SocketAsyncEventArgs args = new SocketAsyncEventArgs();
            args.Completed += OnSendCompleted;
            return args;
        }

        private void OnSendCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (falconPeer != null && falconPeer.IsCollectingStatistics) // HERE BE DRAGONS sometimes falonPeer is null?!?!?!
            {
                falconPeer.Statistics.AddBytesSent(args.Count);
            }
            sendArgsPool.Return(args);
        }

        internal void EmitDiscoverySignal()
        {
            foreach (IPEndPoint ep in endPointsToSendTo)
            {
                // check we haven't already discovered the peer we are about to try discover!
                lock (endPointsReceivedReplyFrom)
                {
                    if (endPointsReceivedReplyFrom.Find(dp => dp.Address.Equals(ep.Address) && dp.Port == ep.Port) != null)
                        continue;
                }

                SocketAsyncEventArgs args = sendArgsPool.Borrow();
                if (token.HasValue)
                {
                    Buffer.BlockCopy(Const.DISCOVER_PACKET_WITH_TOKEN_HEADER, 0, args.Buffer, args.Offset, Const.DISCOVER_PACKET_WITH_TOKEN_HEADER.Length);
                    Buffer.BlockCopy(tokenBytes, 0, args.Buffer, args.Offset + Const.DISCOVER_PACKET_WITH_TOKEN_HEADER.Length, tokenBytes.Length);
                }
                else
                {
                    Buffer.BlockCopy(Const.DISCOVER_PACKET, 0, args.Buffer, args.Offset, Const.DISCOVER_PACKET.Length);
                    args.SetBuffer(args.Offset, Const.DISCOVER_PACKET.Length); // buffer segmant will be reset to original size in pool once used
                }
                args.RemoteEndPoint = ep;

                falconPeer.Log(LogLevel.Debug, String.Format("Emitting discovery signal to: {0}, with token: {1}.", ep, token.HasValue ? token.Value.ToString() : "None"));

                if (!falconPeer.Socket.SendToAsync(args))
                {
                    OnSendCompleted(null, args);
                }
            }
        }
        
        internal void Tick()
        {
            if (TaskEnded)
                return;

            ++tickCount;
            if (tickCount == ticksBetweenEmits)
            {
                tickCount = 0;
                ++emitCount;
                if (emitCount == totalEmits) // an emit is sent at 0 ticks so time is now up
                {
                    lock (endPointsReceivedReplyFrom)
                    {
                        if (callback != null)
                        {
                            callback(endPointsReceivedReplyFrom.ToArray());
                        }
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
            int duration,
            int numOfSignalsToEmit,
            int maxNumOfPeersToDiscover,
            IEnumerable<IPEndPoint> endPointsToSendTo,
            Guid? token,
            DiscoveryCallback callback)
        {
            // NOTE: This class is re-used from a pool so this method needs to fully reset 
            //       the class.

            if(listenForReply)
                Debug.Assert(callback != null, "callback required if listening for a reply");
            else
                Debug.Assert(callback == null, "callback must be null if not listening for a reply");
            Debug.Assert(maxNumOfPeersToDiscover > 0, "max no. of peers to receive a reply must be greater than 0");

            this.TaskEnded                  = false;
            this.falconPeer                 = falconPeer;
            this.tickCount                  = 0;
            this.emitCount                  = 0;
            this.listenForReply             = listenForReply;
            this.callback                   = callback;
            this.ticksBetweenEmits          = (duration / numOfSignalsToEmit) / Settings.TickTime;
            this.totalEmits                 = numOfSignalsToEmit;
            this.maxNumberPeersToDiscover   = maxNumOfPeersToDiscover;
            this.token                      = token;

            if(token.HasValue)
                this.tokenBytes = token.Value.ToByteArray();
            else
                this.tokenBytes = null;
            
            this.endPointsToSendTo.Clear();
            this.endPointsToSendTo.AddRange(endPointsToSendTo);
            this.endPointsReceivedReplyFrom.Clear();
        }

        internal void AddDiscoveryReply(IPEndPoint endPointReceivedFrom)
        {
            lock (endPointsReceivedReplyFrom)
            {
                // check we haven't already discovered this peer
                if (endPointsReceivedReplyFrom.Find(ep => ep.Address.Equals(endPointReceivedFrom.Address) && ep.Port == endPointReceivedFrom.Port) != null)
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
        }
        
        internal bool IsForDiscoveryReply(IPEndPoint endPointDiscoveryReplyReceivedFrom)
        {
            // ASSUMPTION: There can only be one EmitDiscoverySignalTask at any one time that 
            //             matches (inc. broadcast addresses) any one discovery reply.

            Debug.Assert(endPointDiscoveryReplyReceivedFrom.AddressFamily == AddressFamily.InterNetwork);

            foreach (IPEndPoint endPointToSendTo in endPointsToSendTo)
            {
                if (endPointToSendTo.Port == endPointDiscoveryReplyReceivedFrom.Port)
                {
                    if (endPointToSendTo.Address.Equals(endPointDiscoveryReplyReceivedFrom.Address))
                    {
                        return true;
                    }

                    byte[] bytesFrom = endPointDiscoveryReplyReceivedFrom.Address.GetAddressBytes();
                    byte[] bytesTo = endPointToSendTo.Address.GetAddressBytes();

                    bool matches = ((bytesTo[0] == 255 || bytesFrom[0] == bytesTo[0])
                        && (bytesTo[1] == 255 || bytesFrom[1] == bytesTo[1])
                        && (bytesTo[2] == 255 || bytesFrom[2] == bytesTo[2])
                        && (bytesTo[3] == 255 || bytesFrom[3] == bytesTo[3]));

                    if(matches)
                        return true;
                }
            }

            return false;
        }
    }
}
