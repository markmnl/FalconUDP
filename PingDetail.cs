using System.Net;

namespace FalconUDP
{
    internal class PingDetail
    {
        internal int? PeerIdPingSentTo;             //}
        internal IPEndPoint IPEndPointPingSentTo;   //} only one set
        internal float EllapsedSeconds;
        internal bool IsForConnectedPeer { get { return PeerIdPingSentTo.HasValue; } }

        private void Init()        
        { 
            this.EllapsedSeconds = 0.0f;
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
