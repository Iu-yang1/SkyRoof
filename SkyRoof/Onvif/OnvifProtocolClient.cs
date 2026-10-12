using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SkyRoof
{
  internal sealed record OnvifCameraDevice(string Endpoint, string Scope);
  internal sealed record OnvifMediaProfile(string Token, string Name, bool Media2)
  {
    public override string ToString() => Name + (Media2 ? " (Media2)" : " (Media1)");
  }

  /// <summary>
  /// Standards-based ONVIF WS-Discovery Probe (SOAP 1.2, UDP multicast 3702).
  /// ProbeMatch XAddrs are only displayed: users explicitly choose which
  /// discovered endpoint to contact, preventing unsolicited credential posts.
  /// </summary>
  internal sealed record OnvifDiscoveryResult(
    IReadOnlyList<OnvifCameraDevice> Devices, int InterfacesProbed,
    int ResponsePackets);

  /// <summary>
  /// Probe every active IPv4 network interface, not only the OS default
  /// multicast route. Replies are UDP unicasts to each probe socket's
  /// ephemeral source port; the same socket must receive those replies.
  /// </summary>
  internal static class OnvifDiscovery
  {
    private static readonly IPEndPoint MulticastEndpoint =
      new(IPAddress.Parse("239.255.255.250"), 3702);

    internal static string BuildProbeXml(bool filterByDeviceType)
    {
      XNamespace env = "http://www.w3.org/2003/05/soap-envelope";
      XNamespace address = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
      XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
      XNamespace network = "http://www.onvif.org/ver10/network/wsdl";

      // The second, unfiltered Probe is a WS-Discovery compatibility fallback
      // for cameras that do not advertise the NetworkVideoTransmitter type.
      var body = new XElement(discovery + "Probe");
      if (filterByDeviceType)
        body.Add(new XElement(discovery + "Types", "dn:NetworkVideoTransmitter"));

      return new XDocument(new XElement(env + "Envelope",
        new XAttribute(XNamespace.Xmlns + "a", address),
        new XAttribute(XNamespace.Xmlns + "d", discovery),
        new XAttribute(XNamespace.Xmlns + "dn", network),
        new XElement(env + "Header",
          new XElement(address + "MessageID", "urn:uuid:" + Guid.NewGuid()),
          new XElement(address + "To",
            "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
          new XElement(address + "Action",
            "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
        new XElement(env + "Body", body))).ToString(SaveOptions.DisableFormatting);
    }

    internal static IReadOnlyList<OnvifCameraDevice> ParseProbeMatches(
      string xmlText)
    {
      // Multicast packets are untrusted data. Reject DTDs and cap their size.
      using var stringReader = new StringReader(xmlText);
      using var xml = System.Xml.XmlReader.Create(stringReader,
        new System.Xml.XmlReaderSettings
        {
          DtdProcessing = System.Xml.DtdProcessing.Prohibit,
          XmlResolver = null,
          MaxCharactersInDocument = 65536
        });
      XDocument document = XDocument.Load(xml);
      var devices = new List<OnvifCameraDevice>();
      XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
      foreach (XElement match in document.Descendants(discovery + "ProbeMatch"))
      {
        string scopes = match.Element(discovery + "Scopes")?.Value ?? "";
        string addresses = match.Element(discovery + "XAddrs")?.Value ?? "";
        foreach (string candidate in addresses.Split(
          (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
          if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? endpoint) &&
              endpoint.Scheme is "http" or "https" &&
              string.IsNullOrEmpty(endpoint.UserInfo))
            devices.Add(new OnvifCameraDevice(endpoint.AbsoluteUri, scopes));
        }
      }
      return devices;
    }

    internal static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
      var addresses = new List<IPAddress>();
      foreach (var adapter in
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
      {
        if (adapter.OperationalStatus !=
            System.Net.NetworkInformation.OperationalStatus.Up ||
            adapter.NetworkInterfaceType is
              System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
              System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
          continue;

        foreach (var address in adapter.GetIPProperties().UnicastAddresses)
        {
          IPAddress ip = address.Address;
          if (ip.AddressFamily == AddressFamily.InterNetwork &&
              !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) &&
              !addresses.Contains(ip))
            addresses.Add(ip);
        }
      }
      // On unusual adapters, let Windows select a multicast route as a final
      // fallback instead of making discovery silently unavailable.
      if (addresses.Count == 0) addresses.Add(IPAddress.Any);
      return addresses;
    }

    internal static async Task<IReadOnlyList<OnvifCameraDevice>> ProbeAsync(
      CancellationToken cancellationToken) =>
      (await ProbeWithDiagnosticsAsync(cancellationToken)).Devices;

    internal static async Task<OnvifDiscoveryResult> ProbeWithDiagnosticsAsync(
      CancellationToken cancellationToken)
    {
      var interfaces = GetLocalIPv4Addresses();
      var attempts = await Task.WhenAll(interfaces.Select(
        ip => ProbeInterfaceAsync(ip, cancellationToken)));
      cancellationToken.ThrowIfCancellationRequested();

      var devices = attempts.SelectMany(a => a.Devices)
        .GroupBy(d => d.Endpoint, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First()).ToArray();
      return new OnvifDiscoveryResult(devices,
        attempts.Count(a => a.Sent), attempts.Sum(a => a.Packets));
    }

    private sealed record InterfaceResult(
      bool Sent, int Packets, IReadOnlyList<OnvifCameraDevice> Devices);

    private static async Task<InterfaceResult> ProbeInterfaceAsync(
      IPAddress localIPv4, CancellationToken cancellationToken)
    {
      var devices = new List<OnvifCameraDevice>();
      int packets = 0;
      bool sent = false;
      try
      {
        // A separate bound socket per local IPv4 interface is essential on a
        // multi-homed PC (Wi-Fi/Ethernet/VPN/virtual adapters).
        using var udp = new UdpClient(new IPEndPoint(localIPv4, 0));
        udp.MulticastLoopback = false;
        udp.Ttl = 2;
        if (!localIPv4.Equals(IPAddress.Any))
          udp.Client.SetSocketOption(SocketOptionLevel.IP,
            SocketOptionName.MulticastInterface, localIPv4.GetAddressBytes());

        // Use independent message IDs for typed and untyped probes.
        foreach (bool filtered in new[] { true, false })
        {
          byte[] bytes = Encoding.UTF8.GetBytes(BuildProbeXml(filtered));
          await udp.SendAsync(bytes, bytes.Length, MulticastEndpoint);
          sent = true;
          await Task.Delay(100, cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(4500));
        while (!timeout.IsCancellationRequested)
        {
          UdpReceiveResult datagram;
          try { datagram = await udp.ReceiveAsync(timeout.Token); }
          catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
          { break; }
          packets++;
          try
          {
            // Decode only discovery XML; never attempt a device login here.
            devices.AddRange(ParseProbeMatches(
              Encoding.UTF8.GetString(datagram.Buffer)));
          }
          catch (System.Xml.XmlException) { }
        }
      }
      catch (SocketException) when (!cancellationToken.IsCancellationRequested)
      {
        // A VPN/virtual adapter may reject multicast. Other interfaces still
        // perform discovery, and the UI reports attempted interface count.
      }
      catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
      return new InterfaceResult(sent, packets, devices);
    }
  }

  /// <summary>
  /// ONVIF Device Service + Media1/Media2 SOAP client. WS-Security UsernameToken
  /// PasswordDigest (nonce + UTC created + password) and HTTP Digest/Basic are
  /// supported. Credentials are never logged or persisted.
  /// </summary>
  internal sealed class OnvifProtocolClient : IDisposable
  {
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private const string Media1Ns = "http://www.onvif.org/ver10/media/wsdl";
    private const string Media2Ns = "http://www.onvif.org/ver20/media/wsdl";
    private const string TypesNs = "http://www.onvif.org/ver10/schema";
    private const string WssNs =
      "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string WsuNs =
      "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private readonly HttpClient http;
    private readonly Uri deviceEndpoint;
    private readonly string username;
    private readonly string password;
    private readonly Dictionary<bool, Uri> mediaEndpoints = new();

    internal OnvifProtocolClient(string endpoint, string username, string password)
    {
      if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? address) ||
          address.Scheme is not ("http" or "https"))
        throw new ArgumentException("ONVIF device endpoint must be an HTTP(S) URL.");
      deviceEndpoint = address;
      this.username = username;
      this.password = password;
      var handler = new HttpClientHandler
      {
        AllowAutoRedirect = false,
        Credentials = string.IsNullOrEmpty(username) ? null :
          new NetworkCredential(username, password),
        PreAuthenticate = true
      };
      http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    internal async Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(
      CancellationToken cancel)
    {
      await ResolveServicesAsync(cancel);
      var profiles = new List<OnvifMediaProfile>();
      foreach (bool media2 in new[] { true, false })
      {
        if (!mediaEndpoints.TryGetValue(media2, out Uri? endpoint)) continue;
        try
        {
          string ns = media2 ? Media2Ns : Media1Ns;
          XNamespace service = ns;
          XDocument response = await SoapAsync(endpoint, ns, "GetProfiles",
            new XElement(service + "GetProfiles"), cancel);
          foreach (XElement element in response.Descendants().Where(
            x => x.Name.LocalName == "Profiles"))
          {
            string? token = element.Attribute("token")?.Value;
            if (string.IsNullOrWhiteSpace(token)) continue;
            string name = element.Elements().FirstOrDefault(x => x.Name.LocalName == "Name")?.Value
              ?? token;
            profiles.Add(new OnvifMediaProfile(token, name, media2));
          }
        }
        catch (HttpRequestException) when (
          (media2 && mediaEndpoints.ContainsKey(false)) ||
          (!media2 && profiles.Count > 0))
        {
          // An ONVIF device may advertise Media2 yet implement only Media1 operations.
        }
      }
      if (profiles.Count == 0)
        throw new InvalidOperationException("The ONVIF device returned no compatible Media1/Media2 profiles.");
      return profiles;
    }

    internal async Task<Uri> GetStreamUriAsync(
      OnvifMediaProfile profile, CancellationToken cancel)
    {
      if (!mediaEndpoints.TryGetValue(profile.Media2, out Uri? endpoint))
        await ResolveServicesAsync(cancel);
      if (!mediaEndpoints.TryGetValue(profile.Media2, out endpoint))
        throw new InvalidOperationException("Selected ONVIF media service is unavailable.");

      string ns = profile.Media2 ? Media2Ns : Media1Ns;
      XNamespace svc = ns;
      XNamespace tt = TypesNs;
      XElement request = BuildStreamUriRequest(profile);

      XDocument response = await SoapAsync(endpoint!, ns, "GetStreamUri", request, cancel);
      string? text = response.Descendants().FirstOrDefault(
        x => x.Name.LocalName == "Uri")?.Value;
      if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
          uri.Scheme is not ("rtsp" or "rtsps"))
        throw new InvalidOperationException("Camera did not return a valid RTSP stream URI.");
      return uri;
    }

    /// <summary>
    /// Media1 and Media2 GetStreamUri have different wire schemas. Kept as
    /// a separate deterministic builder to make protocol regressions testable.
    /// </summary>
    internal static XElement BuildStreamUriRequest(OnvifMediaProfile profile)
    {
      XNamespace svc = profile.Media2 ? Media2Ns : Media1Ns;
      XNamespace tt = TypesNs;
      if (profile.Media2)
        return new XElement(svc + "GetStreamUri",
          new XElement(svc + "Protocol", "RTSP"),
          new XElement(svc + "ProfileToken", profile.Token));

      return new XElement(svc + "GetStreamUri",
        new XElement(svc + "StreamSetup",
          new XElement(tt + "Stream", "RTP-Unicast"),
          new XElement(tt + "Transport",
            new XElement(tt + "Protocol", "RTSP"))),
        new XElement(svc + "ProfileToken", profile.Token));
    }

    private async Task ResolveServicesAsync(CancellationToken cancel)
    {
      if (mediaEndpoints.Count > 0) return;
      XNamespace device = DeviceNs;
      try
      {
        XDocument response = await SoapAsync(deviceEndpoint, DeviceNs,
          "GetServices", new XElement(device + "GetServices",
            new XElement(device + "IncludeCapability", false)), cancel);
        foreach (XElement service in response.Descendants().Where(
          x => x.Name.LocalName == "Service"))
        {
          string? ns = service.Elements().FirstOrDefault(
            x => x.Name.LocalName == "Namespace")?.Value;
          string? addr = service.Elements().FirstOrDefault(
            x => x.Name.LocalName == "XAddr")?.Value;
          if (ns != Media1Ns && ns != Media2Ns) continue;
          if (TryServiceUri(addr, out Uri? uri))
            mediaEndpoints[ns == Media2Ns] = uri!;
        }
      }
      catch (HttpRequestException)
      {
        // Older devices may support GetCapabilities but not GetServices.
      }

      if (!mediaEndpoints.ContainsKey(false))
      {
        try
        {
        XDocument response = await SoapAsync(deviceEndpoint, DeviceNs,
          "GetCapabilities", new XElement(device + "GetCapabilities",
            new XElement(device + "Category", "Media")), cancel);
        XElement? media = response.Descendants().FirstOrDefault(
          x => x.Name.LocalName == "Media");
        string? addr = media?.Elements().FirstOrDefault(
          x => x.Name.LocalName == "XAddr")?.Value;
        if (TryServiceUri(addr, out Uri? uri))
          mediaEndpoints[false] = uri!;
        }
        catch (HttpRequestException) when (mediaEndpoints.ContainsKey(true))
        {
          // Media2-only cameras can legitimately reject legacy GetCapabilities.
        }
      }

      if (mediaEndpoints.Count == 0)
        throw new InvalidOperationException("ONVIF device does not advertise a compatible Media service.");
    }

    private static bool TryServiceUri(string? text, out Uri? uri)
    {
      bool good = Uri.TryCreate(text, UriKind.Absolute, out uri)
        && uri.Scheme is "http" or "https";
      if (!good) uri = null;
      return good;
    }

    private async Task<XDocument> SoapAsync(Uri endpoint, string ns,
      string method, XElement payload, CancellationToken cancel)
    {
      XNamespace env = "http://www.w3.org/2003/05/soap-envelope";
      XNamespace wsse = WssNs;
      XNamespace wsu = WsuNs;
      XElement header = new(env + "Header");
      if (!string.IsNullOrEmpty(username))
      {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
          System.Globalization.CultureInfo.InvariantCulture);
        byte[] suffix = Encoding.UTF8.GetBytes(created + password);
        byte[] input = new byte[nonce.Length + suffix.Length];
        nonce.CopyTo(input, 0);
        suffix.CopyTo(input, nonce.Length);
        string digest = Convert.ToBase64String(SHA1.HashData(input));
        header.Add(new XElement(wsse + "Security",
          new XAttribute(env + "mustUnderstand", "1"),
          new XElement(wsse + "UsernameToken",
            new XElement(wsse + "Username", username),
            new XElement(wsse + "Password",
              new XAttribute("Type",
                "http://docs.oasis-open.org/wss/2004/01/" +
                "oasis-200401-wss-username-token-profile-1.0#PasswordDigest"), digest),
            new XElement(wsse + "Nonce",
              new XAttribute("EncodingType",
                "http://docs.oasis-open.org/wss/2004/01/" +
                "oasis-200401-wss-soap-message-security-1.0#Base64Binary"),
              Convert.ToBase64String(nonce)),
            new XElement(wsu + "Created", created))));
      }

      XDocument envelope = new(new XElement(env + "Envelope",
        new XAttribute(XNamespace.Xmlns + "s", env),
        new XAttribute(XNamespace.Xmlns + "wsse", wsse),
        new XAttribute(XNamespace.Xmlns + "wsu", wsu),
        header, new XElement(env + "Body", payload)));
      using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
      request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting),
        Encoding.UTF8, "application/soap+xml");
      request.Content.Headers.ContentType!.Parameters.Add(
        new System.Net.Http.Headers.NameValueHeaderValue("action",
          "\""+ ns + "/" + method + "\""));
      using HttpResponseMessage reply = await http.SendAsync(request, cancel);
      string xml = await reply.Content.ReadAsStringAsync(cancel);
      if (!reply.IsSuccessStatusCode)
        throw new HttpRequestException(
          $"ONVIF {method} failed: HTTP {(int)reply.StatusCode}. Check credentials and camera services.");
      XDocument document = XDocument.Parse(xml);
      XElement? fault = document.Descendants().FirstOrDefault(
        x => x.Name.LocalName == "Fault");
      if (fault != null)
        throw new InvalidOperationException($"ONVIF {method} SOAP fault.");
      return document;
    }

    public void Dispose() => http.Dispose();
  }
}
