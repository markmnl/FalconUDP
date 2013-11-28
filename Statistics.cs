using System.Threading;

namespace FalconUDP
{
    /// <summary>
    /// Provides total number of bytes sent and recieved in the last secound.
    /// </summary>
    /// <remarks>Statistics are only collected when <see cref="FalconPeer.StartCollectingStatistics()"/> is called.</remarks>
    public class Statistics
    {
        private long ellapsedMillisecondsSinceSecondStart;
        private object updatingLock;
        private int bytesSentInLastSecound;
        private int bytesReceivedInLastSecound;
        private int bytesSentPerSecound;
        private int bytesRecievedPerSecound;
        
        /// <summary>
        /// Total number of bytes sent in the last secound (including IP, UDP and FalconUDP header bytes).
        /// </summary>
        public int BytesSentPerSecound 
        {
            get { return bytesSentPerSecound; } 
        }

        /// <summary>
        /// Total number of bytes received in the last secound (including IP, UDP and FalconUDP header bytes).
        /// </summary>
        public int BytesReceivedPerSecound 
        {
            get { return bytesRecievedPerSecound; }
        }

        internal Statistics()
        {
            updatingLock = new object();
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref ellapsedMillisecondsSinceSecondStart, 0);
            Interlocked.Exchange(ref bytesSentInLastSecound, 0);
            Interlocked.Exchange(ref bytesReceivedInLastSecound, 0);
            Interlocked.Exchange(ref bytesSentPerSecound, 0);
            Interlocked.Exchange(ref bytesRecievedPerSecound, 0);
        }

        internal void AddBytesSent(int falconPacketSize)
        {
            Interlocked.Add(ref bytesSentInLastSecound, (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE));
        }

        internal void AddBytesReceived(int falconPacketSize)
        {
            Interlocked.Add(ref bytesReceivedInLastSecound, (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE));
        }

        internal void Tick(long totalElapsedMilliseconds)
        {
            if (totalElapsedMilliseconds - ellapsedMillisecondsSinceSecondStart >= 1000) // NOTE: error margin of one tick
            {
                Interlocked.Exchange(ref bytesRecievedPerSecound, bytesReceivedInLastSecound);
                Interlocked.Exchange(ref bytesReceivedInLastSecound, 0);
                Interlocked.Exchange(ref bytesSentPerSecound, bytesSentInLastSecound);
                Interlocked.Exchange(ref bytesSentInLastSecound, 0);
                Interlocked.Exchange(ref ellapsedMillisecondsSinceSecondStart, totalElapsedMilliseconds);
            }
        }
    }
}
