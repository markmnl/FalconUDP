using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace FalconUDPTests
{
    [TestClass]
    public class PacketTests
    {
        [TestMethod]
        public void WriteReadBytesTest()
        {
            const int PACKET_SIZE = 10000;

            var pool = new PacketPool(PACKET_SIZE, 1);
            var packet = pool.Borrow();
            var bytes = new byte[PACKET_SIZE];
            var rand = new Random();

            rand.NextBytes(bytes);
            
            packet.WriteBytes(bytes);

            Assert.AreEqual(PACKET_SIZE, packet.BytesWritten);

            packet.ResetAndMakeReadOnly(0);

            var readBytes = packet.ReadBytes(PACKET_SIZE);

            Assert.AreEqual(0, packet.BytesRemaining);
            Assert.IsTrue(Enumerable.SequenceEqual(bytes, readBytes), "Bytes written not the same when read!");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))] 
        public void WriteOverflowTest()
        {
            const int PACKET_SIZE = 12;

            var pool = new PacketPool(PACKET_SIZE, 1);
            var packet = pool.Borrow();
            var bytes = new byte[PACKET_SIZE + 1];

            packet.WriteBytes(bytes);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void ReadOverflowTest()
        {
            const int PACKET_SIZE = 12;

            var pool = new PacketPool(PACKET_SIZE, 1);
            var packet = pool.Borrow();
            var bytes = new byte[4];

            packet.WriteBytes(bytes);
            packet.ResetAndMakeReadOnly(0);

            packet.ReadBytes(bytes.Length+1);
        }

        [TestMethod]
        public void RandomStressTest()
        {
            const int PACKET_SIZE = 123;
            const int NUM_OF_LOOPS = 10000;

            var pool = new PacketPool(PACKET_SIZE, 1);
            var packet = pool.Borrow();
            var rand = new Random();

            for (var i = 0; i < NUM_OF_LOOPS; i++)
            {
                packet.Init();
                var bytes = new byte[rand.Next(PACKET_SIZE+1)];
                rand.NextBytes(bytes);
                packet.WriteBytes(bytes);
                Assert.AreEqual(bytes.Length, packet.BytesWritten);
                packet.ResetAndMakeReadOnly(0);
                var numOfBytesToRead = rand.Next(bytes.Length + 1);
                var readBytes = packet.ReadBytes(numOfBytesToRead);
                Assert.AreEqual(bytes.Length - numOfBytesToRead, packet.BytesRemaining);
                Assert.IsTrue(Enumerable.SequenceEqual(bytes.Take(numOfBytesToRead), readBytes.Take(numOfBytesToRead)), "Bytes written not the same when read!");
            }
        }

        [TestMethod]
        public void TestReadWriteVariableLengthInt32()
        {
            var pool = new PacketPool(5, 1);
            var packet = pool.Borrow();

            int valueAsRead;
            for(int i = 0; i < Int32.MaxValue && i >= 0; i+=127)
            {
                packet.WriteVariableLengthInt32(i);
                packet.ResetAndMakeReadOnly(-1);
                valueAsRead = packet.ReadVariableLengthInt32();
                Assert.AreEqual(i, valueAsRead, "Value written: {0}, not as read: {1}", i, valueAsRead);
                packet.Init();
            }

            // test MaxValue too which the above loop did not
            packet.WriteVariableLengthInt32(Int32.MaxValue);
            packet.ResetAndMakeReadOnly(-1);
            valueAsRead = packet.ReadVariableLengthInt32();
            Assert.AreEqual(Int32.MaxValue, valueAsRead, "Value written: {0}, not as read: {1}", Int32.MaxValue, valueAsRead);
        }
    }
}
