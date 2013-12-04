using System.Net;

namespace FalconUDP
{
    internal class PingDetail
    {
        internal int? PeerIdPingSentTo;
        internal IPEndPoint IPEndPointPingSentTo;
        internal long EllapsedMillisecondsAtSend;
        internal bool IsForConnectedPeer { get { return PeerIdPingSentTo.HasValue; } }

        private void Init()        
        { 
            this.EllapsedMillisecondsAtSend= 0;
        }

        internal void Init(IPEndPoint ipEndPoint)
        {
            IPEndPointPingSentTo = ipEndPoint;
            PeerIdPingSentTo = null;
            Init();
        }

        internal void Init(int? peerId)
        {
            PeerIdPingSentTo = peerId;
            IPEndPointPingSentTo = null;
            Init();
        }
    }
}
