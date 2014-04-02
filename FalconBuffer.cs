using System.Diagnostics;

namespace FalconUDP
{
    internal class FalconBuffer
    {
        private readonly int originalCount;

        internal byte[] Buffer { get; private set; }
        internal int Offset { get; private set; }
        internal int Count { get; private set; }
        internal int Pos { get; private set; }
        internal object UserToken { get; set; }

        internal FalconBuffer(byte[] backingBuffer, int offset, int count)
        {
            this.Buffer = backingBuffer;
            this.Offset = offset;
            this.Count = count;
            this.originalCount = count;
        }

        internal void AdjustCount(int newCount)
        {
            Debug.Assert(newCount > 0, "newCount must be > 0");
            Debug.Assert(newCount <= originalCount, "newCount cannot be greater than originalCount");

            Count = newCount;
        }

        internal void ResetCount()
        {
            Count = originalCount;
        }

        internal void CopyBuffer(FalconBuffer src)
        {
            System.Buffer.BlockCopy(src.Buffer, src.Offset, Buffer, Offset, src.Count);
            AdjustCount(src.Count);
        }
    }
}
