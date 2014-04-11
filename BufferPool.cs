using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FalconUDP
{
    internal class BufferPool<T> where T: FalconBuffer
    {
        private int bufferSize;
        private int numOfBuffersPerPool;
        private Stack<T> pool;
        private Func<T> producer; 
#if DEBUG
        private HashSet<T> leased; 
#endif
 
        internal BufferPool(int bufferSize, int initalNumberOfBuffers, Func<T> producer)
        {
            this.bufferSize = bufferSize;
            this.numOfBuffersPerPool = initalNumberOfBuffers;
            this.pool = new Stack<T>();
            this.producer = producer;
#if DEBUG
            this.leased = new HashSet<T>();
#endif
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] backingBuffer = new byte[bufferSize * numOfBuffersPerPool];
            for(int i = 0; i < numOfBuffersPerPool; i++)
            {
                T buffer = producer();
                buffer.Init(backingBuffer, i * bufferSize, bufferSize);
                pool.Push(buffer);
            }
        }

        internal T Borrow()
        {
            if(pool.Count == 0)
                GrowPool();
            T buffer = pool.Pop();
#if DEBUG
            leased.Add(buffer);
#endif
            buffer.OnBorrow();
            return buffer;
        }

        internal void Return(T buffer)
        {
#if DEBUG
            Debug.Assert(leased.Contains(buffer), "buffer not from this pool!");
            leased.Remove(buffer);
#endif
            buffer.ResetCount();
            pool.Push(buffer);
        }
    }
}
