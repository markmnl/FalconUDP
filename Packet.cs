using System;
using System.Net;
using System.Text;

namespace FalconUDP
{
    /// <summary>
    /// Represents a Packet to be sent or received.
    /// </summary>
    /// <remarks>
    /// An instance of a Packet must be obtained from <see cref="FalconPeer.BorrowPacketFromPool()"/>. 
    /// Once finished with the packet must be returned to the pool using <see cref="FalconPeer.ReturnPacketToPool(Packet)"/>
    /// , the Packet must not be re-used, always get another Packet by borrowing another one from 
    /// <see cref="FalconPeer.BorrowPacketFromPool()"/>.
    /// </remarks>
    public class Packet
    {
        /// <summary>
        /// FalconUDP Peer Id this packet was received from. Only set on received packets.
        /// </summary>
        public int PeerId { get; internal set; }

        /// <summary>
        /// Estimated Elapsed milliseconds since sent. Only set on received packets.
        /// </summary>
        /// <remarks>
        /// One way time taken for the packet to arrive is estimated on time taken to receive ACKs
        /// from remote peer using the formula:
        /// 
        ///     latency = ACK round trip time for last Settings.LatencySampleSize ACKs / (Settings.LatencySampleSize * 2)
        /// 
        /// This is then added to the time taken till the packet is read using <see cref="FalconPeer.ProcessReceivedPackets"/>
        /// ACKs are only sent in response to Reliable packets (which includes KeepAlives).
        /// </remarks>
        public int ElapsedMillisecondsSinceSent { get; internal set; }

        /// <summary>
        /// Number bytes remaining to be read from this Packet from the current pos.
        /// </summary>
        public int BytesRemaining { get { return BytesWritten - (pos - offset); } }

        /// <summary>
        /// Number of bytes written to this Packet.
        /// </summary>
        public int BytesWritten { get; private set; }

        internal bool IsReadOnly;
        internal long ElapsedTimeAtReceived;
        internal ushort DatagramSeq;

        private byte[] backingBuffer;
        private int offset;
        private int count;
        private int pos;
           
        // do not construct, get from a pool
        internal Packet(byte[] backingBuffer, int offset, int count)
        {
            this.backingBuffer = backingBuffer;
            this.offset = offset;
            this.count = count;
        }
        
        private void PreRead(int size)
        {
            if(!IsReadOnly)
                throw new InvalidOperationException("Packet is write-only, borrow another from the pool.");
            if(size > BytesRemaining)
                throw new ArgumentException("Not enough bytes remain to read from current position");
        }

        private void PreWrite(int size)
        {
            if(IsReadOnly)
                throw new InvalidOperationException("Packet is read-only, borrow another from the pool.");
            if(size > (count - (pos - offset)))
                throw new ArgumentException("Not enough bytes avaliable to write from current position");
            BytesWritten += size;
        }

        /// <summary>
        /// Resets the pos, marks are read only and sets the peer id from.
        /// </summary>
        /// <param name="peerId">Falcon Peer Id packet received from.</param>
        internal void ResetAndMakeReadOnly(int peerId)
        {
            pos = offset;
            IsReadOnly = true;
            PeerId = peerId;
        }

        internal void Init()
        {
            // NOTE: This should fully reset this Packet as is called when re-used from the pool.

            pos = offset;
            BytesWritten = 0;
            IsReadOnly = false;
            ElapsedMillisecondsSinceSent = 0;
            DatagramSeq = 0;
        }

        internal void CopyBytes(int index, byte[] destination, int dstOffset, int count)
        {
            if (index > BytesWritten)
                throw new ArgumentOutOfRangeException("index");
            if (index + count > BytesWritten)
                throw new InvalidOperationException("Not enough bytes remain to copy count from index");
            if (count == 0)
                return;

            Buffer.BlockCopy(backingBuffer, offset + index, destination, dstOffset, count);
        }

        internal byte[] ToBytes()
        { 
            byte[] bytes = new byte[BytesWritten];
            if(BytesWritten > 0)
                Buffer.BlockCopy(backingBuffer, offset, bytes, 0, BytesWritten);
            return bytes;
        }
        
