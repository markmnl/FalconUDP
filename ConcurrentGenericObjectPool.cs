using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class ConcurrentGenericObjectPool<T> where T: new()
    {
        private ConcurrentBag<T> pool;
        private bool canGrow;
#if DEBUG
        private List<T> leased;
#endif

        internal ConcurrentGenericObjectPool(int initalCapacity)
        {
            this.pool = new ConcurrentBag<T>();

            for (int i = 0; i < initalCapacity; i++)
            {
                this.pool.Add(new T());
            }
#if DEBUG
            leased = new List<T>();
#endif
        }

        internal T Borrow()
        {
            T item;
            if (!pool.TryTake(out item))
            {
                item = new T();
            }

#if DEBUG
                if(leased.Contains(item))
                    throw new InvalidOperationException("item already leased");
                leased.Add(item);
#endif
            return item;
        }

        internal void Return(T item)
        {
#if DEBUG
            if (!leased.Contains(item))
                throw new InvalidOperationException("item not leased");
            leased.Remove(item);
#endif
            pool.Add(item);
        }
    }
}
