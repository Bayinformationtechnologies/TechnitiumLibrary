﻿/*
Technitium Library
Copyright (C) 2015  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace TechnitiumLibrary.Net.UPnP.Networking
{
    public class InternetGatewayDevice
    {
        #region variables

        IPAddress _deviceIP;
        IPAddress _networkBroadcastAddress;

        private Uri _controlURLIP;
        private Uri _controlURLPPP;

        #endregion

        #region constructor

        private InternetGatewayDevice()
        { }

        #endregion

        #region public static

        public static InternetGatewayDevice Discover(IPAddress networkBroadcastAddress, int timeout, int maxRetries = 3)
        {
            Socket sUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sUdp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
            sUdp.ReceiveTimeout = timeout;

            byte[] request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\n" +
                                                                "HOST: 239.255.255.250:1900\r\n" +
                                                                "ST:upnp:rootdevice\r\n" +
                                                                "MAN:\"ssdp:discover\"\r\n" +
                                                                "MX:3\r\n\r\n");

            byte[] buffer = new byte[8 * 1024];

            try
            {
                int retryCount = 1;
                //retry loop
                do
                {
                    Exception lastEx = null;
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    int bytesRecv = 0;

                    sUdp.SendTo(request, new IPEndPoint(networkBroadcastAddress, 1900));

                    //read all responses loop
                    do
                    {
                        try
                        {
                            bytesRecv = sUdp.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remoteEP);
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            bytesRecv = 0;
                        }

                        if (bytesRecv > 0)
                        {
                            try
                            {
                                Uri descriptionUri = null;
                                Uri controlURLIP = null;
                                Uri controlURLPPP = null;
                                bool isRootDevice = false;

                                using (StreamReader sR = new StreamReader(new MemoryStream(buffer, 0, bytesRecv, false)))
                                {
                                    do
                                    {
                                        string tmp = sR.ReadLine();

                                        if (tmp == null)
                                            break;

                                        if (tmp.StartsWith("location:", StringComparison.CurrentCultureIgnoreCase))
                                            descriptionUri = new Uri(tmp.Substring(9).Trim());

                                        else if (tmp.StartsWith("st:", StringComparison.CurrentCultureIgnoreCase))
                                            isRootDevice = tmp.Substring(3).Trim().Equals("upnp:rootdevice", StringComparison.CurrentCultureIgnoreCase);

                                    } while (true);
                                }

                                if (isRootDevice)
                                {
                                    //find service URL
                                    XmlDocument desc = new XmlDocument();
                                    desc.Load(WebRequest.Create(descriptionUri).GetResponse().GetResponseStream());

                                    XmlNamespaceManager nsMgr = new XmlNamespaceManager(desc.NameTable);
                                    nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");

                                    XmlNode typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
                                    if (!typen.Value.Contains("InternetGatewayDevice"))
                                        throw new InternetGatewayDeviceException("Error while parsing XML response from UPnP root device. Cannot find InternetGatewayDevice node.");

                                    XmlNode node;

                                    node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:WANPPPConnection:1\"]/tns:controlURL/text()", nsMgr);
                                    if (node != null)
                                    {
                                        if (node.Value.StartsWith("http:", StringComparison.CurrentCultureIgnoreCase))
                                            controlURLPPP = new Uri(node.Value);
                                        else
                                            controlURLPPP = new Uri(descriptionUri, node.Value);
                                    }

                                    node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:WANIPConnection:1\"]/tns:controlURL/text()", nsMgr);
                                    if (node != null)
                                    {
                                        if (node.Value.StartsWith("http:", StringComparison.CurrentCultureIgnoreCase))
                                            controlURLIP = new Uri(node.Value);
                                        else
                                            controlURLIP = new Uri(descriptionUri, node.Value);
                                    }

                                    if ((controlURLIP == null) && (controlURLPPP == null))
                                        throw new InternetGatewayDeviceException("Cannot find control URL in XML response from UPnP root device.");

                                    return new InternetGatewayDevice
                                    {
                                        _deviceIP = (remoteEP as IPEndPoint).Address,
                                        _networkBroadcastAddress = networkBroadcastAddress,
                                        _controlURLIP = controlURLIP,
                                        _controlURLPPP = controlURLPPP
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                lastEx = ex;
                            }
                        }
                        else
                        {
                            break;
                        }
                    } while (true);

                    retryCount++;
                    if (retryCount > maxRetries)
                        throw new InternetGatewayDeviceException("UPnP internet gateway device was not found.", lastEx);

                } while (true);
            }
            finally
            {
                sUdp.Close();
            }
        }

        #endregion

        #region private

        private HttpWebResponse SOAPRequest(string SOAP, string functionName)
        {
            Exception exPPP = null;

            if (_controlURLPPP != null)
            {
                try
                {
                    return SOAPRequest(_controlURLPPP, SOAP, functionName, "WANPPPConnection");
                }
                catch (WebException ex)
                {
                    exPPP = ex;
                }
            }

            if (_controlURLIP != null)
            {
                try
                {
                    return SOAPRequest(_controlURLIP, SOAP.Replace("service:WANPPPConnection:1", "service:WANIPConnection:1"), functionName, "WANIPConnection");
                }
                catch (WebException ex)
                {
                    if (exPPP != null)
                        throw new InternetGatewayDeviceException("Server returned an error.", exPPP);
                    else
                        throw new InternetGatewayDeviceException("Server returned an error.", ex);
                }
            }

            throw new InternetGatewayDeviceException("No control URL available to make UPnP SOAP request.");
        }

        private static HttpWebResponse SOAPRequest(Uri controlURL, string SOAP, string functionName, string WANService)
        {
            string request = "<?xml version=\"1.0\"?>\r\n" +
                            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                            "<s:Body>\r\n" +
                            SOAP + "\r\n" +
                            "</s:Body>\r\n" +
                            "</s:Envelope>";

            byte[] buffer = Encoding.ASCII.GetBytes(request);

            WebRequest wReq = WebRequest.Create(controlURL);
            wReq.Method = "POST";
            wReq.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:" + WANService + ":1#" + functionName + "\"");
            wReq.ContentType = "text/xml; charset=\"utf-8\"";
            wReq.ContentLength = buffer.Length;
            wReq.GetRequestStream().Write(buffer, 0, buffer.Length);

            HttpWebResponse response;

            try
            {
                response = (HttpWebResponse)wReq.GetResponse();
            }
            catch (WebException ex)
            {
                response = (HttpWebResponse)ex.Response;
            }

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 401:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Invalid Action.");

                case 402:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Invalid Args.");

                case 404:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Invalid Var.");

                case 501:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Action Failed.");

                case 500:
                    try
                    {
                        XmlDocument xResp = new XmlDocument();
                        xResp.Load(response.GetResponseStream());

                        XmlNamespaceManager nsMgr = new XmlNamespaceManager(xResp.NameTable);
                        nsMgr.AddNamespace("s", "http://schemas.xmlsoap.org/soap/envelope/");

                        XmlNode nodeFaultDetail = xResp.SelectSingleNode("//s:Envelope/s:Body/s:Fault/detail", nsMgr);

                        if (nodeFaultDetail != null)
                        {
                            XmlNode nodeDetailItem = nodeFaultDetail.FirstChild;

                            if (nodeDetailItem != null)
                            {
                                string errorCode = "unknown";
                                string errorDescription = "unknown";

                                foreach (XmlNode nodeChild in nodeDetailItem.ChildNodes)
                                {
                                    switch (nodeChild.LocalName)
                                    {
                                        case "errorCode":
                                            errorCode = nodeChild.InnerText;
                                            break;

                                        case "errorDescription":
                                            errorDescription = nodeChild.InnerText;
                                            break;
                                    }
                                }

                                throw new InternetGatewayDeviceException("UPnP device returned an error: (" + errorCode + ") " + errorDescription);
                            }
                        }
                    }
                    catch (InternetGatewayDeviceException)
                    {
                        throw;
                    }
                    catch
                    {
                        //ignore any response xml parsing errors
                    }

                    //throw generic error
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Internal Server Error.");

                default:
                    return response;
            }
        }

        #endregion

        #region public

        public void AddPortMapping(ProtocolType protocol, int externalPort, IPEndPoint internalEP, string description, bool enabled = true, uint leaseDuration = 0)
        {
            string SOAP = "<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANPPPConnection:1\">" +
                                "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + externalPort + "</NewExternalPort><NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                                "<NewInternalPort>" + internalEP.Port + "</NewInternalPort><NewInternalClient>" + internalEP.Address.ToString() + "</NewInternalClient>" +
                                "<NewEnabled>" + (enabled ? "1" : "0") + "</NewEnabled><NewPortMappingDescription>" + description + "</NewPortMappingDescription>" +
                                "<NewLeaseDuration>" + leaseDuration + "</NewLeaseDuration>" +
                           "</u:AddPortMapping>";

            HttpWebResponse response = SOAPRequest(SOAP, "AddPortMapping");

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 200: //success
                    break;

                case 715:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") The source IP address cannot be wild-carded.");

                case 716:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") The external port cannot be wild-carded.");

                case 718:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") The port mapping entry specified conflicts with a mapping assigned previously to another client.");

                case 724:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") Internal and External port values must be the same.");

                case 725:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") The NAT implementation only supports permanent lease times on port mappings.");

                case 726:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") RemoteHost must be a wildcard and cannot be a specific IP address or DNS name.");

                case 727:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") ExternalPort must be a wildcard and cannot be a specific port value.");

                default:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") " + response.StatusDescription);
            }
        }

        public void DeletePortMapping(ProtocolType protocol, int externalPort)
        {
            string SOAP = "<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANPPPConnection:1\">" +
                                "<NewRemoteHost></NewRemoteHost>" +
                                "<NewExternalPort>" + externalPort + "</NewExternalPort>" +
                                "<NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                          "</u:DeletePortMapping>";

            HttpWebResponse response = SOAPRequest(SOAP, "DeletePortMapping");

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 200: //success
                    break;

                case 714:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") The specified value does not exists in the array.");

                default:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") " + response.StatusDescription);
            }
        }

        public IPAddress GetExternalIPAddress()
        {
            string SOAP = "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANPPPConnection:1\"></u:GetExternalIPAddress>";

            HttpWebResponse response = SOAPRequest(SOAP, "GetExternalIPAddress");

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 200:
                    //success
                    XmlDocument xResp = new XmlDocument();
                    xResp.Load(response.GetResponseStream());

                    XmlNamespaceManager nsMgr = new XmlNamespaceManager(xResp.NameTable);
                    XmlNode node = xResp.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr);

                    return IPAddress.Parse(node.Value);

                default:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") " + response.StatusDescription);
            }
        }

        public PortMappingEntry GetSpecificPortMappingEntry(ProtocolType protocol, int externalPort)
        {
            string SOAP = "<u:GetSpecificPortMappingEntry xmlns:u=\"urn:schemas-upnp-org:service:WANPPPConnection:1\">" +
                                "<NewRemoteHost></NewRemoteHost>" +
                                "<NewExternalPort>" + externalPort + "</NewExternalPort>" +
                                "<NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                           "</u:GetSpecificPortMappingEntry>";

            HttpWebResponse response = SOAPRequest(SOAP, "GetSpecificPortMappingEntry");

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 200:
                    //success
                    XmlDocument xResp = new XmlDocument();
                    xResp.Load(response.GetResponseStream());

                    XmlNamespaceManager nsMgr = new XmlNamespaceManager(xResp.NameTable);

                    XmlNode node1 = xResp.SelectSingleNode("//NewInternalPort/text()", nsMgr);
                    XmlNode node2 = xResp.SelectSingleNode("//NewInternalClient/text()", nsMgr);
                    XmlNode node3 = xResp.SelectSingleNode("//NewEnabled/text()", nsMgr);
                    XmlNode node4 = xResp.SelectSingleNode("//NewPortMappingDescription/text()", nsMgr);
                    XmlNode node5 = xResp.SelectSingleNode("//NewLeaseDuration/text()", nsMgr);

                    return new PortMappingEntry(Convert.ToInt32(node1.Value), IPAddress.Parse(node2.Value), Convert.ToBoolean(Convert.ToInt32(node3.Value)), node4.Value, Convert.ToInt32(node5.Value));

                case 714:
                    //The specified value does not exists in the array
                    return null;

                default:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") " + response.StatusDescription);
            }
        }

        public GenericPortMappingEntry GetGenericPortMappingEntry(int portMappingIndex)
        {
            string SOAP = "<u:GetGenericPortMappingEntry xmlns:u=\"urn:schemas-upnp-org:service:WANPPPConnection:1\">" +
                                "<NewPortMappingIndex>" + portMappingIndex + "</NewPortMappingIndex>" +
                          "</u:GetGenericPortMappingEntry>";

            HttpWebResponse response = SOAPRequest(SOAP, "GetGenericPortMappingEntry");

            int statusCode = Convert.ToInt32(response.StatusCode);

            switch (statusCode)
            {
                case 200:
                    //success
                    XmlDocument xResp = new XmlDocument();
                    xResp.Load(response.GetResponseStream());

                    XmlNamespaceManager nsMgr = new XmlNamespaceManager(xResp.NameTable);
                    XmlNode node1 = xResp.SelectSingleNode("//NewRemoteHost/text()", nsMgr);

                    IPAddress RemoteHost = null;
                    if (node1 == null)
                        RemoteHost = IPAddress.Any;
                    else
                        RemoteHost = IPAddress.Parse(node1.Value);

                    XmlNode node2 = xResp.SelectSingleNode("//NewExternalPort/text()", nsMgr);
                    XmlNode node3 = xResp.SelectSingleNode("//NewProtocol/text()", nsMgr);
                    XmlNode node4 = xResp.SelectSingleNode("//NewInternalPort/text()", nsMgr);
                    XmlNode node5 = xResp.SelectSingleNode("//NewInternalClient/text()", nsMgr);
                    XmlNode node6 = xResp.SelectSingleNode("//NewEnabled/text()", nsMgr);
                    XmlNode node7 = xResp.SelectSingleNode("//NewPortMappingDescription/text()", nsMgr);
                    XmlNode node8 = xResp.SelectSingleNode("//NewLeaseDuration/text()", nsMgr);

                    return new GenericPortMappingEntry(RemoteHost, Convert.ToInt32(node2.Value), (ProtocolType)Enum.Parse(typeof(ProtocolType), node3.Value, true), Convert.ToInt32(node4.Value), IPAddress.Parse(node5.Value), Convert.ToBoolean(Convert.ToInt32(node6.Value)), node7.Value, Convert.ToInt32(node8.Value));

                case 713:
                    //The specified array index is out of bound
                    return null;

                default:
                    throw new InternetGatewayDeviceException("UPnP device returned an error: (" + statusCode + ") " + response.StatusDescription);
            }
        }

        public bool ForwardPort(ProtocolType protocol, int externalPort, IPEndPoint internalEP, string description = "", bool force = false, uint leaseDuration = 0)
        {
            try
            {
                PortMappingEntry portMap = GetSpecificPortMappingEntry(protocol, externalPort);

                if (portMap != null)
                {
                    if (portMap.InternalEP.Equals(internalEP))
                    {
                        //external port already mapped
                        return true;
                    }
                    else
                    {
                        //external port not available
                        if (force)
                            DeletePortMapping(protocol, externalPort);
                        else
                            return false;
                    }
                }
            }
            catch
            { }

            try
            {
                AddPortMapping(protocol, externalPort, internalEP, description, true, leaseDuration);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region properties

        public IPAddress DeviceIP
        { get { return _deviceIP; } }

        public IPAddress NetworkBroadcastAddress
        { get { return _networkBroadcastAddress; } }

        #endregion
    }
}