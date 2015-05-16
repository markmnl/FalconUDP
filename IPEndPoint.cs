using System;
using Windows.Networking;

namespace FalconUDP
{
    /// <summary>
    /// Replacement for IPEndPoint missing in NETFX_CORE which uses strings instead?!
    /// </summary>
    public class IPEndPoint : IEquatable<IPEndPoint>
    {
        private HostName hostName;
        private string portAsString;
        private uint ip;
        private ushort port;
        private int hash;
        private string asString;

        public int Port { get { return port; } }

        internal HostName Address { get { return hostName; } }            //} NETFX_CORE expects these silly types using it's API
        internal string PortAsString { get { return portAsString; } }     //} 

        internal IPEndPoint(uint ip, ushort port)
        {
            this.ip = ip;
            this.port = port;

            var ipAsString = String.Format("{0}.{1}.{2}.{3}", 
                ((ip & 0xFF000000) >> (8 * 3)),
                ((ip & 0x00FF0000) >> (8 * 2)),
                ((ip & 0x0000FF00) >> 8),
                ((ip & 0x000000FF)));

            this.hostName = new HostName(ipAsString);
            this.portAsString = port.ToString();
        }

        public IPEndPoint(string addr, int port)
            : this(addr, port.ToString())
        {
        }

        public IPEndPoint(string addr, string port)
        {
            // Currently we only accept IPv4

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
        }

        public static bool TryParse(string addr, string port, out IPEndPoint endPoint)
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

            endPoint = new IPEndPoint(addr, port);

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
            if (hash == 0)
                hash = unchecked((int)this.ip) ^ this.port;
            return hash;
        }

        public override string ToString()
        {
            if(asString == null)
                asString = String.Format("{0}:{1}", hostName.CanonicalName, port);
            return asString;
        }

        public bool Equals(IPEndPoint other)
        {
            return this.ip == other.ip && this.port == other.port;
        }

        // NOTE: We have not overridden Object.Equal(), so that will still be used.
    }
}
