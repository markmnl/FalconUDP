namespace FalconUDP
{
    internal class AckDetail
    {
        internal ushort Seq;
        internal SendOptions Channel;
        internal PacketType Type;
        internal long EllapsedTimeAtEnqueud;

        internal void Init(ushort seq, SendOptions channel, PacketType type, long ellapsedTimeAtEnqueued)
        {
            this.Seq = seq;
            this.Channel = channel;
            this.Type = type;
            this.EllapsedTimeAtEnqueud = ellapsedTimeAtEnqueued;
        }
    }
}
