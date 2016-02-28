using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

namespace FalconUDP
{
    class AutonomousTransciever : IFalconTransceiver
    {
        struct DatagramDetail
        {
            public int Index, Count;
            public IPEndPoint IP;
        }

        private Socket socket;
        private IPEndPoint anyAddrEndPoint;
        private FalconPeer localPeer;
        private EndPoint placeHolderEndPoint = new IPEndPoint(IPAddress.Any, 30000);
        private Thread netThread;
        private byte[] lastDatagramBuffer;
        private byte[] receivedDatagramsBuffer;                         //
        private int receivedDatagramsIndex;                             // Updated from netThread, read from application; lock on receivedDatagramsBuffer
        private Queue<DatagramDetail> receivedDatagramDetails;          //

        public int BytesAvaliable
        {
            get 
            { 
                return receivedDatagramsIndex; 
            }
        }

        public AutonomousTransciever(FalconPeer localPeer)
        {
            this.localPeer = localPeer;
            this.anyAddrEndPoint = new IPEndPoint(IPAddress.Any, localPeer.Port);
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            this.lastDatagramBuffer = new byte[FalconPeer.MaxDatagramSize];
            this.receivedDatagramsBuffer = new byte[localPeer.ReceiveBufferSize];
            this.receivedDatagramDetails = new Queue<DatagramDetail>();
        }

        private void Run()
        {
            while (socket.IsBound)
            {
                int size = 0;
                try
                {
                    size = socket.ReceiveFrom(lastDatagramBuffer, ref placeHolderEndPoint);
                }
                catch (SocketException ex)
                {
                    localPeer.Log(LogLevel.Error, "SocketException ReceiveFrom " + ex.Message);
                    continue;
                }

                if (size == 0)
                    continue;
                if (size > FalconPeer.MaxDatagramSize)
                    continue;
                
                lock (receivedDatagramsBuffer)
                {
                    var detail = new DatagramDetail();
                    detail.Index = receivedDatagramsIndex;
                    detail.Count = size;
                    detail.IP = (IPEndPoint)placeHolderEndPoint;
                    receivedDatagramDetails.Enqueue(detail);

                    if (size > 0 && ((receivedDatagramsIndex + size) < receivedDatagramsBuffer.Length))
                    {
                        Buffer.BlockCopy(lastDatagramBuffer, 0, receivedDatagramsBuffer, receivedDatagramsIndex, size);
                        Interlocked.Exchange(ref receivedDatagramsIndex, (receivedDatagramsIndex + size));
                    }
                }
            }
        }

        public FalconOperationResult TryStart()
        {
            try
            {
                socket.Bind(anyAddrEndPoint);
                socket.ReceiveBufferSize = localPeer.ReceiveBufferSize;
                socket.SendBufferSize = localPeer.SendBufferSize;
                socket.EnableBroadcast = true;
            }
            catch (SocketException se)
            {
                // e.g. address already in use
                return new FalconOperationResult(se);
            }

            netThread = new Thread(Run);
            netThread.IsBackground = true; // terminate if the app does
            netThread.Start();

            return FalconOperationResult.SuccessResult;
        }

        public void Stop()
        {
            try
            {
                socket.Close();
            }
            catch { }
            try
            {
                netThread.Abort();
            }
            catch { }
        }

        // returns 0 if fatal failure receiving from epFrom
        public int Read(byte[] receiveBuffer, ref IPEndPoint ipFrom)
        {
            lock (receivedDatagramsBuffer)
            {
                var detail = receivedDatagramDetails.Dequeue();

                ipFrom = detail.IP;

                // If no more details remain all messages have been read from buffer, reset index
                // so future messages can re-use the receivedDatagramsBuffer.
                //
                // ASSUMPTION: We will never receive more messages than receivedDatagramsBuffer can hold 
                //             between reads to read all messages
                // 
                if (receivedDatagramDetails.Count == 0)
                {
                    receivedDatagramsIndex = 0;
                }

                if (detail.Count > 0)
                {
                    Buffer.BlockCopy(receivedDatagramsBuffer, detail.Index, receiveBuffer, 0, detail.Count);
                }

                return detail.Count;
            }
        }

        // return false if fatal failure sending to ip
        public bool Send(byte[] buffer, int index, int count, IPEndPoint ip, bool expidite)
        {
            // NOTE: expidite ignored, setting EF results in exception on PS4

            try
            {
                socket.SendTo(buffer, index, count, SocketFlags.None, ip);
            }
            catch (SocketException se)
            {
                localPeer.Log(LogLevel.Error, String.Format("Socket Error {0}: {1}, sending to peer: {2}", se.ErrorCode.ToString(), se.Message, ip));
                return false;
            }

            return true;
        }
    }
}
