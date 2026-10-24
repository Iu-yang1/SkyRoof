using System;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Local RC-3040S management on ws://host:60030/ only. Completely independent
/// from the TCP/60020 motor I/O worker and the remote NRL2 UDP transport.
/// Every method opens a bounded one-shot session; nothing runs on UI timers.
/// </summary>
internal static class Rc3040sManagementService
{
  internal sealed record Snapshot(string? Firmware, int? AzRaw, int? ElRaw,
    string? Mode, string? Travel, int Frames, bool EchoVerified,
    string? DeviceIdSuffix = null);

  private static readonly Regex Banner = new(
    @"RC_3040S_67S_8H_\d{6}_[A-Za-z0-9]+",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
  private static readonly Regex FirmwareId = new(
    @"RC_3040S_67S_8H_\d{6}_[A-Za-z0-9]+\s+([0-9A-Fa-f]{14})",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
  private static readonly Regex Speed = new(
    @"(?im)^\s*AT\+(AZ|EL)_SPD=(\d{5})\s*$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
  private static readonly SemaphoreSlim Exclusive = new(1, 1);
  private const int MaxFrames = 40, MaxChars = 16384;

  internal static bool TryParse(string data, int frames, bool echo, out Snapshot snapshot)
  {
    snapshot = new Snapshot(null, null, null, null, null, frames, echo);
    if(data.Length > MaxChars || frames > MaxFrames) return false;
    var banner = Banner.Match(data);
    int? az=null, el=null;
    foreach(Match speed in Speed.Matches(data))
    {
      if(!int.TryParse(speed.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,
        out int value) || value is <15 or >65535) continue;
      if(speed.Groups[1].Value.Equals("AZ",StringComparison.OrdinalIgnoreCase)) az=value;
      else el=value;
    }
    string? model=Regex.Match(data,@"(?im)^AT\+YT=([A-Z0-9]{1,12})\s*$") is var m && m.Success
      ? m.Groups[1].Value : null;
    string? travel=Regex.Match(data,@"(?im)^AT\+(P450|P360)\s*$") is var t && t.Success
      ? t.Groups[1].Value : null;
    var deviceId=FirmwareId.Match(data);
    snapshot=new Snapshot(banner.Success?banner.Value:null,az,el,model,travel,frames,echo,
      deviceId.Success ? deviceId.Groups[1].Value[^4..].ToUpperInvariant() : null);
    return snapshot.Firmware!=null && data.Contains("NET SETUP",StringComparison.OrdinalIgnoreCase);
  }

  private static void ValidateHost(string host)
  {
    if(!IPAddress.TryParse(host,out var addr) || addr.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
       IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.Broadcast))
      throw new ArgumentException("RC-3040S LAN management requires a literal controller IPv4 address.");
  }
  private static async Task<ClientWebSocket> ConnectAsync(string host,CancellationToken token)
  {
    ValidateHost(host);
    var socket=new ClientWebSocket();
    // Never route private management credentials to a configured HTTP proxy.
    socket.Options.Proxy=new WebProxy();
    socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(15);
    try
    {
      await socket.ConnectAsync(new Uri($"ws://{host}:60030/"),token).ConfigureAwait(false);
      return socket;
    }
    catch {socket.Dispose();throw;}
  }
  private static async Task SendAsync(ClientWebSocket ws,string cmd,CancellationToken token)
  {
    await ws.SendAsync(Encoding.ASCII.GetBytes(cmd),WebSocketMessageType.Text,true,token)
      .ConfigureAwait(false);
  }
  // Embedded RC-3040S devices may pause between WebSocket frames. An ordinary
  // ReceiveAsync cancellation can abort the ClientWebSocket and break the next
  // AT+TEST request. Keep exactly one in-flight receive per socket and apply
  // idle deadlines ONLY to the waiter, not to the transport's receive task.
  private sealed class FrameReader
  {
    private readonly ClientWebSocket socket;
    private readonly byte[] buffer = new byte[2048];
    private Task<ValueWebSocketReceiveResult>? pending;
    internal FrameReader(ClientWebSocket ws) => socket=ws;

    internal async Task<string?> ReadMessageAsync(TimeSpan timeout,CancellationToken token)
    {
      var chunk=new StringBuilder();
      while(true)
      {
        pending ??= socket.ReceiveAsync(buffer.AsMemory(),token).AsTask();
        ValueWebSocketReceiveResult part;
        try { part=await pending.WaitAsync(timeout,token).ConfigureAwait(false); }
        catch(TimeoutException) { return null; }
        pending=null;
        if(part.MessageType==WebSocketMessageType.Close)return null;
        if(part.MessageType!=WebSocketMessageType.Text)
          throw new InvalidDataException("Unexpected binary management frame.");
        chunk.Append(Encoding.UTF8.GetString(buffer,0,part.Count));
        if(chunk.Length>4096)throw new InvalidDataException("Management frame exceeded 4096 characters");
        if(part.EndOfMessage)return chunk.ToString();
      }
    }
  }

  private static async Task<(string Text,int Frames,bool Echo)> CollectAsync(
    FrameReader reader,string? expectedEcho,CancellationToken token)
  {
    var all=new StringBuilder();int frames=0;bool echo=false;
    // First frame 3s, subsequent inter-frame gap 850ms. This is a bounded
    // heuristic only, NOT a firmware transaction completion marker.
    while(frames<MaxFrames && all.Length<MaxChars)
    {
      var message=await reader.ReadMessageAsync(
        frames==0?TimeSpan.FromSeconds(3):TimeSpan.FromMilliseconds(850),
        token).ConfigureAwait(false);
      if(message is null)break;
      if(expectedEcho!=null && message.TrimEnd('\r','\n')==expectedEcho)echo=true;
      all.Append(message);frames++;
    }
    if(all.Length>=MaxChars || frames>=MaxFrames)
      throw new InvalidDataException("Management dump exceeded safe limits.");
    return (all.ToString(),frames,echo);
  }

  private static async Task<Snapshot> QueryCoreAsync(
    ClientWebSocket socket,bool includeEcho,CancellationToken token)
  {
    // C2 was confirmed read-only by 260223_C hardware.
    var reader=new FrameReader(socket);
    await SendAsync(socket,"C2\r",token).ConfigureAwait(false);
    var (data,n,_) = await CollectAsync(reader,null,token).ConfigureAwait(false);
    if(!TryParse(data,n,false,out var snapshot))
      throw new InvalidDataException("Incomplete RC-3040S C2 management snapshot.");
    if(snapshot.AzRaw.HasValue && snapshot.ElRaw.HasValue || !includeEcho)
      return snapshot;
    // Explicitly selected diagnostic probe only. 260223_C echoed TEST_01
    // and then emitted a delayed YUNTAI block. This behavior is not a query ACK.
    const string echo="SKYROOF_READ";
    await SendAsync(socket,"AT+TEST="+echo+"\r",token).ConfigureAwait(false);
    var (extra,frames,matched)=await CollectAsync(reader,echo,token).ConfigureAwait(false);
    if(!matched || !TryParse(data+extra,n+frames,true,out snapshot))
      throw new InvalidDataException("Diagnostic echo mismatch or incomplete configuration dump.");
    return snapshot;
  }
  private static async Task<T> WithSessionAsync<T>(string host,
    Func<ClientWebSocket,CancellationToken,Task<T>> work,CancellationToken token)
  {
    await Exclusive.WaitAsync(token).ConfigureAwait(false);
    try
    {
      using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
      deadline.CancelAfter(TimeSpan.FromSeconds(14));
      using var socket=await ConnectAsync(host,deadline.Token).ConfigureAwait(false);
      try {return await work(socket,deadline.Token).ConfigureAwait(false);}
      finally
      {
        if(socket.State==WebSocketState.Open)
        {
          using var close=new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
          try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"done",close.Token).ConfigureAwait(false); }
          catch(Exception ex) when(ex is WebSocketException or OperationCanceledException or InvalidOperationException)
          { socket.Abort(); }
        }
      }
    }
    finally {Exclusive.Release();}
  }

