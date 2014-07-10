using System.Net;

namespace FalconUDP
{
    public static class FalconExtensions
    {
        public static bool FastEquals(this IPEndPoint ip1, IPEndPoint ip2)
        {
#pragma warning disable 0618
            return ip1.Address.Address.Equals(ip2.Address.Address) && ip1.Port == ip2.Port;
#pragma warning restore 0618
            // TODO if IPv6
        }
    }
}
