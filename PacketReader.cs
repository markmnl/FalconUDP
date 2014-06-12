using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FalconUDP
{
    public class PacketReader
    {
        private Packet currentPacket;
        private FalconPeer falconPeer;

        internal Packet CurrentPacket
        {
            get { return currentPacket; }
        }

        public int PeerId
        {
            get { return currentPacket.PeerId; }
        }

        internal PacketReader(FalconPeer peer)
        {
            this.falconPeer = peer;
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

        public byte ReadByte()
        {
            return currentPacket.ReadByte();
        }
    }
}
