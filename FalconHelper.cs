using System;

namespace FalconUDP
{
    internal static class FalconHelper
    {
        internal static unsafe void WriteFalconHeader(byte[] dstBuffer, 
            int dstIndex, 
            PacketType type, 
            SendOptions opts, 
            ushort seq, 
            ushort payloadSize)
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotImplementedException();

            fixed (byte* ptr = &dstBuffer[dstIndex])
            {
                *ptr = (byte)((byte)opts | (byte)type);
                *(ushort*)(ptr + 1) = seq;
                *(ushort*)(ptr + 3) = payloadSize;
            }
        }

        internal static unsafe void WriteAdditionalFalconHeader(byte[] dstBuffer,
            int dstIndex,
            PacketType type,
            SendOptions opts,
            ushort payloadSize)
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotImplementedException();

            fixed (byte* ptr = &dstBuffer[dstIndex])
            {
                *ptr = (byte)((byte)opts | (byte)type);
                *(ushort*)(ptr + 1) = payloadSize;
            }
        }

        internal static void WriteAck(Ack ack, 
            byte[] dstBuffer, 
            int dstIndex, 
            long ellapsedMillisecondsNow)
        {
            long stopoverTime = ellapsedMillisecondsNow - ack.EllapsedTimeAtEnqueud;
            if (stopoverTime > ushort.MaxValue)
                stopoverTime = ushort.MaxValue; // TODO log warning?

            WriteFalconHeader(dstBuffer,
                dstIndex,
                ack.Type,
                ack.Channel,
                ack.Seq,
                (ushort)stopoverTime);
        }
    }
}
