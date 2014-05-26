
namespace FalconUDP
{
    /// <summary>
    /// Class contining the number of object FalconPeer will create on construction to mitigate 
    /// later allocations.
    /// </summary>
    public class FalconPoolSizes
    {
        /// <summary>
        /// Number of <see cref="Packet"/>s to pool. The optimal number is just over the maximum 
        /// number in use at any one time - which will be the number received between calls to
        /// <see cref="FalconPeer.ProcessReceivedPackets()"/>
        /// </summary>
        public int InitalNumPacketsToPool = 32;
        /// <summary>
        /// Number of Pings to pool - an internal object used whenever a Ping is sent. The optimal 
        /// number is the maximum in use at any one time - which will be the number of outstanding 
        /// (i.e. haven't received Ping reply or timed-out) pings.
        /// </summary>
        public int InitalNumPingsToPool = 10;
        /// <summary>
        /// Number of DiscoverySignalTask's to pool - an internal object used to track a discovery 
        /// process initated from calling of the Discover or Punch through methods on FalconPeer. 
        /// The optimal number is the maximum number of discovery process you will have outstanding
        /// at any one time.
        /// </summary>
        public int InitalNumEmitDiscoverySignalTaskToPool = 5;
        /// <summary>
        /// Number of internal buffers to pool used to store pakcets enqued but not yet sent. The
        /// optimal number is the maximum number of sends, minus the number that can be "packed-in"
        /// existing datagrams, at any one time.
        /// </summary>
        public int InitalNumSendDatagramsToPool = 20;
        /// <summary>
        /// Number of interal data structurs use to save ACKknowledgement details to be sent in 
        /// response to receiving a Reliable message. The optimal number is the maximum number of 
        /// ACKs oustanding resulting from incoming reliable packets between calls to 
        /// <see cref="FalconPeer.ProcessReceivedPackets()"/> then <see cref="FalconPeer.SendEnquedPackets()"/>
        /// </summary>
        public int InitalNumAcksToPool = 20;

        /// <summary>
        /// Default pool sizes.
        /// </summary>
        public static readonly FalconPoolSizes Default = new FalconPoolSizes
            {
                InitalNumPacketsToPool = 32,
                InitalNumPingsToPool = 10,
                InitalNumEmitDiscoverySignalTaskToPool = 5,
                InitalNumSendDatagramsToPool = 20,
                InitalNumAcksToPool = 20,
            };
    }
}
