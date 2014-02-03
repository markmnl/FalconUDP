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

    [TestClass()]
    public class FalconPeerTests
    {
        private const int START_PORT = 37986;
        private const int TICK_RATE = 20;
        private const int MAX_REPLY_WAIT_TIME = 5000; // milliseconds
        
        private static int portCount = START_PORT;
        private static Thread ticker;
        private static List<FalconPeer> activePeers, disableSendFromPeers;
        private static event ReplyReceived replyReceived;
        private static FalconPeer peerProcessingReceivedPacketsFor;
        private static object falconPeerLock = new object();

        #region Additional test attributes
        // 
        //You can use the following additional attributes as you write your tests:
        //
        //Use ClassInitialize to run code before running the first test in the class
        //[ClassInitialize()]
        //public static void MyClassInitialize(TestContext testContext)
        //{
        //}
        //
        //Use ClassCleanup to run code after all tests in a class have run
        //[ClassCleanup()]
        //public static void MyClassCleanup()
        //{
        //}
        //
        //Use TestInitialize to run code before running each test
        //[TestInitialize()]
        //public void MyTestInitialize()
        //{
        //}
        //
        //Use TestCleanup to run code after each test has run
        //[TestCleanup()]
        //public void MyTestCleanup()
        //{
        //}
        //
        #endregion
        
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
            FalconPeer peer = new FalconPeer(port, ProcessReceivedPacket, null, LogLevel.Debug);
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

        private void ConnectToLocalPeer(FalconPeer peer, FalconPeer remotePeer, string pass)
        {
            var mre = new ManualResetEvent(false);
            FalconOperationResult<int> result = null;
            peer.TryJoinPeerAsync("127.0.0.1", remotePeer.Port, rv =>
                {
                    result = rv;
                    mre.Set();
                }, pass);
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

        #region Starting Up

        [TestMethod]
        public void TryStartTest()
        {
            CreateAndStartLocalPeer();
        }

        #endregion

        #region Connecting

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

        #endregion

        #region Stopping
        #endregion

        #region Send and Receive

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

             
            host.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(host));

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

                    peer.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(peer));

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
            const int NUM_OF_PEERS              = 8;
            const int MAX_PACKET_SIZE           = 1024 - 33;
            const int NUM_ITERATIONS_PER_PEER   = 3;
            const int MAX_NUM_PACKETS_PER_PEER  = 2;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var otherPeers = ConnectXNumOfPeers(peer1, NUM_OF_PEERS-1, null);
            peer1.SetVisibility(false, null, false);

            var allPeers = new List<FalconPeer>(otherPeers);
            allPeers.Add(peer1);
            allPeers.ForEach(p => p.SetLogLevel(LogLevel.Debug));

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
                    //switch (rand.Next(4))
                    //{
                    //    case 1: opts = SendOptions.InOrder; break;
                    //    case 2: opts = SendOptions.Reliable; break;
                    //    case 3: opts = SendOptions.ReliableInOrder; break;
                    //}

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

        #endregion

        [TestMethod]
        public void KeepAliveTest()
        {
            const int TIME_TO_WAIT = 10000;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, true);
            var peer2 = CreateAndStartLocalPeer();
            
            ConnectToLocalPeer(peer2, peer1, null);

            Thread.Sleep(MAX_REPLY_WAIT_TIME); // allow AcceptJoin's ACK to get through

            // Stop peer2 without saying bye, wait a while and assert peer2 was dropped from peer1
            // which must be the result of a KeepAlive not being ACK'd since we never sent anything.

            peer2.Stop(false);

            Thread.Sleep(TIME_TO_WAIT);

            Assert.AreEqual(peer1.GetAllRemotePeers().Count, 0, "Peer 2 was stopped but is still connected to peer1!");
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

                waitHandel.WaitOne(2000);

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
            const int DELAY = 100;
            const int OUT_OF_RANGE_TOLERANCE = 10;
            const int NUM_OF_PINGS = 100;

            var peer1 = CreateAndStartLocalPeer();
            peer1.SetVisibility(true, null, false);
            var peer2 = CreateAndStartLocalPeer();
            var latencies = new int[NUM_OF_PINGS];
            var count = 0;
            var waitHandel = new AutoResetEvent(false);
            var sw = new Stopwatch();
            long elapsed = 0;

            ConnectToLocalPeer(peer2, peer1, null);

            peer1.SetSimulateLatency(DELAY, 0);
            peer2.SetSimulateLatency(DELAY, 0);
             
            replyReceived = null; // clears any listeners
            replyReceived += (sender, packet) =>
                {
                    int actual = (int)(sw.ElapsedMilliseconds - elapsed) / 2;
                    int estimated = packet.ElapsedMillisecondsSinceSent;
                    Debug.WriteLine("*** Estimated latency: {0}, actual: {1}", estimated, actual);
                    latencies[count] = packet.ElapsedMillisecondsSinceSent;
                    count++;
                    waitHandel.Set();
                };

            sw.Start();
            for (var i = 0; i < NUM_OF_PINGS; i++)
            {
                peer1.EnqueueSendToAll(SendOptions.ReliableInOrder, GetPingPacket(peer1));
                peer1.SendEnquedPackets();
                waitHandel.WaitOne();
                elapsed = sw.ElapsedMilliseconds;
            }

            Assert.IsTrue(Math.Abs(latencies.Average() - DELAY * 2) < OUT_OF_RANGE_TOLERANCE);
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

            peer2.DiscoverFalconPeersAsync(100, peer1.Port, DISCOVERY_TOKEN, ips => 
                {
                    if(ips!= null && ips.Length > 0 && ips[0].Port == peer1.Port)
                        discoveredPeer1 = true;
                    waitHandel.Set();
                });

            waitHandel.WaitOne();

            Assert.IsTrue(discoveredPeer1);

            discoveredPeer1 = false;

            peer2.DiscoverFalconPeersAsync(100, peer1.Port, Guid.NewGuid(), ips =>
                {
                    if (ips != null && ips.Length > 0 && ips[0].Port == peer1.Port)
                        discoveredPeer1 = true;
                    waitHandel.Set();
                });

            waitHandel.WaitOne();

            Assert.IsTrue(!discoveredPeer1);
        }
    }
}
