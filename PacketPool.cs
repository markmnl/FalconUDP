using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FalconUDP
{
    /// <remarks>
    /// Pool will grow if exhausted by the same size as inital one however it is best to avoid this
    /// by predicting the max size needed. Once expanded the pool never shrinks.
    /// 
    /// Uses a single large buffers which can be divided up and assigned to Packets. This 
    /// enables buffers to be easily reused and guards against fragmenting heap memory.
    /// 
    /// Using the public API is thread safe.
    /// </remarks>
    internal class PacketPool
    {
        private List<byte[]> bigBackingBuffers;
        private Queue<Packet> pool;
        private int buffersSize;
        private int numPerPool;
#if DEBUG
        private List<Packet> leased;
#endif
        
        internal PacketPool(int buffersSize, int initalNum)
        {
            this.buffersSize        = buffersSize;
            this.pool               = new Queue<Packet>(initalNum);
            this.bigBackingBuffers  = new List<byte[]>();
            this.numPerPool         = initalNum;
#if DEBUG
            leased = new List<Packet>();
#endif      
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] bigBackingBuffer = new byte[buffersSize * numPerPool];
            for (int i = 0; i < numPerPool; i++)
            {
                Packet p = new Packet(bigBackingBuffer, i * buffersSize, buffersSize);
                this.pool.Enqueue(p);
            }
            bigBackingBuffers.Add(bigBackingBuffer);
        }

        internal Packet Borrow()
        {
            lock (pool)
            {
                if (pool.Count == 0)
                {
                    Debug.WriteLine("***PacketPool depleted, newing up another pool, each pool is {0}bytes.***", buffersSize * numPerPool);
                    GrowPool();
                }

                Packet p = pool.Dequeue();
                p.Init();
#if DEBUG
                leased.Add(p);
#endif
                return p;
            }
        }

        internal void Return(Packet p)
        {
            lock (pool)
            {
#if DEBUG
                if (!leased.Contains(p))
                    throw new InvalidOperationException("item not leased");
                leased.Remove(p);
#endif
                pool.Enqueue(p);
            }
        }
    }
}
