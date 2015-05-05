using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;

namespace FalconUDP
{
    /// <summary>
    /// Replacement for IPEndPoint which no in NETFX_CORE which uses strings instead?!
    /// </summary>
    public class IPEndPoint
    {
        private HostName hostName;
        private string portAsString;
        private uint ip;
        private ushort port;
        private int hash;
        private string asString;

        internal HostName Address { get { return hostName; } }    //} NETFX_CORE expects these silly types using it's API
        internal string Port { get { return portAsString; } }     //} 

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
            if (hash == null)
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
