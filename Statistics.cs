using System.Threading;

namespace FalconUDP
{
    /// <summary>
    /// Provides total number of bytes sent and recieved in the last secound.
    /// </summary>
    /// <remarks>Statistics are only collected when <see cref="FalconPeer.StartCollectingStatistics()"/> is called.</remarks>
    public class Statistics
    {
        private float ellapsedSecondsSinceSecondStart;
        private int bytesSentInLastSecond;
        private int bytesReceivedInLastSecond;
        private int bytesSentPerSecond;
        private int bytesReceivedPerSecond;

        /// <summary>
        /// Total number of bytes sent in the last secound (including IP, UDP and FalconUDP header bytes).
        /// </summary>
        public int BytesSentPerSecound
        {
            get { return bytesSentPerSecond; }
        }

        /// <summary>
        /// Total number of bytes received in the last secound (including IP, UDP and FalconUDP header bytes).
        /// </summary>
        public int BytesReceivedPerSecound
        {
            get { return bytesReceivedPerSecond; }
        }

        internal Statistics()
        {
        }

        internal void Reset()
        {
            ellapsedSecondsSinceSecondStart = 0;
            bytesSentInLastSecond = 0;
            bytesReceivedInLastSecond = 0;
            bytesSentPerSecond = 0;
            bytesReceivedPerSecond = 0;
        }

        internal void AddBytesSent(int falconPacketSize)
        {
            bytesSentInLastSecond += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
        }

        internal void AddBytesReceived(int falconPacketSize)
        {
            bytesReceivedInLastSecond += (falconPacketSize + Const.CARRIER_PROTOCOL_HEADER_SIZE);
        }

        internal void Update(float dt)
        {
            ellapsedSecondsSinceSecondStart += dt;
            if (ellapsedSecondsSinceSecondStart >= 1.0f) // NOTE: error margin of one update
            {
                bytesReceivedPerSecond = bytesReceivedInLastSecond;
                bytesSentPerSecond = bytesSentInLastSecond;
                bytesReceivedInLastSecond = 0;
                bytesSentInLastSecond = 0;
                ellapsedSecondsSinceSecondStart = 0;
            }
        }
    }
}
