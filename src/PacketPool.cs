using System.Collections.Generic;
using System.Diagnostics;
using System;

namespace FalconUDP
{
    /// <remarks>
    /// Pool will grow if exhausted by the same size as inital one however it is best to avoid this
    /// by predicting the max size needed. Once expanded the pool never shrinks.
    /// 
    /// Uses a single large buffers which can be divided up and assigned to Packets. This 
    /// enables buffers to be easily reused and guards against fragmenting heap memory.
    /// </remarks>
    internal class PacketPool
    {
        private readonly List<byte[]> bigBackingBuffers;
        private readonly Stack<Packet> pool;
        private readonly int buffersSize;
        private readonly int packetsPerBigBuffer;
#if DEBUG
        private readonly List<Packet> leased;
#endif
        
        internal PacketPool(int buffersSize, int initalNum)
        {
            this.buffersSize        = buffersSize;
            this.pool               = new Stack<Packet>(initalNum);
            this.bigBackingBuffers  = new List<byte[]>();
            this.packetsPerBigBuffer         = initalNum;
#if DEBUG
            leased = new List<Packet>();
#endif      
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] bigBackingBuffer = new byte[buffersSize * packetsPerBigBuffer];
            for (int i = 0; i < packetsPerBigBuffer; i++)
            {
                Packet p = new Packet(bigBackingBuffer, i * buffersSize, buffersSize);
                this.pool.Push(p);
            }
            bigBackingBuffers.Add(bigBackingBuffer);
        }

        internal Packet Borrow()
        {
            if (pool.Count == 0)
            {
                Debug.WriteLine("***PacketPool depleted, newing up another pool, each pool is {0}bytes.***", buffersSize * packetsPerBigBuffer);
                GrowPool();
            }

            Packet p = pool.Pop();
            p.Init();
#if DEBUG
            leased.Add(p);
#endif
            return p;
        }

        internal void Return(Packet p)
        {
#if DEBUG
            if (!leased.Contains(p))
                throw new InvalidOperationException("item not leased");
            leased.Remove(p);
#endif
            pool.Push(p);
        }
    }
}
