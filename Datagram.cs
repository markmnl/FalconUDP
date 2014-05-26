using System;

namespace FalconUDP
{
    internal class Datagram
    {
        private readonly int originalSize;

        internal bool IsResend;
        internal ushort Sequence;
        internal SendOptions SendOptions;
        internal int ResentCount;
        internal float EllapsedSecondsSincePacketSent;
        internal float EllapsedSecondsAtSent;

        public byte[] BackingBuffer { get; private set; }
        public int Offset { get; private set; }
        public int Count { get; private set; }
        public bool IsReliable
        {
            get { return (SendOptions & SendOptions.Reliable) == SendOptions.Reliable; }
        }
        public bool IsInOrder
        {
            get { return (SendOptions & SendOptions.InOrder) == SendOptions.InOrder; }
        }
        public int MaxCount
        {
            get { return originalSize; }
        }

        internal Datagram(byte[] backingBuffer, int offset, int count)
        {
            this.BackingBuffer = backingBuffer;
            this.Offset = offset;
            this.Count = count;
            this.originalSize = count;
        }

        internal void Resize(int newCount)
        {
            // can never be greater than original size
            if(Count > originalSize)
                throw new ArgumentOutOfRangeException("newCount", "cannot be greater than original cout");
            Count = newCount;
        }

        internal void Reset()
        {
            Count = originalSize;
            EllapsedSecondsSincePacketSent = 0.0f;
            ResentCount = 0;
            IsResend = false;
        }
    }
}
