using System.Net;

namespace FalconUDP
{
    internal class PingDetail
    {
        internal int? PeerIdPingSentTo;             //}
        internal IPEndPoint IPEndPointPingSentTo;   //} only one set
        internal long EllapsedMillisecondsAtSend;
        internal bool IsForConnectedPeer { get { return PeerIdPingSentTo.HasValue; } }

        private void Init(long ellapsedMillisecondsAtSend)        
        { 
            this.EllapsedMillisecondsAtSend = ellapsedMillisecondsAtSend;
        }

        internal void Init(IPEndPoint ipEndPoint, long ellapsedMillisecondsAtSend)
        {
            IPEndPointPingSentTo = ipEndPoint;
            PeerIdPingSentTo = null;
            Init(ellapsedMillisecondsAtSend);
        }

        internal void Init(int? peerId, long ellapsedMillisecondsAtSend)
        {
            PeerIdPingSentTo = peerId;
            IPEndPointPingSentTo = null;
            Init(ellapsedMillisecondsAtSend);
        }
    }
}
