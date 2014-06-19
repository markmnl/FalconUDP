using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FalconUDPTests
{
    [TestClass]
    public class FalconPumpTests
    {
        private const int StartPort = 37986;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.0 / 60.0);
        private static readonly TimeSpan MaxReplyWaitTime = TimeSpan.FromSeconds(1.0);

        private static int portCount = StartPort;

        private int GetUnusedPortNumber()
        {
            portCount++;
            while (IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(ip => ip.Port == portCount))
                portCount++;
            return portCount;
        }

        public void ConnectTest()
        {
            var peer1 = new FalconPump(GetUnusedPortNumber(), FalconPoolSizes.Default, TickInterval);
        }
    }
}
