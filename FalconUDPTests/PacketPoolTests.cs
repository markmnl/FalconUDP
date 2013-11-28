using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FalconUDPTests
{
    [TestClass()]
    public class PacketPoolTests
    {
        [TestMethod]
        public void PacketPoolStressTest()
        {
            // Borrow NUM_OF_PACKETS packets on random thread pool threads write/read random 
            // number of bytes and return them at random intervals on other random thread pool 
            // threads.

            const int NUM_OF_LOOPS              = 1000000;
            const int PACKET_SIZE               = 1400;
            const int POOL_SIZE                 = 200;
            const int TIMER_TICK_RATE           = 1;

            var pool = new PacketPool(PACKET_SIZE, POOL_SIZE);
            var leasedPackets = new List<Packet>();
            var rand = new Random();
            var timer = new Timer(state => 
                {
                    lock (leasedPackets)
                    {
                        var numOfPacketsToReturn = rand.Next(leasedPackets.Count);
                        for (var i = 0; i < numOfPacketsToReturn; i++)
                        {
                            var packet = leasedPackets[0];
                            leasedPackets.RemoveAt(0);
                            pool.Return(packet);
                        }
                    }
                }, null, TIMER_TICK_RATE, TIMER_TICK_RATE);

            for (var i = 0; i < NUM_OF_LOOPS; i++)
            {
                Task.Factory.StartNew(() =>
                    {
                        var packet = pool.Borrow();
                        List<byte> bytesWrote = new List<byte>(PACKET_SIZE);
                        while (packet.BytesWritten < PACKET_SIZE)
                        {
                            var sizeToWrite = rand.Next((PACKET_SIZE - packet.BytesWritten) + 1);
                            var bytes = new byte[sizeToWrite];
                            rand.NextBytes(bytes);
                            packet.WriteBytes(bytes);
                            bytesWrote.AddRange(bytes);
                        }
                        packet.IsReadOnly = true;
                        packet.ResetPos();
                        List<byte> bytesRead = new List<byte>(PACKET_SIZE);
                        while (packet.BytesRemaining > 0)
                        {
                            var numOfBytesToRead = rand.Next(packet.BytesRemaining + 1);
                            bytesRead.AddRange(packet.ReadBytes(numOfBytesToRead));
                        }

                        Assert.IsTrue(Enumerable.SequenceEqual(bytesWrote, bytesRead), "Bytes written not the same when read!");

                        lock (leasedPackets)
                        {
                            leasedPackets.Add(packet);
                        }
                    });
            }
        }
    }
}
