
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
            fixed (byte* ptr = &dstBuffer[dstIndex])
            {
                *ptr = (byte)((byte)opts | (byte)type);
                *(ushort*)(ptr + 1) = payloadSize;
            }
        }

        internal static void WriteAck(AckDetail ack, byte[] dstBuffer, int dstIndex)
        {
            WriteFalconHeader(dstBuffer, dstIndex, PacketType.ACK, ack.Channel, ack.Seq, 0);
        }
    }
}
