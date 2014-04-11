using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace FalconUDPTests
{
    [TestClass]
    public class PacketTests
    {
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

        private static Packet PacketProducer()
        {
            return new Packet();
        }

        [TestMethod]
        public void WriteReadBytesTest()
        {
            const int PACKET_SIZE = 10000;

            var pool = new BufferPool<Packet>(PACKET_SIZE, 1, PacketProducer);
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

            var pool = new BufferPool<Packet>(PACKET_SIZE, 1, PacketProducer);
            var packet = pool.Borrow();
            var bytes = new byte[PACKET_SIZE + 1];

            packet.WriteBytes(bytes);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void ReadOverflowTest()
        {
            const int PACKET_SIZE = 12;

            var pool = new BufferPool<Packet>(PACKET_SIZE, 1, PacketProducer);
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

            var pool = new BufferPool<Packet>(PACKET_SIZE, 1, PacketProducer);
            var packet = pool.Borrow();
            var rand = new Random();

            for (var i = 0; i < NUM_OF_LOOPS; i++)
            {
                //TODOpacket.Init();
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
    }
}
