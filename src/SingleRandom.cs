using System;

namespace FalconUDP
{
    internal static class SingleRandom
    {
        private static readonly Random random;

        static SingleRandom()
        {
            random = new Random();
        }

        internal static int Next()
        {
            return random.Next();
        }

        internal static int Next(int min, int max)
        {
            return random.Next(min, max);
        }

        internal static double NextDouble()
        {
            return random.NextDouble();
        }

        internal static void NextBytes(byte[] buffer)
        {
            random.NextBytes(buffer);
        }
    }
}
