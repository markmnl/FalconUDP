﻿
namespace FalconUDP
{
    // used when simulating delay
    internal class DelayedDatagram
    {
        internal float EllapsedSecondsRemainingToDelay;
        internal Datagram Datagram;

        internal DelayedDatagram(float seconds, Datagram datagram)
        {
            EllapsedSecondsRemainingToDelay = seconds;
            Datagram = datagram;
        }
    }
}
