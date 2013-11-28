using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class ConcurrentGenericObjectPool<T> where T: new()
    {
        private Stack<T> pool; // NOTE: using a ConcurrentBag resulted in SocketAsyncEventArgs somehow being disposed when in pool?!
        private bool canGrow;
#if DEBUG
        private List<T> leased;
#endif

        internal ConcurrentGenericObjectPool(int initalCapacity, bool canGrow)
        {
            this.pool = new Stack<T>(initalCapacity);
            this.canGrow = canGrow;

            for (int i = 0; i < initalCapacity; i++)
            {
                this.pool.Push(new T());
            }
#if DEBUG
            leased = new List<T>();
#endif
        }

        internal T Borrow()
        {
            T item;
            lock (pool)
            {
                if (pool.Count > 0)
                    item = pool.Pop();
                else if(canGrow)
                    item = new T();
                else
                    throw new InvalidOperationException("pool depleted");
#if DEBUG
                if(leased.Contains(item))
                    throw new InvalidOperationException("item already leased");
                leased.Add(item);
#endif
            }
            return item;
        }

        internal void Return(T item)
        {
            lock (pool)
            {
#if DEBUG
                if (!leased.Contains(item))
                    throw new InvalidOperationException("item not leased");
                leased.Remove(item);
#endif
                pool.Push(item);
            }
        }
    }
}
