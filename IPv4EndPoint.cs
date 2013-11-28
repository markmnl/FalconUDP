#if NETFX_CORE
using System;
using Windows.Networking;

namespace FalconUDP
{
    /// <summary>
    /// Replacement for IPEndPoint which is lacking in NETFX_CORE.
    /// </summary>
    public class IPv4EndPoint : IEquatable<IPv4EndPoint>
    {
        private HostName hostName;
        private string portAsString;
        private uint ip;
        private ushort port;
        private int hash;
        private string asString;

        public HostName Address { get { return hostName; } }    //} NETFX_CORE expects these types
        public string Port { get { return portAsString; } }     //} 

        public IPv4EndPoint(string addr, string port)
        {
            // Currently we only accept the real deal - raw IPv4 address and port as numbers,
            // I'm not going to waste flipping time trying to connect to DNS and resolve the
            // IP. But when we do get around to it: http://msdn.microsoft.com/en-us/library/windows/apps/hh701245.aspx
            // though that should be done outside this class.

            if (!UInt16.TryParse(port, out this.port))
            {
                throw new ArgumentException("Port must be a number between 0 - 65535");
            }

            if (!TryParseIPv4Address(addr, out this.ip))
            {
                throw new ArgumentException("Invalid IPv4 adrress: " + addr);
            }

            this.hostName = new HostName(addr);
            this.portAsString = port;

            // Since address and port cannot be modified, i.e. IPv4EndPoint is immutable, calc 
            // the hash and string now and always return those when asked for.

            this.hash = unchecked((int)this.ip) ^ this.port;
            this.asString = String.Format("{0}:{1}", hostName.CanonicalName, port);
        }

        public static bool TryParse(string addr, string port, out IPv4EndPoint endPoint)
        {
            ushort portNum;
            if (!UInt16.TryParse(port, out portNum))
            {
                endPoint = null;
                return false;
            }

            uint ip;
            if (!TryParseIPv4Address(addr, out ip))
            {
                endPoint = null;
                return false;
            }

            endPoint = new IPv4EndPoint(addr, port);

            return true;
        }

        public static bool TryParseIPv4Address(string addr, out uint ip)
        {
            ip = 0;

            if (String.IsNullOrWhiteSpace(addr))
                return false;

            string[] octets = addr.Split('.');

            if (octets.Length != 4)
                return false;

            byte octet;
            for (int i = 0; i < 4; i++)
            {
                if (!Byte.TryParse(octets[i], out octet))
                    return false;

                ip |= (uint)(octet << (8*(3-i)));
            }

            return true;
        }

        public override int GetHashCode()
        {
            return hash;
        }

        public override string ToString()
        {
            return asString;
        }

        public bool Equals(IPv4EndPoint other)
        {
            return this.ip == other.ip && this.port == other.port;
        }

        // NOTE: We have not overridden Object.Equal(), so that will still be used.
    }
}
#endif