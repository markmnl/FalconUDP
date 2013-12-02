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
            get { lock(updatingLock) return bytesSentPerSecound; } 
        }

        /// <summary>
        /// Total number of bytes received in the last secound (including IP, UDP and FalconUDP header bytes).
        /// </summary>
        public int BytesReceivedPerSecound 
        {
            get { lock (updatingLock) return bytesRecievedPerSecound; }
        }

        internal Statistics()
        {
            updatingLock = new object();
        }

        internal void Reset()
        {
            lock (updatingLock)
            {
                ellapsedMillisecondsSinceSecondStart = 0;
                bytesSentInLastSecound = 0;
                bytesReceivedInLastSecound = 0;
                bytesSentPerSecound = 0;
                bytesRecievedPerSecound = 0;
            }
        }

        internal void AddBytesSent(int falconPacketSize)
        {
            lock (updatingLock)
            {
                bytesSentInLastSecound += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
            }
        }

        internal void AddBytesReceived(int falconPacketSize)
        {
            lock (updatingLock)
            {
                bytesReceivedInLastSecound += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
            }
        }

        internal void Tick(long totalElapsedMilliseconds)
        {
            if (totalElapsedMilliseconds - ellapsedMillisecondsSinceSecondStart >= 1000) // NOTE: error margin of one tick
            {
                lock (updatingLock)
                {
                    bytesRecievedPerSecound = bytesReceivedInLastSecound;
                    bytesReceivedInLastSecound = 0;
                    bytesSentPerSecound = bytesSentInLastSecound;
                    bytesSentInLastSecound = 0;
                    ellapsedMillisecondsSinceSecondStart = totalElapsedMilliseconds;
                }
            }
        }
    }
}
