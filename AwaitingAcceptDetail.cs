using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
        internal IPEndPoint EndPoint;
        internal FalconOperationCallback Callback;
        internal int Ticks;
        internal int RetryCount;
        internal byte[] Pass;

        internal AwaitingAcceptDetail(IPEndPoint ip, FalconOperationCallback callback, string pass)
        {
            EndPoint = ip;
            Callback = callback;
            if(pass != null)
                Pass = Settings.TextEncoding.GetBytes(pass);
            Ticks = 0;
            RetryCount = 0;
        }
    }
}
