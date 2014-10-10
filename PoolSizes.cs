
namespace FalconUDP
{
    /// <summary>
    /// Class contining the number of objects FalconPeer will create on construction to mitigate 
    /// later allocations and 
    /// </summary>
    public class FalconPoolSizes
    {
        /// <summary>
        /// Number of <see cref="Packet"/>s to pool. The optimal number is the maximum number in 
        /// use at any one time - which will be the number received between calls to <see cref="FalconPeer.Update()"/>.
        /// </summary>
        public int InitalNumPacketsToPool = 64;
        /// <summary>
        /// Number of Pings to pool - an internal object used whenever a Ping is sent. The optimal 
        /// number is the maximum in use at any one time - which will be the number of outstanding 
        /// (i.e. haven't received Pong reply or timed-out) pings.
        /// </summary>
        public int InitalNumPingsToPool = 10;
        /// <summary>
        /// Number of DiscoverySignalTask's to pool - an internal object used to track a discovery 
        /// process initated from calling one of the Discover or Punch through methods on 
        /// FalconPeer. The optimal number is the maximum number of discovery process you will 
        /// have outstanding at any one time.
        /// </summary>
        public int InitalNumEmitDiscoverySignalTaskToPool = 5;
        /// <summary>
        /// Number of internal buffers to pool used to store packets enqued but not yet sent per 
        /// peer. The optimal number is the maximum number of unsent datagrams at any time which 
        /// the number of packets enqueued to send minus the number that can be "packed-in" 
        /// existing datagrams.
        /// </summary>
        public int InitalNumSendDatagramsToPoolPerPeer = 4;
        /// <summary>
        /// Number of interal data structurs use to save ACKknowledgement details to be sent in 
        /// response to receiving a Reliable message. The optimal number is the maximum number of 
        /// ACKs oustanding resulting from incoming reliable packets between calls to 
        /// <see cref="FalconPeer.Update()"/> and <see cref="FalconPeer.SendEnquedPackets()"/>
        /// </summary>
        public int InitalNumAcksToPool = 20;

        /// <summary>
        /// Default pool sizes.
        /// </summary>
        public static readonly FalconPoolSizes Default = new FalconPoolSizes
            {
                InitalNumPacketsToPool = 64,
                InitalNumPingsToPool = 10,
                InitalNumEmitDiscoverySignalTaskToPool = 5,
                InitalNumSendDatagramsToPoolPerPeer = 40,
                InitalNumAcksToPool = 20,
            };
    }
}
