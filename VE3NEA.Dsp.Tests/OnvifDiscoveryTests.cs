using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class OnvifDiscoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProbeXmlMatchesOnvifSoap12AndWsDiscovery2005Namespaces(bool typed)
    {
        XDocument soap = XDocument.Parse(OnvifDiscovery.BuildProbeXml(typed));
        XNamespace env = "http://www.w3.org/2003/05/soap-envelope";
        XNamespace wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
        var header = soap.Root!.Element(env + "Header")!;
        Assert.StartsWith("urn:uuid:", header.Element(wsa + "MessageID")!.Value);
        Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery",
            header.Element(wsa + "To")!.Value);
        Assert.Equal("http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe",
            header.Element(wsa + "Action")!.Value);

        var probe = soap.Root.Element(env + "Body")!.Element(discovery + "Probe")!;
        Assert.NotNull(probe);
        var type = probe.Element(discovery + "Types");
        if (typed)
        {
            Assert.NotNull(type);
            Assert.Equal("dn:NetworkVideoTransmitter", type!.Value);
            Assert.Equal("http://www.onvif.org/ver10/network/wsdl",
                probe.GetNamespaceOfPrefix("dn")!.NamespaceName);
        }
        else Assert.Null(type);
    }

    [Fact]
    public void EveryProbeGetsFreshMessageId()
    {
        var first = XDocument.Parse(OnvifDiscovery.BuildProbeXml(true));
        var second = XDocument.Parse(OnvifDiscovery.BuildProbeXml(true));
        XNamespace wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        Assert.NotEqual(first.Descendants(wsa + "MessageID").Single().Value,
                        second.Descendants(wsa + "MessageID").Single().Value);
    }

    [Fact]
    public void ParsesHikvisionStyleProbeMatchesWithMultipleXAddrs()
    {
        string packet = """
          <?xml version="1.0" encoding="utf-8"?>
          <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
           xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
           xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing">
            <s:Header><wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action></s:Header>
            <s:Body><d:ProbeMatches><d:ProbeMatch>
            <d:Scopes>onvif://www.onvif.org/name/HIKVISION</d:Scopes>
            <d:XAddrs>http://192.168.1.64/onvif/device_service
                      http://192.168.1.64:8080/onvif/device_service</d:XAddrs>
            </d:ProbeMatch></d:ProbeMatches></s:Body>
          </s:Envelope>
          """;

        var devices = OnvifDiscovery.ParseProbeMatches(packet);
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d =>
            d.Endpoint == "http://192.168.1.64/onvif/device_service");
        Assert.All(devices, d => Assert.Contains("HIKVISION", d.Scope));
    }

    [Fact]
    public void IgnoresNonProbeMatchMessagesAndUnsafeServiceUrl()
    {
        var text = """
          <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
          xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <s:Body><d:ProbeMatches><d:ProbeMatch><d:XAddrs>
          ftp://192.168.1.64/file http://operator:secret@192.168.1.64/onvif/device_service
          </d:XAddrs></d:ProbeMatch></d:ProbeMatches></s:Body></s:Envelope>
          """;
        Assert.Empty(OnvifDiscovery.ParseProbeMatches(text));
    }

    [Fact]
    public void DiscoveryXmlCannotUseExternalEntities()
    {
        string malicious = """
          <!DOCTYPE s:Envelope [<!ENTITY external SYSTEM "file:///c:/windows/win.ini">]>
          <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">&external;</s:Envelope>
          """;
        Assert.Throws<XmlException>(() => OnvifDiscovery.ParseProbeMatches(malicious));
    }

    [Fact]
    public void BareRtspPort554IsRejectedBeforeOnvifAuthentication()
    {
        string error = OnvifCameraSettings.ValidateEndpoint("http://192.168.1.64:554");
        Assert.Contains("554", error);
        Assert.Contains("/onvif/device_service", error);
        Assert.Equal("", OnvifCameraSettings.ValidateEndpoint(
            "http://192.168.1.64/onvif/device_service"));
    }
}
