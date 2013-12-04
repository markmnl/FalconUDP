using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
        internal IPEndPoint EndPoint;
        internal FalconOperationCallback Callback;
        internal float EllapsedMillisecondsSinceStart;
        internal int RetryCount;
        internal byte[] Pass;

        internal AwaitingAcceptDetail(IPEndPoint ip, FalconOperationCallback callback, string pass)
        {
            EndPoint = ip;
            Callback = callback;
            if(pass != null)
                Pass = Settings.TextEncoding.GetBytes(pass);
            EllapsedMillisecondsSinceStart = 0;
            RetryCount = 0;
        }
    }
}
