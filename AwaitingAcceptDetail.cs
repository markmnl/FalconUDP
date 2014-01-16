using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
        internal IPEndPoint EndPoint;
        internal FalconOperationCallback<int> Callback;
        internal float EllapsedMillisecondsSinceStart;
        internal int RetryCount;
        internal byte[] Pass;

        internal AwaitingAcceptDetail(IPEndPoint ip, FalconOperationCallback<int> callback, string pass)
        {
            EndPoint = ip;
            Callback = callback;
            if(pass != null)
                Pass = Settings.TextEncoding.GetBytes(pass);
            EllapsedMillisecondsSinceStart = 0.0f;
            RetryCount = 0;
        }
    }
}
