using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Net;

namespace SkyRoof.NativeRotator;

public interface IRotatorTransport : IDisposable
{
  void Open();
  bool Connected { get; }
  void Write(ReadOnlySpan<byte> bytes);
  int Read(byte[] buffer);
  void Abort();
}
public static class NativeTransportFactory
{
  public static IRotatorTransport Create(RotatorSettings options) =>
    IsolatedRotatorTransport.ShouldIsolateThisProcess() &&
    !RotatorEngineSubprocess.InChildEngine
      ? new IsolatedRotatorTransport(options) : CreateDirect(options);

  // The child alone owns OS serial/TCP/UDP handles. Tests hosted by
  // testhost.exe retain the direct fake transport to test motion semantics.
  internal static IRotatorTransport CreateDirect(RotatorSettings options) =>
    options.NativeTransport switch
  {
    RotatorTransportKind.Serial => new SerialTransport(options),
    RotatorTransportKind.Tcp => new TcpTransport(options),
    RotatorTransportKind.Udp => new UdpTransport(options),
    _ => throw new NotSupportedException("Unsupported native transport")
  };
}
internal sealed class SerialTransport : IRotatorTransport
{
  private readonly RotatorSettings options;
  private SerialPort? serial;
  internal SerialTransport(RotatorSettings options)=>this.options=options;
  public bool Connected => serial?.IsOpen==true;
  public void Open()
  {
    if(options.NativeDataBits is < 5 or > 8)
      throw new ArgumentOutOfRangeException(nameof(options.NativeDataBits),"Serial data bits must be 5 to 8");
    if(options.NativeBaudRate<=0)
      throw new ArgumentOutOfRangeException(nameof(options.NativeBaudRate));
    var p=new SerialPort(options.NativeSerialPort,options.NativeBaudRate,
      (Parity)options.NativeParity,options.NativeDataBits,(StopBits)options.NativeStopBits)
    {Handshake=(Handshake)options.NativeHandshake,ReadTimeout=Math.Clamp(options.NativeReadTimeout,100,250),
      WriteTimeout=Math.Clamp(options.SendTimeout,100,30000),DtrEnable=options.NativeDtrEnable};
    try
    {
      // RTS is controlled by the handshake driver when hardware RTS/CTS
      // flow control is selected. Setting RtsEnable in that mode throws.
      if(options.NativeHandshake is not (RotatorSerialHandshake.RequestToSend or RotatorSerialHandshake.RequestToSendXOnXOff))
        p.RtsEnable=options.NativeRtsEnable;
      p.Open();
      serial=p;
    }
    catch {p.Dispose();throw;}
  }
  public void Write(ReadOnlySpan<byte> bytes)
  {
    var p=serial??throw new IOException("Serial port closed");
    byte[] b=bytes.ToArray();p.Write(b,0,b.Length);
  }
  public int Read(byte[] buffer)
  {
    var p=serial??throw new IOException("Serial port closed");
    try{return p.Read(buffer,0,buffer.Length);}catch(TimeoutException){return 0;}
  }
  public void Abort(){var p=Interlocked.Exchange(ref serial,null);if(p!=null){try{p.Close();}catch{}p.Dispose();}}
  public void Dispose()=>Abort();
}
internal sealed class TcpTransport : IRotatorTransport
{
  private readonly RotatorSettings options;
  private TcpClient? client;
  internal TcpTransport(RotatorSettings options)=>this.options=options;
  // TcpClient.Connected is merely a snapshot of the previous I/O call.
  // Let the protocol heartbeat discover half-open links via Read/Write.
  public bool Connected => client!=null;
  public void Open()
  {
    var c=new TcpClient{NoDelay=true,ReceiveTimeout=Math.Clamp(options.NativeReadTimeout,100,500),
      SendTimeout=Math.Clamp(options.SendTimeout,100,30000)};
    try
    {
      using var timeout = new CancellationTokenSource(Math.Clamp(options.SendTimeout,200,30000));
      try
      {
        c.ConnectAsync(options.Host,options.Port,timeout.Token).AsTask().GetAwaiter().GetResult();
      }
      catch(OperationCanceledException ex) when(timeout.IsCancellationRequested)
      {
        throw new TimeoutException(
          $"TCP connection to {options.Host}:{options.Port} timed out after {Math.Clamp(options.SendTimeout,200,30000)} ms",ex);
      }
      // KeepAlive is supplementary to application-layer position polling.
      // TCP's .Connected property must not be used as a health probe.
      c.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.KeepAlive,true);
      client=c;
    }catch{c.Dispose();throw;}
  }
  public void Write(ReadOnlySpan<byte> bytes)=>(client??throw new IOException("TCP disconnected")).GetStream().Write(bytes);
  public int Read(byte[] buffer)
  {
    try{
      int n=(client??throw new IOException("TCP disconnected")).GetStream().Read(buffer);
      if(n==0)throw new EndOfStreamException("TCP rotator EOF");
      return n;
    }catch(IOException e) when (e.InnerException is SocketException se && se.SocketErrorCode==SocketError.TimedOut){return 0;}
  }
  public void Abort()=>Interlocked.Exchange(ref client,null)?.Dispose();
  public void Dispose()=>Abort();
}
internal sealed class UdpTransport : IRotatorTransport
{
  private readonly RotatorSettings options;
  private UdpClient? socket;
  internal UdpTransport(RotatorSettings options)=>this.options=options;
  public bool Connected=>socket!=null;
  public void Open()
  {
    int local=options.NativeProtocol==RotatorProtocolKind.PstRotator && options.Port<65535?options.Port+1:0;
    var u=new UdpClient(local);
    try{u.Connect(options.Host,options.Port);u.Client.ReceiveTimeout=Math.Clamp(options.NativeReadTimeout,100,30000);socket=u;}
    catch{u.Dispose();throw;}
  }
  public void Write(ReadOnlySpan<byte> bytes)
  {
    var u=socket??throw new IOException("UDP disconnected");byte[] b=bytes.ToArray();u.Send(b,b.Length);
  }
  public int Read(byte[] buffer)
  {
    var u=socket??throw new IOException("UDP disconnected");
    try{IPEndPoint source=new(IPAddress.Any,0);byte[] b=u.Receive(ref source);
      if(b.Length>buffer.Length)throw new InvalidDataException("UDP rotator frame too long");
      b.CopyTo(buffer,0);return b.Length;}
    catch(SocketException ex) when(ex.SocketErrorCode==SocketError.TimedOut){return 0;}
  }
  public void Abort()=>Interlocked.Exchange(ref socket,null)?.Dispose();
  public void Dispose()=>Abort();
}
