﻿using System;
using System.Net;
using System.Net.Sockets;

namespace FalconUDP
{
    internal class SocketTransceiver : IFalconTransceiver
    {
        private const int DefaultTypeOfService = 0;
        private const int EFTypeOfService = 184;

        private Socket socket;
        private IPEndPoint anyAddrEndPoint;
        private bool isEFSet;
        private FalconPeer localPeer;
        private EndPoint placeHolderEndPoint = new IPEndPoint(IPAddress.Any, 30000);


        public int BytesAvaliable
        {
            get 
            { 
                return socket.Available; 
            }
        }

        public SocketTransceiver(FalconPeer localPeer)
        {
            this.localPeer = localPeer;
            this.anyAddrEndPoint = new IPEndPoint(IPAddress.Any, localPeer.Port);
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        public FalconOperationResult TryStart()
        {
            try
            {
                try
                {
#if !(MONO || WINDOWS_UWP)
                    socket.SetIPProtectionLevel(IPProtectionLevel.EdgeRestricted);
#endif
#if !LINUX
                    socket.IOControl(-1744830452, new byte[] { 0 }, new byte[] { 0 }); // http://stackoverflow.com/questions/10332630/connection-reset-on-receiving-packet-in-udp-server
#endif
                }
                catch
                {
                    // we tried, but does fail for some and is not fatal e.g. http://steamcommunity.com/app/334560/discussions/0/405691147596493066/
                }
                socket.Bind(anyAddrEndPoint);
                socket.Blocking = false;
                socket.ReceiveBufferSize = localPeer.ReceiveBufferSize;
                socket.SendBufferSize = localPeer.SendBufferSize;
                socket.EnableBroadcast = true;
                SetEF();
            }
            catch (SocketException se)
            {
                // e.g. address already in use
                return new FalconOperationResult(se);
            }

            return FalconOperationResult.SuccessResult;
        }

        private void SetEF()
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, EFTypeOfService);
            isEFSet = true;
        }

        private void UnsetEF()
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, DefaultTypeOfService);
            isEFSet = false; 
        }

        public void Stop()
        {
            try
            {
#if WINDOWS_UWP
                socket.Dispose();
#else
                socket.Close();
#endif
            }
            catch { }
        }

        // returns 0 if fatal failure receiving from epFrom
        public int Read(byte[] receiveBuffer, ref IPEndPoint ipFrom)
        {
            int size = 0;
            try
            {
                size = socket.ReceiveFrom(receiveBuffer, ref placeHolderEndPoint);
                ipFrom = (IPEndPoint)placeHolderEndPoint;
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Exception {0} {1}, while receiving from {2}."
#if WINDOWS_UWP
                    , se.SocketErrorCode
#else
                    , se.ErrorCode
#endif
                    , se.Message
                    , ipFrom));
            }
            return size;
        }

        // return false if fatal failure sending to ip
        public bool Send(byte[] buffer, int index, int count, IPEndPoint ip, bool expidite)
        {
            if (expidite != isEFSet)
            {
                if (expidite)
                    SetEF();
                else
                    UnsetEF();
            }

            try
            {
                socket.SendTo(buffer, index, count, SocketFlags.None, ip);
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Error {0}: {1}, sending to peer: {2}"
#if WINDOWS_UWP
                    , se.SocketErrorCode
#else
                    , se.ErrorCode
#endif
                    , se.Message
                    , ip));
                return false;
            }

            return true;
        }
    }
}
