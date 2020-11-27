using System.Net;

namespace FalconUDP
{
    internal static class FalconExtensions
    {
        internal static bool FastEquals(this IPEndPoint ip1, IPEndPoint ip2)
        {
#if NETFX_CORE || WINDOWS_UWP
            return ip1.Equals(ip2);
#else
#pragma warning disable 0618
            return ip1.Address.Address.Equals(ip2.Address.Address) && ip1.Port == ip2.Port;
#pragma warning restore 0618
#endif
            // TODO if IPv6
        }
    }
}