  internal static Task<Snapshot> ReadAsync(string host,bool detailed,CancellationToken token=default) =>
    WithSessionAsync(host,(ws,ct)=>QueryCoreAsync(ws,detailed,ct),token);

  /// <summary>
  /// Sends one explicitly approved speed configuration AT command, and performs a
  /// separate on-demand C2 readback afterward. No automatic retries or rollback:
  /// interrupted EEPROM writes have indeterminate result.
  /// </summary>
  internal static async Task<(Snapshot Before,Snapshot After)> SaveSpeedAsync(
    string host,bool azimuth,int desired,CancellationToken token=default)
  {
    if(desired is <15 or >65535)throw new ArgumentOutOfRangeException(nameof(desired));
    var original=await ReadAsync(host,true,token).ConfigureAwait(false);
    int? existing=azimuth?original.AzRaw:original.ElRaw;
    if(existing is null)throw new InvalidOperationException("Current raw speed not read: write blocked.");
    if(existing==desired)return (original,original);
    var sent=await WithSessionAsync(host,async(ws,ct)=>{
      var check=await QueryCoreAsync(ws,false,ct).ConfigureAwait(false);
      if(check.Firmware!=original.Firmware ||
         original.DeviceIdSuffix is null ||
         check.DeviceIdSuffix!=original.DeviceIdSuffix)
        throw new InvalidOperationException("Controller firmware changed: speed write blocked.");
      string command=$"AT+{(azimuth?"AZ":"EL")}_SPD={desired:00000}\r";
      await SendAsync(ws,command,ct).ConfigureAwait(false);
      return true;
    },token).ConfigureAwait(false);
    _=sent;
    // A post-write read is not EEPROM validation. If timing uncertain, report
    // failure rather than issuing any automatic retry/compensating write.
    var after=await ReadAsync(host,true,token).ConfigureAwait(false);
    if((azimuth?after.AzRaw:after.ElRaw)!=desired)
      throw new InvalidDataException("Readback did not match desired raw speed. No retry attempted.");
    return (original,after);
  }

  internal static Task RestartAsync(string host,string expectedFirmware,
    string expectedDeviceIdSuffix,CancellationToken token=default)=>
    WithSessionAsync(host,async(ws,ct)=>{
      var configuration=await QueryCoreAsync(ws,false,ct).ConfigureAwait(false);
      if(configuration.Firmware!=expectedFirmware ||
         configuration.DeviceIdSuffix is null ||
         !string.Equals(configuration.DeviceIdSuffix,expectedDeviceIdSuffix,StringComparison.Ordinal))
        throw new InvalidOperationException("Controller firmware/device changed: restart blocked.");
      await SendAsync(ws,"AT+RESET\r",ct).ConfigureAwait(false);
      return true; // Network send only; reboot confirmation is not provided.
    },token);
}
