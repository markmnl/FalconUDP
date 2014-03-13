using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;

namespace FalconUDP
{
    internal class SocketAsyncEventArgsPool
    {
        private Queue<SocketAsyncEventArgs> pool;
        private Func<SocketAsyncEventArgs> getNewArgsFunc;
        private List<byte[]> bigBackingBuffers;
        private int buffersSize;
        private int numPerPool;
        
        internal SocketAsyncEventArgsPool(int buffersSize, int numPerPool, Func<SocketAsyncEventArgs> getNewArgsFunc)
        {
            this.buffersSize        = buffersSize;
            this.numPerPool         = numPerPool;
            this.getNewArgsFunc     = getNewArgsFunc;
            this.pool               = new Queue<SocketAsyncEventArgs>(numPerPool);
            this.bigBackingBuffers  = new List<byte[]>();
            
            GrowPool();
        }

        private void GrowPool()
        {
            byte[] bigBackingBuffer = new byte[buffersSize * numPerPool];
            for (int i = 0; i < numPerPool; i++)
            {
                SocketAsyncEventArgs args = getNewArgsFunc();
                args.SetBuffer(bigBackingBuffer, i * buffersSize, buffersSize);
                this.pool.Enqueue(args);
            }
            bigBackingBuffers.Add(bigBackingBuffer);
        }

        internal SocketAsyncEventArgs Borrow()
        {
            if (pool.Count == 0)
            {
                Debug.WriteLine("***SocketAsyncEventArgsPool depleted, newing up another pool, each pool is {0}bytes***", buffersSize * numPerPool);
                GrowPool();
            }

            return pool.Dequeue();
        }

        internal void Return(SocketAsyncEventArgs args)
        {
            if (args == null)
                throw new ArgumentNullException();

            if (args.UserToken != null)
                args.UserToken = null;

            // set the buffer back to its orignal size
            if (args.Count != buffersSize)
                args.SetBuffer(args.Offset, buffersSize);

            pool.Enqueue(args);
        }
    }
}
