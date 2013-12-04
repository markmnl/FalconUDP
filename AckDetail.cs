namespace FalconUDP
{
    internal class AckDetail
    {
        internal ushort Seq;
        internal SendOptions Channel;
        internal PacketType Type;
        internal float EllapsedMillisecondsSincetEnqueud;

        internal void Init(ushort seq, SendOptions channel, PacketType type)
        {
            this.Seq = seq;
            this.Channel = channel;
            this.Type = type;
            this.EllapsedMillisecondsSincetEnqueud = 0;
        }
    }
}
