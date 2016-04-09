using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace FalconUDP
{
    public static class UWPNetExtensions
    {
        public static long GetAddressAsLong(this IPAddress ip)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException(ip.AddressFamily.ToString());

            var value = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            return (long)value;
        }
    }

}
