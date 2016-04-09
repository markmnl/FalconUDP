using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace FalconUDP
{
    internal class UPnPInternetGatewayDevice
    {
        private string serviceName;
        private string controlUrl;

        public UPnPInternetGatewayDevice(string serviceName, string absoluteControlUri)
        {
            this.serviceName = serviceName;
            this.controlUrl = absoluteControlUri;
        }
        
        private static string GetUPnPProtocolString(ProtocolType type)
        {
            if (type == ProtocolType.Tcp)
                return "TCP";
            if (type == ProtocolType.Udp)
                return "UDP";
            throw new ArgumentException(type.ToString());
        }
        
        public static void BeginCreate(string locationUri, Action<UPnPInternetGatewayDevice> callback)
        {
#if WINDOWS_UWP
            return;
#else

            string controlUri = null;
            string serviceName = null;
            
            Task.Factory.StartNew(() =>
            {
                var request = HttpWebRequest.Create(locationUri);

                using (var webResponse = (HttpWebResponse)request.GetResponse())
                {
                    using (var stream = webResponse.GetResponseStream())
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.Load(stream);
                        XmlNamespaceManager namespaceManager = new XmlNamespaceManager(doc.NameTable);
                        namespaceManager.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");

                        // we are looking for a InternetGatewayDevice
                        XmlNode deviceTypeNode = doc.SelectSingleNode("//tns:device/tns:deviceType/text()", namespaceManager);
                        if (!deviceTypeNode.Value.Contains("InternetGatewayDevice"))
                            return;

                        // get the controlURL of WANIPConnection or WANPPPConnection 
                        XmlNode controlUrlElement = doc.SelectSingleNode("//tns:service[tns:serviceType='urn:schemas-upnp-org:service:WANIPConnection:1']/tns:controlURL/text()", namespaceManager);
                        serviceName = "WANIPConnection:1";
                        if (controlUrlElement == null)
                        {
                            controlUrlElement = doc.SelectSingleNode("//tns:service[tns:serviceType='urn:schemas-upnp-org:service:WANPPPConnection:1']/tns:controlURL/text()", namespaceManager);
                            serviceName = "WANPPPConnection:1";
                            if (controlUrlElement == null)
                            {
                                return;
                            }
                        }

                        string url = controlUrlElement.Value;
                        if (url.StartsWith("http")) // i.e. already absolute
                        {
                            controlUri = url;
                        }
                        else
                        {
                            string baseUri = new Uri(locationUri).GetLeftPart(UriPartial.Authority);
                            controlUri = baseUri + (url.StartsWith("/") ? url : "/" + url);
                        }
                    }
                }

            }).ContinueWith(completedTask => 
            {
                if (completedTask.Exception != null) // IMPORTANT Exception must be accessed otherwise thrown on finalize
                {
                    callback(null);
                }
                else
                {
                    UPnPInternetGatewayDevice device = new UPnPInternetGatewayDevice(serviceName, controlUri);
                    callback(device);
                }
            });
#endif
        }

        private bool SendCommand(string requestBody, string function)
        {
#if WINDOWS_UWP
            return false;
#else

            try
            {
                string request = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                requestBody +
                "</s:Body>" +
                "</s:Envelope>";

                byte[] body = Encoding.UTF8.GetBytes(request);

                HttpWebRequest webRequest = (HttpWebRequest)HttpWebRequest.Create(controlUrl);
                webRequest.Method = "POST";
                webRequest.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:" + serviceName + "#" + function + "\"");
                webRequest.ContentType = "text/xml; charset=\"utf-8\"";

                using(var requestStream = webRequest.GetRequestStream())
                {
                    requestStream.Write(body, 0, body.Length);
                }
                
                using (var webResponse = (HttpWebResponse)webRequest.GetResponse())
                {
                    return webResponse.StatusCode == HttpStatusCode.OK;
                    // NOTE: Failure can be for a number or reasons, one I have encountered is 
                    //       is the mapping was already added.
                }
            }
            catch
            {
                // NOTE: GetResponse() throws exception for error responses
                return false;
            }
#endif
        }
        
        public bool TryAddForwardingRule(ProtocolType protocol, IPAddress addr, ushort port, string description)
        {
#if WINDOWS_UWP
            return false;
#else
            string protocolString = GetUPnPProtocolString(protocol);
            string portString = port.ToString();
            string request =  String.Format("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:{0}\">", serviceName) + 
String.Format(@"<NewRemoteHost></NewRemoteHost>
  <NewExternalPort>{1}</NewExternalPort>
  <NewProtocol>{2}</NewProtocol>
  <NewInternalPort>{3}</NewInternalPort>
  <NewInternalClient>{4}</NewInternalClient>
  <NewEnabled>1</NewEnabled>
  <NewPortMappingDescription>{5}</NewPortMappingDescription>
  <NewLeaseDuration>0</NewLeaseDuration>
</u:AddPortMapping>", serviceName, portString, protocolString, portString, addr, description);

            return SendCommand(request, "AddPortMapping");
#endif
        }

        public bool TryDeleteForwardingRule(ProtocolType protocol, ushort port)
        {
#if WINDOWS_UWP
            return false;
#else
            string protocolString = GetUPnPProtocolString(protocol);
            string portString = port.ToString();
            string request = String.Format("<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:{0}\">", serviceName) +
String.Format(@"<NewRemoteHost></NewRemoteHost>
  <NewExternalPort>{1}</NewExternalPort>
  <NewProtocol>{2}</NewProtocol>
</u:DeletePortMapping>", serviceName, portString, protocolString);
            
            return SendCommand(request, "DeletePortMapping");
#endif
        }
    }
}
