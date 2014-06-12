using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FalconUDP
{
    public class PacketWriter
    {
        private Packet currentPacket;
        private FalconPeer falconPeer;

        internal Packet CurrentPacket
        {
            get { return currentPacket; }
        }
        
        internal PacketWriter(FalconPeer falconPeer)
        {
        }

        internal void AssignPacket(Packet packet)
        {
            // Return current packet to pool it came from.
            if(this.currentPacket != null)
            {
                falconPeer.ReturnPacketToPool(this.currentPacket);
            }
            this.currentPacket = packet;
        }

        public void WriteByte(byte value)
        {
            currentPacket.WriteByte(value);
        }
    }
}
