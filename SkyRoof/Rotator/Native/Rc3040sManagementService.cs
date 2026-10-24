using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

  internal enum SpeedWriteOutcome { Applied, Unchanged, ReadbackUnavailable, EchoOnly, Failed }
  internal sealed record SpeedWriteResult(Snapshot Before, Snapshot? After,
    SpeedWriteOutcome Outcome, int RequestedRaw, bool ExactTextEcho, string Detail,
    bool CommandAttempted = false);

  // Numeric conversion constants recovered from the 251217 software diagnostic.
  // Display is nominal speed; real calibration belongs to hardware acceptance.
  internal static class SpeedUnits
  {
    private const double AzDivisor=361600.0;
    private const double ElDivisor=326400.0;
    internal static double ToDegreesPerSecond(bool azimuth,int raw) =>
      raw*360.0/(azimuth?AzDivisor:ElDivisor);
    internal static int ToRaw(bool azimuth,double degreesPerSecond)
    {
      if(!double.IsFinite(degreesPerSecond)||degreesPerSecond<=0)
        throw new ArgumentOutOfRangeException(nameof(degreesPerSecond));
      return checked((int)Math.Round(degreesPerSecond*
        (azimuth?AzDivisor:ElDivisor)/360.0,MidpointRounding.AwayFromZero));
    }
    internal static int SliderTick(bool azimuth,int raw) =>
      (int)Math.Round(ToDegreesPerSecond(azimuth,raw)*100.0,MidpointRounding.AwayFromZero);
    internal static int SliderMaximum(bool azimuth,int maxRaw) =>
      (int)Math.Floor(ToDegreesPerSecond(azimuth,maxRaw)*100.0);
    internal static int RawFromSlider(bool azimuth,int tick,int minRaw,int maxRaw) =>
      Math.Clamp(ToRaw(azimuth,tick/100.0),minRaw,maxRaw);
    internal static bool SliderChanged(bool azimuth,int tick,int originalRaw) =>
      tick!=SliderTick(azimuth,originalRaw);
    internal static string Display(bool azimuth,int raw) =>
      ToDegreesPerSecond(azimuth,raw).ToString("0.00",CultureInfo.InvariantCulture)+" °/s";
  }

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
  private static DateTime nextSessionUtc = DateTime.MinValue;
  private static readonly TimeSpan SessionCooldown = TimeSpan.FromMilliseconds(1200);
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
    ClientWebSocket socket,bool includeEcho,CancellationToken token,
    FrameReader? existingReader=null)
  {
    // C2 was confirmed read-only by 260223_C hardware.
    var reader=existingReader??new FrameReader(socket);
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
      // Pace sessions on the constrained Socket0 listener without retrying writes.
      var pause=nextSessionUtc-DateTime.UtcNow;
      if(pause>TimeSpan.Zero)await Task.Delay(pause,token).ConfigureAwait(false);
      using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
      deadline.CancelAfter(TimeSpan.FromSeconds(22));
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
    finally
    {
      nextSessionUtc=DateTime.UtcNow+SessionCooldown;
      Exclusive.Release();
    }
  }

  internal static Task<Snapshot> ReadAsync(string host,bool detailed,CancellationToken token=default) =>
    WithSessionAsync(host,(ws,ct)=>QueryCoreAsync(ws,detailed,ct),token);

  /// <summary>
  /// One connection per Save: fresh C2 identity/speed preflight, one AT write,
  /// then a separate C2 readback request in the SAME WebSocket session.
  /// Readback may reflect volatile state and does not prove EEPROM retention.
  /// </summary>
  internal static SpeedWriteOutcome ClassifySpeedWrite(
    int before,int requested,int? observed,bool exactEcho)
  {
    if(observed is null)return SpeedWriteOutcome.ReadbackUnavailable;
    if(observed==requested && observed!=before)return SpeedWriteOutcome.Applied;
    if(observed==before)
      return exactEcho?SpeedWriteOutcome.EchoOnly:SpeedWriteOutcome.Unchanged;
    return SpeedWriteOutcome.Failed;
  }

  internal static string SpeedCommand(bool azimuth,int raw)
  {
    if(raw is <15 or >10000)throw new ArgumentOutOfRangeException(nameof(raw));
    return $"AT+{(azimuth?"AZ":"EL")}_SPD={raw:D5}\r";
  }

  private static string FailureReason(Exception ex)
  {
    var socket=ex as SocketException ?? ex.InnerException as SocketException ??
      ex.InnerException?.InnerException as SocketException;
    if(socket!=null)return $"TCP {socket.SocketErrorCode} ({socket.NativeErrorCode})";
    return ex switch
    {
      WebSocketException => "WebSocket connection or transport failure",
      OperationCanceledException => "Management session deadline exceeded",
      TimeoutException => "Management reply timeout",
      InvalidDataException => "Incomplete management configuration",
      _ => ex.GetType().Name
    };
  }

  internal static async Task<SpeedWriteResult> SaveSpeedAsync(
    string host,bool azimuth,int desired,CancellationToken token=default,
    Action? onCommandAttempt=null,string? expectedFirmware=null,
    string? expectedIdSuffix=null)
  {
    string command=SpeedCommand(azimuth,desired);
    Snapshot original=new(null,null,null,null,null,0,false);
    bool attempted=false,exactEcho=false;
    try
    {
      return await WithSessionAsync(host,async(ws,ct)=>
      {
        // Read fresh device state. A previously cached UI snapshot cannot
        // authorize the write, even if the same IP is being used.
        var reader=new FrameReader(ws);
        original=await QueryCoreAsync(ws,true,ct,reader).ConfigureAwait(false);
        int? before=azimuth?original.AzRaw:original.ElRaw;
        if(before is null)
          return new SpeedWriteResult(original,null,SpeedWriteOutcome.Failed,
            desired,false,"Preflight speed field absent; AT command NOT sent.");
        if(before==desired)
          return new SpeedWriteResult(original,original,SpeedWriteOutcome.Unchanged,
            desired,false,"Already at requested speed; AT command NOT sent.");
        if(original.Firmware is null || original.DeviceIdSuffix is null)
          return new SpeedWriteResult(original,null,SpeedWriteOutcome.Failed,
            desired,false,"Preflight identity incomplete; AT command NOT sent.");
        if(expectedFirmware!=null && original.Firmware!=expectedFirmware ||
           expectedIdSuffix!=null && original.DeviceIdSuffix!=expectedIdSuffix)
          return new SpeedWriteResult(original,null,SpeedWriteOutcome.Failed,
            desired,false,"Preflight device differs from earlier UI read; AT command NOT sent.");

        // Mark the safety latch BEFORE the potentially transmitted SendAsync.
        // Cancellation/disposal after this point must not re-enable motion.
        attempted=true;
        onCommandAttempt?.Invoke();
        await SendAsync(ws,command,ct).ConfigureAwait(false);

        // Do not open another TCP socket for verification. The device may
        // return a delayed configuration dump, not an explicit AT ACK.
        var (response,_,_)=await CollectAsync(reader,null,ct).ConfigureAwait(false);
        exactEcho=response.Split('\r','\n',StringSplitOptions.RemoveEmptyEntries)
          .Any(line=>line.Trim()==command.TrimEnd('\r'));
        var verified=await QueryCoreAsync(ws,true,ct,reader).ConfigureAwait(false);
        if(verified.Firmware!=original.Firmware ||
           verified.DeviceIdSuffix!=original.DeviceIdSuffix)
          return new SpeedWriteResult(original,verified,SpeedWriteOutcome.Failed,
            desired,exactEcho,"Readback device identity changed; inspect controller.",true);
        int? observed=azimuth?verified.AzRaw:verified.ElRaw;
        var outcome=ClassifySpeedWrite(before.Value,desired,observed,exactEcho);
        var detail=outcome switch
        {
          SpeedWriteOutcome.Applied => "Fresh C2 readback reports updated speed; persistence unverified.",
          SpeedWriteOutcome.Unchanged => "Fresh C2 readback reports the original speed.",
          SpeedWriteOutcome.EchoOnly => "Echo observed but fresh readback reports original speed.",
          SpeedWriteOutcome.ReadbackUnavailable => "Write attempted; speed field absent from fresh readback.",
          _ => "Fresh readback differs from original and requested speed."
        };
        return new SpeedWriteResult(original,verified,outcome,desired,exactEcho,detail,true);
      },token).ConfigureAwait(false);
    }
    catch(OperationCanceledException) when(token.IsCancellationRequested)
    {
      throw; // No assumption about wire transmission after user cancellation.
    }
    catch(Exception ex) when(ex is WebSocketException or IOException or
      OperationCanceledException or TimeoutException or HttpRequestException or
      ArgumentException or InvalidOperationException)
    {
      var reason=FailureReason(ex);
      return new SpeedWriteResult(original,null,
        attempted?SpeedWriteOutcome.ReadbackUnavailable:SpeedWriteOutcome.Failed,
        desired,exactEcho,attempted
          ? $"AT may have been sent; verification failed: {reason}. Do not retry."
          : $"Management preflight failed: {reason}. AT command NOT sent.",
        attempted);
    }
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
