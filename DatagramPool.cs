using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FalconUDP
{
    internal class DatagramPool
    {
        private List<byte[]> bigBackingBuffers;
        private Stack<Datagram> pool;
        private int buffersSize;
        private int packetsPerBigBuffer;
#if DEBUG
        private List<Datagram> leased;
#endif

        internal DatagramPool(int buffersSize, int initalNum)
        {
            this.buffersSize = buffersSize;
            this.pool = new Stack<Datagram>(initalNum);
            this.bigBackingBuffers = new List<byte[]>();
            this.packetsPerBigBuffer = initalNum;
#if DEBUG
            leased = new List<Datagram>();
#endif
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] bigBackingBuffer = new byte[buffersSize * packetsPerBigBuffer];
            for (int i = 0; i < packetsPerBigBuffer; i++)
            {
                Datagram buffer = new Datagram(bigBackingBuffer, i * buffersSize, buffersSize);
                this.pool.Push(buffer);
            }
            bigBackingBuffers.Add(bigBackingBuffer);
        }

        internal Datagram Borrow()
        {
            if (pool.Count == 0)
            {
                Debug.WriteLine("***DatagramPool depleted, newing up another pool, each pool is {0}bytes.***", buffersSize * packetsPerBigBuffer);
                GrowPool();
            }

            Datagram buffer = pool.Pop();
#if DEBUG
            leased.Add(buffer);
#endif
            return buffer;
        }

        internal void Return(Datagram buffer)
        {
#if DEBUG
            if (!leased.Contains(buffer))
                throw new InvalidOperationException("item not leased");
            leased.Remove(buffer);
#endif
            // reset count to original size
            buffer.Reset();

            pool.Push(buffer);
        }
    }
}
