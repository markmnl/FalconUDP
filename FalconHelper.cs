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

        internal static void WriteAck(AckDetail ack, 
            byte[] dstBuffer, 
            int dstIndex)
        {
            float ellapsedMilliseconds = ack.EllapsedSecondsSinceEnqueud * 1000.0f;
            ushort stopoverMilliseconds = ellapsedMilliseconds > ushort.MaxValue 
                ? ushort.MaxValue 
                : (ushort)ellapsedMilliseconds; // TODO log warning if was greater than MaxValue

            WriteFalconHeader(dstBuffer,
                dstIndex,
                ack.Type,
                ack.Channel,
                ack.Seq,
                stopoverMilliseconds);
        }

#if DEBUG
        internal static void ReadFalconHeader(byte[] buffer, 
            int index, 
            out PacketType type, 
            out SendOptions opts, 
            out ushort seq, 
            out ushort payloadSize)
        {
            byte packetDetail = buffer[index];
            opts = (SendOptions)(byte)(packetDetail & Const.SEND_OPTS_MASK);
            type = (PacketType)(byte)(packetDetail & Const.PACKET_TYPE_MASK);
            seq = BitConverter.ToUInt16(buffer, index + 1);
            payloadSize = BitConverter.ToUInt16(buffer, index + 3);
        }
#endif
    }
}
