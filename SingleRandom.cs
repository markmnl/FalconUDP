using System;
using System.Threading;

namespace FalconUDP
{
    internal static class SingleRandom
    {
        private static Random random;
        private static bool gotLock;
        private static SpinLock spinLock;

        static SingleRandom()
        {
            random = new Random();
        }

        internal static int Next()
        {
            gotLock = false;
            spinLock.Enter(ref gotLock);
            int rv = random.Next();
            if(gotLock)
                spinLock.Exit(false); // ASSUMPTION not IA64, TODO what if ARM?
            return rv;
        }

        internal static int Next(int min, int max)
        {
            gotLock = false;
            spinLock.Enter(ref gotLock);
            int rv = random.Next(min, max);
            if (gotLock)
                spinLock.Exit(false); // ASSUMPTION not IA64, TODO what if ARM?
            return rv;
        }

        internal static double NextDouble()
        {
            gotLock = false;
            spinLock.Enter(ref gotLock);
            double rv = random.NextDouble();
            if (gotLock)
                spinLock.Exit(false); // ASSUMPTION not IA64, TODO what if ARM?
            return rv;
        }
    }
}
