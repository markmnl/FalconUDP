# FalconUDP

FalconUDP is an application level protocol for sending and receiving small messages (one to one thousand or more bytes) frequently (once a minute to hundreds of times a second) to and from one or many connected peers. It has been implemented on top of the Internet User Datagram protocol (UDP).

This is the official .NET implementation, implemented as a .NET 4.0 Class Library: FalconUDP.dll. Throughout this repository "FalconUDP" is used interchangeably to refer to the protocol or this .NET implementation.

FalconUDP was developed Gnomic Studios for their multiplayer game Square Heroes: http://www.squareheroes.com/, check it out to see FalconUDP perform

For the latest source please use GitHub: http://github.com/markmnl/FalconUDP. Only "stable" releases will be pushed to CodePlex periodically.

## Features
* **Authentication and connection orientated** – FalconUDP only communicates with previously authenticated peers.
* **Reliable and in-order messages** – Messages can be sent in-order, reliably, both in-order and reliable, or without any reliable and in-order checking and controls. Duplicates are always detected and dropped.
* **Minimal overhead** – FalconUDP adds minimal data to the user-application's data to be sent and received "on the wire" and includes support for "packing" additional user-application packets into existing outgoing messages.
* **Latency estimation** – FalconUDP measures round-trip-times from sending reliable messages till receiving the corresponding ACKnowlodgment, providing one way latency estimation to each connected peer.
* **Any Topology** – User-applications can use their own logical network topology such as Server-Client or Peer-to-Peer, since a "Falcon peer" can have one or more connected peers.
* **Discovery** – FalconUDP peers can "discover" other FalconUDP peers on the same subnet (if a FalconUDP peer is set to accept discovery requests and optionally the request has a valid token).
* **NAT Traversal** – Two FalconUDP peers behind NAT(s) can establish a connection with each other after connecting to third publically accessible server to negotiate the connection.
* **Minimal garbage** – FalconUDP makes extensive use of memory management techniques to avoid creating garbage such as: pooling objects, segmenting buffers from large ones (to prevent heap memory fragmentation) and copying data directly into and out of buffers.
* **Statistics** – Total bytes per second sent and received to/from all remote peers.
* **Simulate** – latency, jitter and packet loss options.

## Limitations
* Currently only IPv4 supported.*
* No Congestion Control. FalconUDP provides no in-built congestion control.†
* No Encryption. FalconUDP has no in-built encryption.†

*IPv6 planned
†User-applications can use their own implementation before sending and after receiving data using the library's API.

## Protocol

```   
    [0]     Packet detail byte (see below).     

    [1]     }
    [2]     } Sequence number as unsigned 16 bit integer
  
    [3]     } 
    [4]     } Payload Size as unsigned 16 bit integer
       
    [n]     }
    ...     } Payload (if any, i.e. size > 0)
    [m]     } 
       
[ optional additional packet - if any bytes remain in datagram
 
    [m+1]   Packet detail byte (see below).
       
    [m+2]   }
    [m+3]   } Payload Size as unsigned 16 bit integer
       
    [m+4]   } 
    ...     } Payload (if any, i.e. size > 0)
    [o]     }
  
    ... possible multiple additional packets 
]
```
  
### Packet Detail byte in bits

```
    [0]     
    [1]     }
    [2]     } Send Options  
    [3]     } 
       
    [4]     }   
    [5]     }   
    [6]     } Packet Type
    [7]     }  
``` 

### ACKs
   
ACKs are special, they are the same size as a FalonUDP header and have seq, Send Options and 
Packet Type stored at the same place, however the values have different meanings. A FalconUDP
packet can start with an ACK or have them after any application packet within (this is so they 
can "piggy-back" on existing outgoing datagrams without triggering a new one).

What each FalconUDP header value means in an ACK:

```   
PacketType   = Will always be ACK
Sequence No. = Sequence ACK is for
SendOptions  = Channel ACK is for (however can be sent on any channel)
Payload size = Undefined
```    

### NOTE:

 * All numbers in FalconUDP headers are unsigned integers stored in Little-Endian byte order.

* Additional packets are only appended in a Falcon packet if they fit wholley within 
without packet exceeding MAX_PACKET_SIZE.

* Additional packets have to have the same SendOptions as byte [1] in the Falcon header.

## TODO
- include packet size and version no. in join request so incompatiable falcon peers cannot join eachother
- peer who is not keepalive master does not get latency updates - should KeepAlive master tell them latency they have for peer?
- consider determining addresses to broadcast to at time of discovery not at startup (could have changed, though it does take time to calc this)
- in addition to above problem more generally local addresses cache should be refreshed when network changes have occured
- quick disconnect when network disconnected

### Universal
- best way to get local addresses?
- UPnP


### One day
- IPv6 and other underlying protocols support
- Optimistic Reliable congestion controlled delivery
