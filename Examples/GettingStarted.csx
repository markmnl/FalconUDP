using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FalconUDP;

private void ProcessRecievedPacket(Packet p)
{
	System.Console.WriteLine(p.ReadStringPrefixedWithSize(System.Text.Encoding.UTF8));
}

