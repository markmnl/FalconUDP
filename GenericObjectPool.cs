using System.Collections.Generic;

namespace FalconUDP
{
    internal class GenericObjectPool<T> where T: new()
    {
        private Stack<T> pool;
#if DEBUG
        private List<T> leased;
#endif

        internal GenericObjectPool(int initalCapacity)
        {
            this.pool = new Stack<T>();

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
            T item = pool.Count > 0 ? pool.Pop() : new T();

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
            pool.Push(item);
        }
    }
}
