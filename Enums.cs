using System;

namespace FalconUDP
{
    /// <summary>
    /// Severity of a log entry.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>All</summary>
        All,        // must be first in list
        /// <summary>Debug</summary>
        Debug,
        /// <summary>Informational</summary>
        Info,
        /// <summary>Warning</summary>
        Warning,
        /// <summary>Error</summary>
        Error,
        /// <summary>Fatal</summary>
        Fatal,
        /// <summary>No logging</summary>
        NoLogging   // must be last in list
    }

    /// <summary>
    /// Sequentiality and reliability control options for the delivery of a packet.
    /// </summary>
    /// <remarks>
    /// Regardless of the SendOption value used packets are never proccessed more than once (in 
    /// case duplicated), and will be dropped if they our-of-order from the last packet the remote
    /// peer received from this peer with the same SendOptions by more than 
    /// Settings.OutOfOrderTolerance.
    /// </remarks>
    [Flags]
    public enum SendOptions : byte
    {
        /// <summary>
        /// Guarantees packet arrival. 
        /// </summary>
        /// <remarks>If the remote peer does not ACKnowledge receipt of packet sent with this flag
        /// , re-send the packet till it does a maximum of Settings.ACKRetryAttempts times. If 
        /// after Settings.ACKRetryAttempts re-sends no ACKnowldgement is recieved the remote peer 
        /// is assumed to have disconnected and is dropped.
        /// </remarks>
        Reliable = 16, // 0001 0000
        /// <summary>
        /// Guarantees packet when received by remote peer is proccessed in-order in relation to 
        /// packets it has already processed using this, and only this, SendOption.
        /// </summary>
        /// <remarks>If remote peer has already processed a later packet this packet will be 
        /// dropped. However even if received after a later packets, if the remote peer has not 
        /// processed later packets yet: packets will be ordered correctly for when peer does read 
        /// them.
        /// </remarks>
        InOrder = 32, // 0010 0000
        /// <summary>
        /// Guarantee arrival (<see cref="Reliable"/>) AND is processed in-order in relation to 
        /// other packets sent using this, and only this, SendOption.
        /// </summary>
        /// <remarks>Using this option guarantees all packets are received and proccessed in-order. 
        /// However this involves more overhead: bandwidth because of the ACKs and memory as peers 
        /// have to hold on to sent packets until they are ACKnowledged, in case they are not and 
        /// need re-sending.</remarks>
        ReliableInOrder = 48, // 0011 0000
        /// <summary>No reliability or sequentiality guarantees, packet is sent and forgotten about.</summary>
        /// <remarks>As with all packets: duplicates and way out-of-order packets will not be 
        /// proccessed by a FalconUDP recepiant, see <see cref="SendOptions"/>.</remarks>
        None = 64, // 0100 0000
    }

    // packet type (last 4 bits of packet info byte in header), max 15 values
    internal enum PacketType : byte
    {
        ACK,
        AntiACK,
        JoinRequest,
        DropPeer,
        AcceptJoin,
        Ping,
        Pong,
        Application,
        DiscoverRequest,
        DiscoverReply,
        Bye,
        KeepAlive
    }
}
