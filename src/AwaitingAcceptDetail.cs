using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
        internal IPEndPoint EndPoint;
        internal FalconOperationCallback<int> Callback;
        internal float EllapsedSecondsSinceStart;
        internal int RetryCount;
        internal byte[] JoinData;
        internal Packet UserDataPacket;

        internal AwaitingAcceptDetail(IPEndPoint ip, FalconOperationCallback<int> callback, byte[] joinData)
        {
            this.EndPoint = ip;
            this.Callback = callback;
            this.JoinData = joinData;
            this.EllapsedSecondsSinceStart = 0.0f;
            this.RetryCount = 0;
        }
    }
}
