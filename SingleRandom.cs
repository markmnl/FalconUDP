using System;
using System.Threading;

namespace FalconUDP
{
    internal static class SingleRandom
    {
        private static Random random;
        private static object myLock;

        static SingleRandom()
        {
            random = new Random();
            myLock = new object();
        }

        internal static int Next()
        {
            lock (myLock)
            {
                return random.Next();
            }
        }

        internal static int Next(int min, int max)
        {
            lock (myLock)
            {
                return random.Next(min, max);
            }
        }

        internal static double NextDouble()
        {
            lock (myLock)
            {
                return random.NextDouble();
            }
        }
    }
}
