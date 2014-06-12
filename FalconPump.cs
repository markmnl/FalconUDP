using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace FalconUDP
{
    public class FalconPump
    {
        private readonly FalconPeer falconPeer;
        private readonly Queue<Packet> receivedPackets;
        private readonly object tickLockObject;
        private readonly Thread thread;
        private readonly TimeSpan tickInterval;
        private readonly Queue<Tuple<int, Packet>> peersAdded;
        private readonly Queue<int> peersRemoved;
        private readonly Queue<IPEndPoint> peersDiscovered;
        private readonly Queue<Tuple<IPEndPoint, string>> joinRequests; 
        private bool stopped;

        public event PeerAdded PeerAdded;
        public event PeerDropped PeerDropped;
        public event PeerDiscovered PeerDiscovered;

        public FalconPump (
            int             port,
            FalconPoolSizes poolSizes,
            TimeSpan        tickInterval,
            LogCallback     logCallback     = null,
            LogLevel        logLevel        = LogLevel.Warning )
        {
            this.falconPeer                 = new FalconPeer(port, ProcessReceivedPacket, poolSizes, logCallback, logLevel);
            this.falconPeer.PeerAdded       += OnPeerAdded;
            this.falconPeer.PeerDropped     += OnPeerPeerDropped;
            this.falconPeer.PeerDiscovered  += OnPeerDiscovered;
            this.receivedPackets            = new Queue<Packet>();
            this.peersAdded                 = new Queue<Tuple<int, Packet>>();
            this.peersRemoved               = new Queue<int>();
            this.peersDiscovered            = new Queue<IPEndPoint>();
            this.joinRequests               = new Queue<Tuple<IPEndPoint, string>>();
            this.tickInterval               = tickInterval;
            this.tickLockObject             = new object();
            this.thread                     = new Thread(Run);
            this.thread.IsBackground        = true;
            this.stopped                    = true;
        }

        private void OnPeerDiscovered(IPEndPoint ipEndPoint)
        {
            peersDiscovered.Enqueue(ipEndPoint);
        }

        private void OnPeerPeerDropped(int id)
        {
            peersRemoved.Enqueue(id);
        }

        private void OnPeerAdded(int id, Packet userData)
        {
            Packet clone = null;
            if(userData != null)
            {
                clone = falconPeer.BorrowPacketFromPool();
                Packet.Clone(userData, clone, true);
            }
            peersAdded.Enqueue(Tuple.Create(id, clone));
        }

        private void ProcessReceivedPacket(Packet packet)
        {
            // NOTE: We are already locked on tickLockObject.

            // Add clone of received packet to receivedPackets as packet passed to this callback is
            // returned to falconPeer's packet pool and the user-application will only read it 
            // sometime later.
            Packet clone = falconPeer.BorrowPacketFromPool();
            Packet.Clone(packet, clone, true);
            receivedPackets.Enqueue(clone);
        }

        private void Run()
        {
            while(true)
            {
                TimeSpan ellapsed = falconPeer.GetEllapsedSinceStarted();
                lock (tickLockObject)
                {
                    if (stopped)
                    {
                        return;
                    }
                    falconPeer.Update();
                }
                TimeSpan sleepTime = tickInterval - (falconPeer.GetEllapsedSinceStarted() - ellapsed);
                Thread.Sleep(sleepTime);
            }
        }

        public FalconOperationResult<object> Start()
        {
            lock(tickLockObject)
            {
                if(!stopped)
                    throw new InvalidOperationException("already started");
                stopped = false;
            }

            FalconOperationResult<object> result = falconPeer.TryStart();
            if(result.Success)
            {
                thread.Start();
            }
            return result;
        }

        public void JoinAsync(string addr, int port, string pass)
        {
            JoinAsync(new IPEndPoint(IPAddress.Parse(addr), port), pass);
        }

        public void JoinAsync(IPEndPoint ipEndPoint, string pass)
        {
            joinRequests.Enqueue(Tuple.Create(ipEndPoint, pass));
        }
        
        public bool Read(PacketReader reader)
        {
            bool packetAssigned = false;
            lock (tickLockObject)
            {
                // Take the opportunity to see if any peers have been added, removed or discovered and
                // raise events as appropriate and the caller's thread.

                if (peersAdded.Count > 0)
                {
                    if (PeerAdded == null)
                    {
                        peersAdded.Clear();
                    }
                    else
                    {
                        do
                        {
                            Tuple<int, Packet> peerAddedData = peersAdded.Dequeue();
                            PeerAdded(peerAddedData.Item1, peerAddedData.Item2); // TODO need an event delegate that takes a packet writer
                        } while (peersAdded.Count > 0);
                    }
                }

                if(peersRemoved.Count > 0)
                {
                    if(PeerDropped == null)
                    {
                        peersRemoved.Clear();
                    }
                    else
                    {
                        do
                        {
                            int peerId = peersRemoved.Dequeue();
                            PeerDropped(peerId);
                        } while (peersRemoved.Count > 0);
                    }
                }

                if(peersDiscovered.Count > 0)
                {
                    if(PeerDiscovered == null)
                    {
                        peersDiscovered.Clear();
                    }
                    else
                    {
                        do
                        {
                            IPEndPoint ip = peersDiscovered.Dequeue();
                            PeerDiscovered(ip);
                        } while (peersDiscovered.Count > 0);
                    }
                }

                // If any recieved packets assign to PacketReader and return true, otherwise assign
                // null to PacketReader and return false.

                if(receivedPackets.Count == 0)
                {
                    reader.AssignPacket(null);
                }
                else
                {
                    Packet packet = receivedPackets.Dequeue();
                    reader.AssignPacket(packet);
                    packetAssigned = true;
                }
            } // lock
            return packetAssigned;
        }

        public void SendToAll(SendOptions opts, PacketWriter writer)
        {
            falconPeer.EnqueueSendToAll(opts, writer.CurrentPacket);

            // Assign a new packet from the pool
            Packet packet = falconPeer.BorrowPacketFromPool();
            writer.AssignPacket(packet);
        }

        public void SendTo(int peerId, SendOptions opts, PacketWriter writer)
        {
            falconPeer.EnqueueSendTo(peerId, opts, writer.CurrentPacket);

            // Assign a new packet from the pool
            Packet packet = falconPeer.BorrowPacketFromPool();
            writer.AssignPacket(packet);
        }

        public PacketReader CreatePacketReader()
        {
            PacketReader reader = new PacketReader(falconPeer);
            return reader;
        }

        public PacketWriter CreatePacketWriter()
        {
            PacketWriter writer = new PacketWriter(falconPeer);
            return writer;
        }

        public void Stop()
        {
            lock (tickLockObject)
            {
                if(stopped)
                    throw new InvalidOperationException("already stopped");
                stopped = true;
            }
        }
    }
}
