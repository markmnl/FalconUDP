namespace FalconUDP
{
    internal class AckDetail
    {
        internal ushort Seq;
        internal SendOptions Channel;
        internal float EllapsedSecondsSinceEnqueued;

        internal void Init(ushort seq, SendOptions channel)
        {
            this.Seq = seq;
            this.Channel = channel;
            this.EllapsedSecondsSinceEnqueued = 0.0f;
        }
    }
}
