using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace FalconUDPTests
{
    public enum FalconTestMessageType : byte
    {
        Ping,
        Pong,
        RandomBytes,
        RandomBytesReply,
    }

    public delegate void ReplyReceived(IPEndPoint sender, Packet packet);

    [TestClass]
    public class FalconPeerTests
    {
        private const int START_PORT = 37986;
        private const int TICK_RATE = 20;
        private const int MAX_REPLY_WAIT_TIME = 500; // milliseconds
        
        private static int portCount = START_PORT;
        private static Thread ticker;
        private static List<FalconPeer> activePeers, disableSendFromPeers;
        private static event ReplyReceived replyReceived;
        private static FalconPeer peerProcessingReceivedPacketsFor;
        private static object falconPeerLock = new object(); // lock on whenever calling something on a peer
                
        public FalconPeerTests()
        {
            activePeers = new List<FalconPeer>();
            disableSendFromPeers = new List<FalconPeer>();
            ticker = new Thread(MainLoop);
            ticker.Start();
            falconPeerLock = new object();
        }

        private static void ProcessReceivedPacket(Packet packet)
        {
            IPEndPoint sender;
            FalconPeer peer = peerProcessingReceivedPacketsFor;
            
            if (!peer.TryGetPeerIPEndPoint(packet.PeerId, out sender))
            {
                Debug.WriteLine("Failed to find IPEndPoint of peer packet received from!");
                //Assert.Fail("Failed to find IPEndPoint of peer packet received from!");
                return;
            }

            if (packet.BytesWritten == 0)
            {
                Debug.WriteLine("Empty packet!?");
                return;
            }

            var type = (FalconTestMessageType)packet.ReadByte();

            switch (type)
            {
                case FalconTestMessageType.Ping:
                    {
                        Debug.WriteLine("Ping received from: {0}, sending pong...", sender);
                        var pongPacket = peer.BorrowPacketFromPool();
                        pongPacket.WriteByte((byte)FalconTestMessageType.Pong);
                        peer.EnqueueSendTo(packet.PeerId, SendOptions.ReliableInOrder, pongPacket);
                    }
                    break;
                case FalconTestMessageType.Pong:
                    {
                        Debug.WriteLine("Pong received from: {0}!", sender);
                        if (replyReceived != null)
                        {
                            replyReceived(sender, packet);
                        }
                    }
                    break;
                case FalconTestMessageType.RandomBytes:
                    {
                        var opts = (SendOptions)packet.ReadByte();
                        //Assert.IsTrue(Enum.IsDefined(typeof(SendOptions), opts), "Invalid SendOptions");
                        if (!Enum.IsDefined(typeof(SendOptions), opts))
                        {
                            Debug.WriteLine("Invalid SendOptions");
                        }
                        var length = packet.ReadUInt16();
                        Debug.WriteLine(" -> RandomBytes received from: {0}, on channel: {1}, purported length: {2}, actual: {3}", sender, opts, length, packet.BytesRemaining);
                        var bytes = packet.ReadBytes(length);

                        var reply = peer.BorrowPacketFromPool();
                        reply.WriteByte((byte)FalconTestMessageType.RandomBytesReply);
                        reply.WriteUInt16((ushort)bytes.Length);
                        reply.WriteBytes(bytes);

                        peer.EnqueueSendTo(packet.PeerId, opts, reply);
                    }
                    break;
                case FalconTestMessageType.RandomBytesReply:
                    {
                        Debug.WriteLine(" <- RandomBytesReply received from: {0}", sender);
                        if (replyReceived != null)
                            replyReceived(sender, packet);
                    }
                    break;
                default:
                    {
                        //Assert.Fail("Unhandeled FalconTestMessagePacketType: " + type.ToString());
                        Debug.WriteLine("Unhandeled FalconTestMessagePacketType: " + type.ToString());
                    }
                    break;
            }
        }

        private void MainLoop()
        {
            while (true)
            {
                lock (falconPeerLock)
                {
                    lock (activePeers)
                    {
                        foreach (var peer in activePeers)
                        {
                            peerProcessingReceivedPacketsFor = peer;
                            if (peer.IsStarted)
                            {
                                peer.Update();

                                if (!disableSendFromPeers.Contains(peer))
                                {
                                    peer.SendEnquedPackets();
                                }
                            }
                        }
                    }
                }
                Thread.Sleep(TICK_RATE);
            }
        }

        #region Helper Methods

        private int GetUnusedPortNumber()
        {
            portCount++;
            while(IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(ip => ip.Port == portCount))
                portCount++;
            return portCount;
        }
        
        private FalconPeer CreateAndStartLocalPeer(int port = -1)
        {
            if(port == -1)
                port = GetUnusedPortNumber();
#if DEBUG
            FalconPeer peer = new FalconPeer(port, ProcessReceivedPacket, null, LogLevel.Debug);
#else
            FalconPeer peer = new FalconPeer(port, ProcessReceivedPacket);
#endif
            var tr = peer.TryStart(); 
            Assert.IsTrue(tr.Success, tr.NonSuccessMessage);
            if (tr.Success)
            {
                lock (activePeers)
                {
                    activePeers.Add(peer);
                }
            }
            return peer;
        }

        private void ConnectToLocalPeer(FalconPeer peer, FalconPeer remotePeer, string pass, Packet userData = null)
        {
            var mre = new ManualResetEvent(false);
            FalconOperationResult<int> result = null;
            lock (falconPeerLock)
            {
                peer.TryJoinPeerAsync("127.0.0.1", remotePeer.Port, pass, rv =>
                    {
                        result = rv;
                        mre.Set();
                    }, userData);
            }
            mre.WaitOne();
            Assert.IsTrue(result.Success, result.NonSuccessMessage);
        }

        private IEnumerable<FalconPeer> ConnectXNumOfPeers(FalconPeer host, int numOfOtherPeers, string pass)
        {
            var otherPeers = new List<FalconPeer>(numOfOtherPeers);

            for (var i = 0; i < numOfOtherPeers; i++)
            {
                var otherPeer = CreateAndStartLocalPeer();
                                
                // connect to the host
                ConnectToLocalPeer(otherPeer, host, pass);

                // connect to each other peer
                foreach (var peer in otherPeers)
                {
                    ConnectToLocalPeer(otherPeer, peer, pass); 
                }

                // allow future other peers to connect to this one
                otherPeer.SetVisibility(true, pass, false);

                otherPeers.Add(otherPeer);
            }

            otherPeers.ForEach(fp => fp.SetVisibility(false, null, false));

            return otherPeers;
        }

        private Packet GetPingPacket(FalconPeer peerToBorrowPacketFrom)
        {
            var pingPacket = peerToBorrowPacketFrom.BorrowPacketFromPool();
            pingPacket.WriteByte((byte)FalconTestMessageType.Ping);
            return pingPacket;
        }

        #endregion

        [TestMethod]
        public void TryStartTest()
        {
            CreateAndStartLocalPeer();
        }

        [TestMethod]
        public void ConnectToOnePeerTest()
        {
            var host = CreateAndStartLocalPeer();
            host.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(host, 1, null);
            host.SetVisibility(false, null, false);

            // TODO with pass, with/without setting accept join...
        }
        
        [TestMethod]
        public void Connect3PeersTest()
        {
            var host = CreateAndStartLocalPeer();
            host.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(host, 2, null);
            host.SetVisibility(false, null, false);

            var allPeers = new List<FalconPeer>(otherPeers);
            allPeers.Add(host);

            foreach (var peer in allPeers)
            {
                var remotePeers = peer.GetAllRemotePeers();
                Assert.AreEqual(allPeers.Count - 1, remotePeers.Count, "Failed to connect to all other peers");
            }
        }

        [TestMethod]
        public void ConnectTo32PeersTest()
        {
            var host = CreateAndStartLocalPeer();
            host.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(host, 31, null);
            host.SetVisibility(false, null, false);

            var allPeers = new List<FalconPeer>(otherPeers);
            allPeers.Add(host);

            foreach (var peer in allPeers)
            {
                var remotePeers = peer.GetAllRemotePeers();
                Assert.AreEqual(allPeers.Count - 1, remotePeers.Count, "Failed to connect to all other peers");
            }            
        }

        [TestMethod]
        public void PingPongOnePeer()
        {
            var host = CreateAndStartLocalPeer();
            host.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(host, 1, null);
            var otherPeer = otherPeers.First();
            host.SetVisibility(false, null, false);
            
            var pongReceived = false;
            replyReceived = null; // clears any listeners
            replyReceived += (sender, packet) => 
                {
                    pongReceived = true;
                };

            lock (falconPeerLock)
            {
                host.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(host));
            }

            Thread.Sleep(MAX_REPLY_WAIT_TIME);

            Assert.IsTrue(pongReceived, "Pong from Ping not received in time!");
        }

        [TestMethod]
        public void PingPong3Peers()
        {
            const int NUM_OF_PINGS = 50;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(peer1, 2, null);
            peer1.SetVisibility(false, null, false);

            var allPeers = new List<FalconPeer>(otherPeers);
            allPeers.Add(peer1);

            foreach (var peer in allPeers)
            {
                var remotePeers = peer.GetAllRemotePeers();
                Assert.AreEqual(allPeers.Count - 1, remotePeers.Count, "Failed to connect to all other peers");
                
                for (var i = 0; i < NUM_OF_PINGS; i++)
                {
                    var are = new AutoResetEvent(false);
                    var pongsReceived = 0;
                    replyReceived = null; // clears any listeners
                    replyReceived += (sender, packet) =>
                        {
                            pongsReceived++;
                            if (pongsReceived == remotePeers.Count)
                                are.Set();
                        };

                    lock (falconPeerLock)
                    {
                        peer.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(peer));
                    }

                    //---------------------------------------------------
                    are.WaitOne(remotePeers.Count * MAX_REPLY_WAIT_TIME);
                    //---------------------------------------------------

                    Assert.AreEqual(remotePeers.Count, pongsReceived, "Never recieved reply Pongs from all other peers in time!");
                }
            }
        }

        [TestMethod]
        public void FalconPeerStressTest()
        {
            // NOTE: beware of overflowing the network buffer
            const int NUM_OF_PEERS              = 5;
            const int MAX_PACKET_SIZE           = 100;
            const int NUM_ITERATIONS_PER_PEER   = 10;
            const int MAX_NUM_PACKETS_PER_PEER  = 3;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(peer1, NUM_OF_PEERS-1, null);
            peer1.SetVisibility(false, null, false);

            var allPeers = new List<FalconPeer>(otherPeers);
            allPeers.Add(peer1);

#if DEBUG
            allPeers.ForEach(p => p.SetLogLevel(LogLevel.Debug));
#endif

            var rand = new Random();
            var numRemotePeers = NUM_OF_PEERS-1;

            foreach (var peer in allPeers)
            {
                for (var i = 0; i < NUM_ITERATIONS_PER_PEER; i++)
                {
                    var repliesLock = new object();
                    var packetsSentCount = 0;
                    var packetsReceivedCount = 0;
                    Debug.WriteLine("---packetsReceivedCount reset---", packetsReceivedCount);
                    var totalBytesToSend = rand.Next(1, MAX_PACKET_SIZE + 1);
                    var waitHandel = new AutoResetEvent(false);

                    SendOptions opts = SendOptions.ReliableInOrder;
                    switch (rand.Next(4))
                    {
                        case 1: opts = SendOptions.InOrder; break;
                        case 2: opts = SendOptions.Reliable; break;
                        case 3: opts = SendOptions.ReliableInOrder; break;
                    }

                    var replies = new List<byte[]>();
                    replyReceived = null; // clears any listeners
                    replyReceived += (sender, packetReceived) =>
                        {
                            lock (repliesLock)
                            {
                                var size = packetReceived.ReadUInt16();
                                var receivedBytes = packetReceived.ReadBytes(size);
                                replies.Add(receivedBytes);

                                ++packetsReceivedCount;
                                Debug.WriteLine("++packetsReceivedCount now: {0}", packetsReceivedCount);

                                if (packetsReceivedCount == (numRemotePeers * packetsSentCount))
                                    waitHandel.Set();
                            }
                        };

                    lock (falconPeerLock)
                    {
                        var numOfPacketsToSend = rand.Next(1, MAX_NUM_PACKETS_PER_PEER + 1);
                        for (var j = 0; j < numOfPacketsToSend; j++)
                        {
                            var length = rand.Next(1, totalBytesToSend + 1);
                            var bytes = new byte[length];
                            rand.NextBytes(bytes);
                            var packet = peer.BorrowPacketFromPool();
                            packet.WriteByte((byte)FalconTestMessageType.RandomBytes);
                            packet.WriteByte((byte)opts);
                            packet.WriteUInt16((ushort)length);
                            packet.WriteBytes(bytes);
                            peer.EnqueueSendToAll(opts, packet);
                            packetsSentCount++;
                        }

                        peer.SendEnquedPackets();
                    }

                    //------------------------------------------------------
                    waitHandel.WaitOne(numRemotePeers * MAX_REPLY_WAIT_TIME);
                    //------------------------------------------------------

                    lock (repliesLock)
                    {
                        Assert.AreEqual(packetsSentCount * numRemotePeers, packetsReceivedCount);
                        replies.Clear();
                    }
                }
            }
        }
        
        [TestMethod]
        public void KeepAliveTest()
        {
            const int NUM_OF_PEERS = 5;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, true);
            var otherPeers = ConnectXNumOfPeers(peer1, NUM_OF_PEERS - 1, null);
            
            Thread.Sleep(MAX_REPLY_WAIT_TIME); // allow AcceptJoin's ACK to get through

            // Stop other peers without saying bye, wait a while and assert they were dropped from 
            // peer1 which must be the result of a KeepAlive not being ACK'd since we never sent 
            // anything.
            foreach (var otherPeer in otherPeers)
            {
                otherPeer.Stop(false);
            }

            // NOTE: Time to wait must be > (KeepAliveInterval x MaxResend) + AckTimout + Any error marine
            int timeToWait = ((int)(peer1.KeepAliveInterval + peer1.AckTimeout).TotalMilliseconds * peer1.MaxMessageResends);
            timeToWait += TICK_RATE;
            Thread.Sleep(timeToWait);

            var connectedPeers = peer1.GetAllRemotePeers();
            Assert.AreEqual(connectedPeers.Count, 0, String.Format("{0} other peers were stopped but are still connected to peer1!", connectedPeers.Count));
        }

        [TestMethod]
        public void AutoFlushTest()
        {
            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, true);
            var peer2 = CreateAndStartLocalPeer();

            try
            {
                ConnectToLocalPeer(peer2, peer1, null);

                Thread.Sleep(MAX_REPLY_WAIT_TIME); // allow AcceptJoin's ACK to get through

                var pongReceived = false;
                var waitHandel = new AutoResetEvent(false);
                replyReceived = null; // clears any listeners
                replyReceived += (sender, packet) =>
                    {
                        pongReceived = true;
                        waitHandel.Set();
                    };

                // Enqueue ping and disable sending and see if get reply pong - which must be from
                // auto flush.

                lock (activePeers)
                {
                    peer1.EnqueueSendToAll(SendOptions.Reliable, GetPingPacket(peer1));
                    disableSendFromPeers.Add(peer1);
                }

                waitHandel.WaitOne(peer1.AutoFlushInterval + TimeSpan.FromMilliseconds(TICK_RATE * 2) + TimeSpan.FromMilliseconds(MAX_REPLY_WAIT_TIME));

                Assert.IsTrue(pongReceived, "Reply pong not received");
            }
            finally
            {
                lock (activePeers)
                {
                    disableSendFromPeers.Remove(peer1);
                }
            }
        }

        [TestMethod]
        public void SimulateLatencyTest()
        {
            TimeSpan DELAY = TimeSpan.FromMilliseconds(100);
            const int OUT_OF_RANGE_TOLERANCE = 10; // NOTE: in DEBUG need wider tolerance to allow for Debug.WriteLine()'s
            const int NUM_OF_PINGS = 100;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var peer2 = CreateAndStartLocalPeer();
            var estimatedLatencies = new int[NUM_OF_PINGS];
            var count = 0;
            var waitHandel = new AutoResetEvent(false);
            var sw = new Stopwatch();
            long ellapsedAtSend = 0;

            ConnectToLocalPeer(peer2, peer1, null);

            peer1.SetSimulateLatency(DELAY, TimeSpan.Zero);
            peer2.SetSimulateLatency(DELAY, TimeSpan.Zero);
             
            replyReceived = null; // clears any listeners
            replyReceived += (sender, packet) =>
                {
                    lock (falconPeerLock) // we should already be in one, but no harm
                    {
                        int actual = (int)(sw.ElapsedMilliseconds - ellapsedAtSend) / 2;
                        int estimated = packet.ElapsedMillisecondsSinceSent;
                        Debug.WriteLine("*** Estimated latency: {0}, actual: {1}", estimated, actual);
                        estimatedLatencies[count] = packet.ElapsedMillisecondsSinceSent;
                        count++;
                        waitHandel.Set();
                    }
                };

            sw.Start();
            for (var i = 0; i < NUM_OF_PINGS; i++)
            {
                lock (falconPeerLock)
                {
                    peer1.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(peer1));
                    ellapsedAtSend = sw.ElapsedMilliseconds;
                    peer1.SendEnquedPackets();
                }
                waitHandel.WaitOne();
            }

            double avg = estimatedLatencies.Average();
            double diff = Math.Abs(avg - DELAY.TotalMilliseconds);
            Assert.IsTrue(diff < OUT_OF_RANGE_TOLERANCE, "Average estimated latency {0} differs from expected: {1} by: {2}ms", avg, DELAY, diff);
        }

        [TestMethod]
        public void DiscoveryTest()
        {
            Guid DISCOVERY_TOKEN = Guid.NewGuid();

            var peer1 = CreateAndStartLocalPeer();
            var peer2 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, true, false, DISCOVERY_TOKEN);

            var waitHandel = new ManualResetEvent(false);
            var discoveredPeer1 = false;

            peer2.DiscoverFalconPeersAsync(TimeSpan.FromMilliseconds(100), peer1.Port, DISCOVERY_TOKEN, ips => 
                {
                    if(ips!= null && ips.Length > 0 && ips[0].Port == peer1.Port)
                        discoveredPeer1 = true;
                    waitHandel.Set();
                });

            waitHandel.WaitOne();

            Assert.IsTrue(discoveredPeer1);

            discoveredPeer1 = false;

            peer2.DiscoverFalconPeersAsync(TimeSpan.FromMilliseconds(100), peer1.Port, Guid.NewGuid(), ips =>
                {
                    if (ips != null && ips.Length > 0 && ips[0].Port == peer1.Port)
                        discoveredPeer1 = true;
                    waitHandel.Set();
                });

            waitHandel.WaitOne();

            Assert.IsTrue(!discoveredPeer1);
        }

        [TestMethod]
        public void JoinUserDataTest()
        {
            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var bytesSent = new byte[123];
            SingleRandom.NextBytes(bytesSent);
            object myLock = new object();
            var waitHandel = new AutoResetEvent(false);
            byte[] bytesReceived = null;

            peer1.PeerAdded += (int id, Packet userDataPacketReceived) =>
                {
                    lock (myLock)
                    {
                        bytesReceived = userDataPacketReceived.ReadBytes(userDataPacketReceived.BytesRemaining);
                        waitHandel.Set();
                    }
                };

            var peer2 = CreateAndStartLocalPeer();
            var userData = peer1.BorrowPacketFromPool();
            userData.WriteBytes(bytesSent);
            ConnectToLocalPeer(peer2, peer1, null, userData);
            peer1.ReturnPacketToPool(userData);

            waitHandel.WaitOne(MAX_REPLY_WAIT_TIME);

            lock (myLock)
            {
                Assert.IsTrue(bytesSent.SequenceEqual(bytesReceived), "user data received in JoinRequest not as sent");
            }
        }

        [TestMethod]
        public void FalconAnonoymousPingTest()
        {
            var peer1 = CreateAndStartLocalPeer();
            var peer2 = CreateAndStartLocalPeer();
            var myLock = new object();
            var pongReceived = false;
            var waitHandel = new AutoResetEvent(false);

            peer1.SetVisibility(true, null, false, true);

            peer2.PongReceivedFromUnknownPeer += (IPEndPoint ip, TimeSpan rtt) =>
                {
                    lock (myLock)
                    {
                        pongReceived = true;
                    }
                };

            peer2.PingEndPoint(new IPEndPoint(IPAddress.Loopback, peer1.Port));

            waitHandel.WaitOne(MAX_REPLY_WAIT_TIME);

            lock (myLock)
            {
                Assert.IsTrue(pongReceived, "Reply Pong not received from unknown Ping in time.");
            }
        }

        [TestMethod]
        public void FalconPingPeerTest()
        {
            var peer1 = CreateAndStartLocalPeer();
            var peer2 = CreateAndStartLocalPeer();
            var myLock = new object();
            var pongReceived = false;
            var waitHandel = new AutoResetEvent(false);

            peer1.SetVisibility(true, null, false, false);
            ConnectToLocalPeer(peer2, peer1, null);

            peer2.PongReceivedFromPeer += (int id, TimeSpan rtt) =>
            {
                lock (myLock)
                {
                    pongReceived = true;
                }
            };

            var peer1Id = peer2.GetAllRemotePeers().First().Key;
            peer2.PingPeer(peer1Id);

            waitHandel.WaitOne(MAX_REPLY_WAIT_TIME);

            lock (myLock)
            {
                Assert.IsTrue(pongReceived, "Reply Pong not received from Ping to known peer in time.");
            }
        }

        [TestMethod]
        public void SimulatePacketLossTest()
        {
            const int NUM_OF_PACKETS            = 1000;
            const double PACKET_LOSS_PERCENTAGE = 50.0;

            var peer1 = CreateAndStartLocalPeer();
            var peer2 = CreateAndStartLocalPeer();
            var count = 0;

            peer1.SetVisibility(true, null, false);
            
            ConnectToLocalPeer(peer2, peer1, null);

            peer2.SetSimulatePacketLoss(PACKET_LOSS_PERCENTAGE);

            replyReceived = null; // clears any listeners
            replyReceived += (sender, packet) =>
                {
                    count++;
                };

            // Send NUM_OF_PACKETS from peer2 to peer1 and assert within 10% PACKET_LOSS_PERCENTAGE
            // of packets were lost.

            for (var i = 0; i < NUM_OF_PACKETS; i++)
            {
                peer2.EnqueueSendToAll(SendOptions.None, GetPingPacket(peer2));
                peer2.SendEnquedPackets();
                Thread.Sleep(1);
            }

            Assert.IsTrue(((count / NUM_OF_PACKETS) * 100.0) - PACKET_LOSS_PERCENTAGE < 10);
        }
    }
}