        /// <summary>
        /// Copies all public members and underlying bytes written from 
        /// <paramref name="srcPacket"/> to <paramref name="dstPacket"/>.
        /// </summary>
        /// <param name="srcPacket">Packet to copy from</param>
        /// <param name="dstPacket">Packet to copy to</param>
        /// <param name="reset">If true resets <paramref name="dstPacket"/>'s current pos to 
        /// the beginning, and makes it read-only.</param>
        public static void Clone(Packet srcPacket, Packet dstPacket, bool reset)
        {
            if(dstPacket.count != srcPacket.count)
                throw new InvalidOperationException("packets are differnt sizes");

            Buffer.BlockCopy(srcPacket.backingBuffer, srcPacket.offset, dstPacket.backingBuffer, dstPacket.offset, srcPacket.BytesWritten);
            dstPacket.BytesWritten = srcPacket.BytesWritten;
            dstPacket.DatagramSeq = srcPacket.DatagramSeq;
            dstPacket.ElapsedMillisecondsSinceSent = srcPacket.ElapsedMillisecondsSinceSent;
            if (reset)
            {
                dstPacket.ResetAndMakeReadOnly(srcPacket.PeerId);
            }
            else
            {
                dstPacket.pos = dstPacket.offset +  (srcPacket.pos - srcPacket.offset);
                dstPacket.IsReadOnly = srcPacket.IsReadOnly;
                dstPacket.PeerId = srcPacket.PeerId;
            }
        }

        
        /// <summary>
        /// Reads next <see cref="Byte"/> in this Packet.
        /// </summary>
        /// <returns>Next <see cref="Byte"/> in this Packet.</returns>
        public byte ReadByte()
        {
            PreRead(sizeof(byte));
            byte rv = backingBuffer[pos];
            pos += sizeof(byte);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="Boolean"/> from the Packet.
        /// </summary>
        /// <returns>Next <see cref="Boolean"/> from this Packet.</returns>
        /// <remarks>Takes up one byte</remarks>
        public bool ReadBool()
        {
            PreRead(sizeof(bool));
            bool rv = backingBuffer[pos] > 0;
            pos += sizeof(bool);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="Int16"/> from this packet.
        /// </summary>
        /// <returns>Next <see cref="Int16"/> from this Packet.</returns>
        public short ReadInt16()
        {
            PreRead(sizeof(short));
            short rv = BitConverter.ToInt16(backingBuffer, pos);
            pos += sizeof(short);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="UInt16"/> from this packet.
        /// </summary>
        /// <returns>Next <see cref="UInt16"/> from this Packet.</returns>
        public ushort ReadUInt16()
        {
            PreRead(sizeof(ushort));
            ushort rv = BitConverter.ToUInt16(backingBuffer, pos);
            pos += sizeof(ushort);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="UInt32"/> from this Packet.
        /// </summary>
        /// <returns>Next <see cref="UInt32"/> from this Packet.</returns>
        public int ReadInt32()
        {
            PreRead(sizeof(int));
            int rv = BitConverter.ToInt32(backingBuffer, pos);
            pos += sizeof(int);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="UInt32"/> from this Packet.
        /// </summary>
        /// <returns>next <see cref="UInt32"/> from this Packet.</returns>
        public uint ReadUInt32()
        {
            PreRead(sizeof(uint));
            uint rv = BitConverter.ToUInt32(backingBuffer, pos);
            pos += sizeof(uint);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="Int64"/> from this Packet.
        /// </summary>
        /// <returns>next <see cref="Int64"/> from this Packet.</returns>
        public long ReadInt64()
        {
            PreRead(sizeof(long));
            long rv = BitConverter.ToInt64(backingBuffer, pos);
            pos += sizeof(long);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="UInt64"/> from this Packet.
        /// </summary>
        /// <returns>next <see cref="UInt64"/> from this Packet.</returns>
        public ulong ReadUInt64()
        {
            PreRead(sizeof(ulong));
            ulong rv = BitConverter.ToUInt64(backingBuffer, pos);
            pos += sizeof(ulong);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="Single"/> from this Packet.
        /// </summary>
        /// <returns>next <see cref="Single"/> from this Packet.</returns>
        public float ReadSingle()
        {
            PreRead(sizeof(float));
            float rv = BitConverter.ToSingle(backingBuffer, pos);
            pos += sizeof(float);
            return rv;
        }

        /// <summary>
        /// Reads next <see cref="Double"/> from this Packet.
        /// </summary>
        /// <returns>next <see cref="Double"/> from this Packet.</returns>
        public double ReadDouble()
        {
            PreRead(sizeof(double));
            double rv = BitConverter.ToDouble(backingBuffer, pos);
            pos += sizeof(double);
            return rv;
        }

        /// <summary>
        /// Reads next <paramref name="count"/> bytes into a new byte[] from this Packet.
        /// </summary>
        /// <param name="count">Number of bytes to read</param>
        /// <returns>Byte array with <paramref name="count"/> bytes from this Packet.</returns>
        public byte[] ReadBytes(int count)
        {
            PreRead(count);
            byte[] bytes = new byte[count];
            Buffer.BlockCopy(backingBuffer, pos, bytes, 0, count);
            pos += count;
            return bytes;
        }

        /// <summary>
        /// Reads next <paramref name="count"/> bytes intp <paramref name="destination"/> from <paramref name="dstOffset"/>.
        /// </summary>
        /// <param name="destination">Destination array to copy bytes into.</param>
        /// <param name="dstOffset">Index at which to start copying.</param>
        /// <param name="count">Number of bytes to copy.</param>
        public void ReadBytes(byte[] destination, int dstOffset, int count)
        {
            PreRead(count);
            Buffer.BlockCopy(backingBuffer, pos, destination, dstOffset, count);
            pos += count;
        }
        
        /// <summary>
        /// Reads text encoded in <paramref name="encoding"/> 
        /// <paramref name="lengthInBytes"/> bytes long from this Packet.
        /// </summary>
        /// <param name="encoding"><see cref="Encoding"/> text is in.</param>
        /// <param name="lengthInBytes">Number of bytes text is for.</param>
        /// <returns>Text as <see cref="String"/></returns>
        public string ReadString(Encoding encoding, int lengthInBytes)
        {
            string rv = encoding.GetString(backingBuffer, pos, lengthInBytes);
            pos += lengthInBytes;
            return rv;
        }

        /// <summary>
        /// Reads <see cref="UInt16"/> size then text encoded in <paramref name="encoding"/> 
        /// for the number bytes in size.
        /// </summary>
        /// <param name="encoding"><see cref="Encoding"/> text is in.</param>
        /// <returns>Text as <see cref="String"/></returns>
        public string ReadStringPrefixedWithSize(Encoding encoding)
        {
            int length = ReadUInt16();
            if(length == 0)
                return null;
            return ReadString(encoding, length);
        }

        /// <summary>
        /// Reads <see cref="IPEndPoint"/> from this Packet.
        /// </summary>
        /// <returns>An <see cref="IPEndPoint"/></returns>
        public IPEndPoint ReadIPEndPoint()
        {
            return new IPEndPoint(ReadInt64(), ReadUInt16());
        }

        /// <summary>
        /// Reads a 16 byte <see cref="Guid"/> from this Packet.
        /// </summary>
        /// <returns><see cref="Guid"/></returns>
        public Guid ReadGuid()
        {
            return new Guid(ReadBytes(16));
        }

        /// <summary>
        /// Writes a <see cref="Byte"/> to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public void WriteByte(byte value)
        {
            PreWrite(sizeof(byte));
            backingBuffer[pos] = value;
            pos += sizeof(byte);
        }
        
        /// <summary>
        /// Writes <see cref="Boolean"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public void WriteBool(bool value)
        {
            PreWrite(sizeof(bool));
            backingBuffer[pos] = value ? (byte)1 : (byte)0;
            pos += sizeof(bool);
        }

        /// <summary>
        /// Writes <see cref="Int16"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteInt16(short value)
        {
            PreWrite(sizeof(short));
            fixed (byte* ptr = backingBuffer)
            {
                *(short*)(ptr + pos) = value;
            }
            pos += sizeof(short);
        }

        /// <summary>
        /// Writes <see cref="UInt16"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteUInt16(ushort value)
        {
            PreWrite(sizeof(ushort));
            fixed (byte* ptr = backingBuffer)
            {
                *(ushort*)(ptr + pos) = value;
            }
            pos += sizeof(ushort);
        }

        /// <summary>
        /// Writes <see cref="UInt16"/> value to this Packet at the index without modifying the 
        /// current position to perform future read and write from.
        /// </summary>
        /// <param name="value">value to write</param>
        /// <param name="index">index in underlying buffer for this packet to start write at</param>
        public unsafe void WriteUInt16At(ushort value, int index)
        {
            if (index < 0 || (index + sizeof(ushort) > count))
                throw new ArgumentOutOfRangeException("index");

            fixed (byte* ptr = backingBuffer)
            {
                *(ushort*)(ptr + (offset + index)) = value;
            }
        }

        /// <summary>
        /// Writes <see cref="Int32"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteInt32(int value)
        {
            PreWrite(sizeof(int));
            fixed (byte* ptr = backingBuffer)
            {
                *(int*)(ptr + pos) = value;
            }
            pos += sizeof(int);
        }

        /// <summary>
        /// Writes <see cref="UInt32"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteUInt32(uint value)
        {
            PreWrite(sizeof(uint));
            fixed (byte* ptr = backingBuffer)
            {
                *(uint*)(ptr + pos) = value;
            }
            pos += sizeof(uint);
        }

        /// <summary>
        /// Writes <see cref="Int64"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteInt64(long value)
        {
            PreWrite(sizeof(long));
            fixed (byte* ptr = backingBuffer)
            {
                *(long*)(ptr + pos) = value;
            }
            pos += sizeof(long);
        }

        /// <summary>
        /// Writes <see cref="UInt64"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteUInt64(ulong value)
        {
            PreWrite(sizeof(ulong));
            fixed (byte* ptr = backingBuffer)
            {
                *(ulong*)(ptr + pos) = value;
            }
            pos += sizeof(ulong);
        }

        /// <summary>
        /// Writes <see cref="Single"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteSingle(float value)
        {
            PreWrite(sizeof(float));
            fixed (byte* ptr = backingBuffer)
            {
                *(float*)(ptr + pos) = value;
            }
            pos += sizeof(float);
        }

        /// <summary>
        /// Writes <see cref="Double"/> value to this Packet.
        /// </summary>
        /// <param name="value">value to write</param>
        public unsafe void WriteDouble(double value)
        {
            PreWrite(sizeof(double));
            fixed (byte* ptr = backingBuffer)
            {
                *(double*)(ptr + pos) = value;
            }
            pos += sizeof(double);
        }

        /// <summary>
        /// Writes <paramref name="count"/> bytes from <paramref name="srcOffset"/> in <paramref name="bytes"/>
        /// to this Packet.
        /// </summary>
        /// <param name="bytes">Byte array containing bytes to write.</param>
        /// <param name="srcOffset">Index in <paramref name="bytes"/> to start writing from.</param>
        /// <param name="count">Number of bytes to write.</param>
        public void WriteBytes(byte[] bytes, int srcOffset, int count)
        {
            PreWrite(count);
            Buffer.BlockCopy(bytes, srcOffset, backingBuffer, pos, count);
            pos += count;
        }

        /// <summary>
        /// Writes <paramref name="count"/> bytes from <paramref name="srcOffset"/> in <paramref name="srcPacket"/>
        /// to this Packet.
        /// </summary>
        /// <param name="srcPacket"><see cref="Packet"/> to copy the bytes from.</param>
        /// <param name="srcOffset">Index in <paramref name="srcOffset"/> index to start copying bytes from.</param>
        /// <param name="count">Number of bytes to write.</param>
        public void WriteBytes(Packet srcPacket, int srcOffset, int count)
        {
            PreWrite(count);
            Buffer.BlockCopy(srcPacket.backingBuffer, srcPacket.offset + srcOffset, backingBuffer, pos, count);
            pos += count;
        }

        /// <summary>
        /// Writes all bytes in byte array to this Packet.
        /// </summary>
        /// <param name="bytes">Byte array to write.</param>
        public void WriteBytes(byte[] bytes)
        {
            if (bytes == null)
                return;
            WriteBytes(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Writes string <paramref name="value"/> to this Packet encoded using <paramref name="encoding"/>
        /// </summary>
        /// <param name="value"><see cref="String"/> to write.</param>
        /// <param name="encoding"><see cref="Encoding"/> to encode <paramref name="value"/> as.</param>
        public void WriteString(string value, Encoding encoding)
        {
            byte[] bytes = encoding.GetBytes(value);
            WriteBytes(bytes);
        }

        /// <summary>
        /// Writes size of string <paramref name="value"/> in bytes encoded using 
        /// <paramref name="encoding"/> as a <see cref="UInt16"/> then writes string <paramref name="value"/>.
        /// </summary>
        /// <param name="value"><see cref="String"/> to write.</param>
        /// <param name="encoding"><see cref="Encoding"/> to encode <paramref name="value"/> as.</param>
        public void WriteStringPrefixSize(string value, Encoding encoding)
        {
            if (String.IsNullOrEmpty(value))
            {
                WriteUInt16(0);
            }
            else
            {
                byte[] bytes = encoding.GetBytes(value);
                WriteUInt16((ushort)bytes.Length);
                WriteBytes(bytes);
            }
        }

        /// <summary>
        /// Writes a <see cref="IPEndPoint"/> to this Packet.
        /// </summary>
        /// <param name="ipEndPoint"><see cref="IPEndPoint"/> to write.</param>
        public void WriteIPEndPoint(IPEndPoint ipEndPoint)
        {
#pragma warning disable 0618
            WriteInt64(ipEndPoint.Address.Address);
#pragma warning restore 0618
            WriteUInt16((ushort)ipEndPoint.Port);
        }

        /// <summary>
        /// Writes <see cref="Guid"/> to this Packet as 16 bytes.
        /// </summary>
        /// <param name="guid"><see cref="Guid"/> value to write.</param>
        public void WriteGuid(Guid guid)
        {
            WriteBytes(guid.ToByteArray());
        }
    }
}
