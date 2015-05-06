using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace FalconUDP
{
    internal class DatagramSocketTransceiver : IFalconTransceiver
    {
        struct MessageDetail
        {
            public int Index, Count;
            public HostName RemoteAddress;
            public string RemotePort;
        }

        private DatagramSocket datagramSocket;
        private FalconPeer localPeer;
        private bool isEFSet;
        private int messagesBufferIndex;                        //} 
        private readonly byte[] messagesBuffer;                 //} Updated from different threads, lock on messagesBuffer
        private readonly Queue<MessageDetail> messageDetails;   //}
        private readonly Dictionary<IPEndPoint, IOutputStream> outputStreams;   // cache of output streams keyed on their endpoint

        public int BytesAvaliable
        {
            get 
            {
                lock (messagesBuffer)
                {
                    return messagesBufferIndex;
                }
            }
        }

        internal DatagramSocketTransceiver(FalconPeer localPeer)
        {
            this.localPeer = localPeer;
            this.messagesBuffer = new byte[localPeer.ReceiveBufferSize];
            this.messageDetails = new Queue<MessageDetail>();
            this.outputStreams = new Dictionary<IPEndPoint, IOutputStream>();
            
        }

        private void SetEF()
        {
            datagramSocket.Control.QualityOfService = SocketQualityOfService.LowLatency;
            isEFSet = true;
        }

        private void UnsetEF()
        {
            datagramSocket.Control.QualityOfService = SocketQualityOfService.Normal;
            isEFSet = false;
        }

        private void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            // NOTE: This event is raised on different threads

            lock (messagesBuffer)
            {
                try
                {
                    using (var dr = args.GetDataReader()) // TODO how to prevent garbage here? https://social.msdn.microsoft.com/Forums/windowsapps/en-US/b1d490f7-a637-4648-925a-99fd7f55af1d/missing-datareader-and-datawriter-readbytes-and-writebytes-overloads-to-which-a?forum=winappswithcsharp#b1d490f7-a637-4648-925a-99fd7f55af1d
                    {
                        var size = (int)dr.UnconsumedBufferLength;
                        var datagram = dr.ReadBuffer(dr.UnconsumedBufferLength);

                        datagram.CopyTo(0, messagesBuffer, messagesBufferIndex, size);

                        messageDetails.Enqueue(new MessageDetail
                        {
                            Index = messagesBufferIndex,
                            Count = size,
                            RemoteAddress = args.RemoteAddress,
                            RemotePort = args.RemotePort
                        });

                        messagesBufferIndex += size;
                    }
                }
                catch (Exception)
                {
                    // e.g. connection closed
                }
            }
        }

        public FalconOperationResult TryStart()
        {
            try
            {
                datagramSocket = new DatagramSocket();

                SetEF();

                datagramSocket.MessageReceived += OnMessageReceived;

                // HACK: We know this is not an async op and will be run in-line so avoid breaking our API
                //       and "wait" for op to complete.
                //
                var asyncOp = datagramSocket.BindServiceNameAsync(localPeer.Port.ToString());
                var task = asyncOp.AsTask();
                task.Wait();

                return FalconOperationResult.SuccessResult;
            }
            catch (Exception ex)
            {
                // e.g. address already in use
                return new FalconOperationResult(ex);
            }
        }

        public void Stop()
        {
            datagramSocket.Dispose();
        }

        public int Read(byte[] recieveBuffer, ref IPEndPoint ipFrom)
        {
            lock (messagesBuffer)
            {
                if (messageDetails.Count == 0)
                    return 0;

                MessageDetail detail = messageDetails.Dequeue();

                // If no more details remain all messages have been read from buffer, reset index
                // so future messages can re-use the messagesBuffer.
                //
                // ASSUMPTION: We will never receive more messages than messageBuffer can hold 
                //             between reads to read all messages
                // 
                if (messageDetails.Count == 0)
                    messagesBufferIndex = 0;
                
                // Try assign ipFrom to IPEndPoint message received from
                if (!IPEndPoint.TryParse(detail.RemoteAddress.RawName, detail.RemotePort, out ipFrom))
                {
                    localPeer.Log(LogLevel.Error, String.Format("Failed to parse remote end point: {0}:{1}", detail.RemoteAddress.RawName, detail.RemotePort));
                    return 0;
                }

                System.Buffer.BlockCopy(messagesBuffer, detail.Index, recieveBuffer, 0, detail.Count);

                return detail.Count;
            }
        }

        public bool Send(byte[] buffer, int index, int count, IPEndPoint ip, bool expidite)
        {
            // NOTE: NETFX_CORE does not support changing EF so we are stuck with what we set when 
            //       DatagramSocket created and ignore expidite.

            // The DatagramSocket sample (https://code.msdn.microsoft.com/windowsapps/DatagramSocket-sample-76a7d82b) 
            // says creating OutputStreams per datagram is costly and recommends caching them. An 
            // OutputStream is required per endpoint so keep one for each endpoint we send to.

            // TODO over an extended period and/or if communicating with many endpoints should clean-up old unused ones

            IOutputStream outputStream;
            if (!outputStreams.TryGetValue(ip, out outputStream))
            {
                // HACK: We know this is not an async op and will be run in-line so avoid breaking our API
                //       and "wait" for op to complete.
                //
                var asyncOp = datagramSocket.GetOutputStreamAsync(ip.Address, ip.PortAsString);
                var task = asyncOp.AsTask();
                task.Wait();

                outputStream = task.Result;
            }

            // How to avoid creating garbage with all these streams: https://social.msdn.microsoft.com/Forums/windowsapps/en-US/b1d490f7-a637-4648-925a-99fd7f55af1d/missing-datareader-and-datawriter-readbytes-and-writebytes-overloads-to-which-a?forum=winappswithcsharp#b1d490f7-a637-4648-925a-99fd7f55af1d

            try
            {
                var rtBuffer = buffer.AsBuffer(index, count);

                // HACK: We know this is not an async op and will be run in-line so avoid breaking our API
                //       and "wait" for op to complete.
                //
                var asyncOp = outputStream.WriteAsync(rtBuffer);
                var task = asyncOp.AsTask();
                task.Wait();
            }
            catch (Exception ex)
            {
                localPeer.Log(LogLevel.Error, String.Format("DatagramSocket Error {0}: sending to peer: {1}", ex.Message, ip.ToString()));
                return false;
            }

            return true;
        }
    }
}
