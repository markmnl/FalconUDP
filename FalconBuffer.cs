using System.Diagnostics;

namespace FalconUDP
{
    public abstract class FalconBuffer
    {
        private int originalCount;

        internal byte[] BackingBuffer { get; private set; }
        internal int Offset { get; private set; }
        internal int Count { get; private set; }

        internal FalconBuffer()
        {

        }

        internal void Init(byte[] backingBuffer, int offset, int count)
        {
            this.BackingBuffer = backingBuffer;
            this.Offset = offset;
            this.Count = count;
            this.originalCount = count;
        }

        internal void SetCount(int newCount)
        {
            Debug.Assert(newCount > 0, "newCount must be > 0");
            Debug.Assert(newCount <= originalCount, "newCount cannot be greater than originalCount");

            Count = newCount;
        }

        internal void ResetCount()
        {
            Count = originalCount;
        }
    }
}
