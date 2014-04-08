
namespace FalconUDP
{
    internal class Datagram : FalconBuffer
    {
        internal SendOptions SendOptions;
        internal bool IsResend;
        internal ushort Seq; // helper field for seq already in the buffer 
    }
}
