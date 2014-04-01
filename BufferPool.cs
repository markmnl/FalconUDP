using System.Collections.Generic;
using System.Diagnostics;

namespace FalconUDP
{
    internal class BufferPool
    {
        private int bufferSize;
        private int numOfBuffersPerPool;
        private Stack<FalconBuffer> pool;
#if DEBUG
        private HashSet<FalconBuffer> leased; 
#endif
 
        internal BufferPool(int bufferSize, int initalNumberOfBuffers)
        {
            this.bufferSize = bufferSize;
            this.numOfBuffersPerPool = initalNumberOfBuffers;
            this.pool = new Stack<FalconBuffer>();
#if DEBUG
            this.leased = new HashSet<FalconBuffer>();
#endif
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] backingBuffer = new byte[bufferSize * numOfBuffersPerPool];
            for(int i = 0; i < numOfBuffersPerPool; i++)
            {
                FalconBuffer buffer = new FalconBuffer(backingBuffer, i * bufferSize, bufferSize);
                pool.Push(buffer);
            }
        }

        internal FalconBuffer Borrow()
        {
            if(pool.Count == 0)
                GrowPool();
            FalconBuffer buffer = pool.Pop();
#if DEBUG
            leased.Add(buffer);
#endif
            return buffer;
        }

        internal void Return(FalconBuffer buffer)
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
