using System.Threading;

namespace FalconUDP
{
    /// <summary>
    /// Provides total number of bytes sent and recieved in the last secound.
    /// </summary>
    /// <remarks>Statistics are only collected when <see cref="FalconPeer.StartCollectingStatistics()"/> is called.</remarks>
    public class Statistics
    {
        private float ellapsedMillisecondsSinceSecondStart;
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
        }

        internal void Reset()
        {
            ellapsedMillisecondsSinceSecondStart = 0;
            bytesSentInLastSecound = 0;
            bytesReceivedInLastSecound = 0;
            bytesSentPerSecound = 0;
            bytesRecievedPerSecound = 0;
        }

        internal void AddBytesSent(int falconPacketSize)
        {
            bytesSentInLastSecound += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
        }

        internal void AddBytesReceived(int falconPacketSize)
        {
            bytesReceivedInLastSecound += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
        }

        internal void Update(float dt)
        {
            ellapsedMillisecondsSinceSecondStart += dt;
            if (ellapsedMillisecondsSinceSecondStart >= 1000) // NOTE: error margin of one update
            {
                bytesRecievedPerSecound = bytesReceivedInLastSecound;
                Reset();
            }
        }
    }
}
