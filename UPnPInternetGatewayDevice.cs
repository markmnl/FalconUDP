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
        private string controlUrl;

        public UPnPInternetGatewayDevice(Uri serviceUri)
        {
            this.controlUrl = serviceUri.AbsoluteUri;
        }
        
        private static string GetUPnPProtocolString(ProtocolType type)
        {
            if (type == ProtocolType.Tcp)
                return "TCP";
            if (type == ProtocolType.Udp)
                return "UDP";
            throw new ArgumentException(type.ToString());
        }
        
        public static void BeginCreate(Uri locationUri, Action<UPnPInternetGatewayDevice> callback)
        {
            Uri controlUri = null;
            
            Task.Factory.StartNew(() =>
            {
                var request = WebRequest.Create(locationUri);

                using (var webResponse = request.GetResponse())
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
                    XmlNode serviceNode = doc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:WANIPConnection:1\" or tns:serviceType=\"urn:schemas-upnp-org:service:WANPPPConnection:1\"]/tns:controlURL/text()", namespaceManager);
                    if (serviceNode == null)
                        return;

                    string url = serviceNode.Value;
                    
                    if (url.StartsWith("http")) // i.e. absolute
                        controlUri = new Uri(url);
                    else
                        controlUri = new Uri(locationUri, url);
                }

            }).ContinueWith(completedTask => 
            {
                if (completedTask.Exception != null) // IMPORTANT Exception must be accessed otherwise thrown on finalize
                {
                    callback(null);
                }
                else
                {
                    UPnPInternetGatewayDevice device = new UPnPInternetGatewayDevice(controlUri);
                    callback(device);
                }
            });
        }

        private XmlDocument SOAPRequest(string soapBody, string function)
        {
            try
            {
                string request = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                soapBody +
                "</s:Body>" +
                "</s:Envelope>";

                WebRequest webRequest = HttpWebRequest.Create(controlUrl);
                webRequest.Method = "POST";
                byte[] body = Encoding.UTF8.GetBytes(request);
                webRequest.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#" + function + "\"");
                webRequest.ContentType = "text/xml; charset=\"utf-8\"";
                webRequest.ContentLength = body.Length;
                webRequest.GetRequestStream().Write(body, 0, body.Length);

                XmlDocument doc = new XmlDocument();
                using (var webResponse = webRequest.GetResponse())
                using (var stream = webResponse.GetResponseStream())
                {
                    doc.Load(stream);
                }
                return doc;
            }
            catch
            {
                return null;
            }
        }
        
        public bool TryAddForwardingRule(ProtocolType protocol, IPAddress addr, ushort port, string description)
        {
            string protocolString = GetUPnPProtocolString(protocol);
            string portString = port.ToString();
            string request = String.Format(@"<u:AddPortMapping xmlns:u='urn:schemas-upnp-org:service:WANIPConnection:1\'>
  <NewRemoteHost></NewRemoteHost>
  <NewExternalPort>{0}</NewExternalPort>
  <NewProtocol>{1}</NewProtocol>
  <NewInternalPort>{2}</NewInternalPort>
  <NewInternalClient>{3}</NewInternalClient>
  <NewEnabled>1</NewEnabled>
  <NewPortMappingDescription>{4}</NewPortMappingDescription>
  <NewLeaseDuration>0</NewLeaseDuration>
</u:AddPortMapping>", portString, protocolString, portString, addr, description);

            XmlDocument result = SOAPRequest(request, "AddPortMapping");

            return result != null; // TODO and parse?
        }

        public bool TryDeleteForwardingRule(ProtocolType protocol, ushort port)
        {
            string protocolString = GetUPnPProtocolString(protocol);
            string portString = port.ToString();
            string request = String.Format(@"<u:DeletePortMapping xmlns:u='urn:schemas-upnp-org:service:WANIPConnection:1\'>
  <NewRemoteHost></NewRemoteHost>
  <NewExternalPort>{0}</NewExternalPort>
  <NewProtocol>{1}</NewProtocol>
</u:DeletePortMapping>", portString, protocolString);
            
            XmlDocument result = SOAPRequest(request, "DeletePortMapping");

            return result != null; // TODO and parse?
        }
    }
}
