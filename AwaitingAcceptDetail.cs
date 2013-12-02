using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
        internal IPEndPoint EndPoint;
        internal FalconOperationCallback Callback;
        internal long EllapsedMillisecondsAtStart;
        internal int RetryCount;
        internal byte[] Pass;

        internal AwaitingAcceptDetail(IPEndPoint ip, FalconOperationCallback callback, string pass, long totalEllapsedMillisecondsNow)
        {
            EndPoint = ip;
            Callback = callback;
            if(pass != null)
                Pass = Settings.TextEncoding.GetBytes(pass);
            EllapsedMillisecondsAtStart = totalEllapsedMillisecondsNow;
            RetryCount = 0;
        }
    }
}
