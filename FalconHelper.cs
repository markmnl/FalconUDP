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

        internal static void WriteAck(AckDetail ack, 
            byte[] dstBuffer, 
            int dstIndex)
        {
            ushort stopoverTime = ack.EllapsedMillisecondsSincetEnqueud > ushort.MaxValue ? ushort.MaxValue : (ushort)ack.EllapsedMillisecondsSincetEnqueud; // TODO log warning if was greater than MaxValue

            WriteFalconHeader(dstBuffer,
                dstIndex,
                ack.Type,
                ack.Channel,
                ack.Seq,
                stopoverTime);
        }
    }
}
