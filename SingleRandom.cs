using System;

namespace FalconUDP
{
    internal class SingleRandom
    {
        public static Random Rand { get; private set; }

        static SingleRandom()
        {
            Rand = new Random();
        }
    }
}
