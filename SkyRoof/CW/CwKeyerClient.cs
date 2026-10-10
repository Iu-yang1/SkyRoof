using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SkyRoof.CW
{
  public readonly record struct CwKeyerStatus(
    string Lease,
    string Mode,
    int BreakIn,
    bool Transmitting,
    int KeySpeedRaw)
  {
    public double Wpm =>
      CwMessageTiming.RawKeySpeedToWpm(
        KeySpeedRaw);

    public bool ReadyToSend =>
      Lease == "IDLE" &&
      Mode is "CW" or "CW-R" &&
      BreakIn is 1 or 2 &&
      !Transmitting;
  }

  internal interface ICwKeyerSession :
    IAsyncDisposable
  {
    Task<CwKeyerStatus> GetStatusAsync(
      CancellationToken cancellationToken = default);

    Task SendAsync(
      string text,
      CancellationToken cancellationToken = default);

    Task StopAsync(
      CancellationToken cancellationToken = default);
  }

  internal interface ICwKeyerSessionFactory
  {
    Task<ICwKeyerSession> ConnectAsync(
      int port,
      CancellationToken cancellationToken = default);
  }

  internal sealed class CwKeyerTcpSessionFactory :
    ICwKeyerSessionFactory
  {
    public async Task<ICwKeyerSession> ConnectAsync(
      int port,
      CancellationToken cancellationToken = default)
    {
      if (port is < 1 or > 65535)
        throw new ArgumentOutOfRangeException(
          nameof(port));

      var client =
        new TcpClient(
          AddressFamily.InterNetwork);

      try
      {
        await client.ConnectAsync(
          IPAddress.Loopback,
          port,
          cancellationToken);

        return new CwKeyerTcpSession(
          client);
      }
      catch
      {
        client.Dispose();
        throw;
      }
    }
  }

  /// <summary>
  /// One persistent client session to SkyCAT's loopback-only CW endpoint.
  /// Keeping this TCP connection open is a safety requirement: SkyCAT owns
  /// the Command-17 lease per client connection and intentionally sends STOP
  /// when that connection disappears.
  /// </summary>
  internal sealed class CwKeyerTcpSession :
    ICwKeyerSession
  {
    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim requestLock =
      new(1, 1);
    private bool disposed;

    internal CwKeyerTcpSession(
      TcpClient client)
    {
      this.client =
        client ??
        throw new ArgumentNullException(
          nameof(client));

      NetworkStream stream =
        client.GetStream();

      reader =
        new StreamReader(
          stream,
          Encoding.ASCII,
          detectEncodingFromByteOrderMarks: false,
          bufferSize: 1024,
          leaveOpen: true);

      writer =
        new StreamWriter(
          stream,
          new ASCIIEncoding(),
          bufferSize: 1024,
          leaveOpen: true)
        {
          AutoFlush = true,
          NewLine = "\n"
        };
    }

    public async Task<CwKeyerStatus>
      GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
      string reply =
        await RequestAsync(
          "STATUS",
          cancellationToken);

      return ParseStatus(reply);
    }

    public async Task SendAsync(
      string text,
      CancellationToken cancellationToken = default)
    {
      CwMessageTiming.ValidateText(text);

      string reply =
        await RequestAsync(
          "SEND " + text,
          cancellationToken);

      if (reply != "OK")
        throw CreateProtocolException(
          "SEND",
          reply);
    }

    public async Task StopAsync(
      CancellationToken cancellationToken = default)
    {
      string reply =
        await RequestAsync(
          "STOP",
          cancellationToken);

      if (reply != "OK")
        throw CreateProtocolException(
          "STOP",
          reply);
    }

    internal static CwKeyerStatus ParseStatus(
      string reply)
    {
      string[] parts =
        reply.Split(
          ' ',
          StringSplitOptions.RemoveEmptyEntries);

      if (parts.Length < 6 ||
          parts[0] != "STATUS")
        throw new FormatException(
          "SkyCAT returned an invalid CW STATUS response.");

      string lease = parts[1];
      string? mode = null;
      int? breakIn = null;
      bool? tx = null;
      int? keyRaw = null;

      foreach (string token in
        parts.Skip(2))
      {
        int equals =
          token.IndexOf('=');
        if (equals <= 0)
          continue;

        string name =
          token[..equals];
        string value =
          token[(equals + 1)..];

        switch (name)
        {
          case "MODE":
            mode = value;
            break;

          case "BKIN":
            if (int.TryParse(
                  value,
                  NumberStyles.None,
                  CultureInfo.InvariantCulture,
                  out int bkin))
              breakIn = bkin;
            break;

          case "TX":
            if (value == "0")
              tx = false;
            else if (value == "1")
              tx = true;
            break;

          case "KEYRAW":
            if (int.TryParse(
                  value,
                  NumberStyles.None,
                  CultureInfo.InvariantCulture,
                  out int raw))
              keyRaw = raw;
            break;
        }
      }

      if (mode is null ||
          breakIn is not (0 or 1 or 2) ||
          tx is null ||
          keyRaw is null or < 0 or > 255)
        throw new FormatException(
          "SkyCAT CW STATUS is missing or contains invalid fields.");

      return new(
        lease,
        mode,
        breakIn.Value,
        tx.Value,
        keyRaw.Value);
    }

    private async Task<string> RequestAsync(
      string request,
      CancellationToken cancellationToken)
    {
      ObjectDisposedException.ThrowIf(
        disposed,
        this);

      await requestLock.WaitAsync(
        cancellationToken);

      try
      {
        await writer.WriteLineAsync(
          request.AsMemory(),
          cancellationToken);

        string? reply =
          await reader.ReadLineAsync(
            cancellationToken);

        if (reply is null)
          throw new IOException(
            "SkyCAT CW keyer connection closed.");

        return reply;
      }
      finally
      {
        requestLock.Release();
      }
    }

    private static Exception
      CreateProtocolException(
        string operation,
        string reply)
    {
      string message =
        $"SkyCAT CW {operation} failed: {reply}";

      return reply switch
      {
        "ERR MODE" =>
          new InvalidOperationException(
            message +
            " (TX VFO must already be CW/CW-R)."),

        "ERR BREAKIN" =>
          new InvalidOperationException(
            message +
            " (Semi/Full BK-IN must already be enabled on the radio)."),

        "ERR TXACTIVE" =>
          new InvalidOperationException(
            message +
            " (the radio is already transmitting)."),

        "ERR BUSY" =>
          new InvalidOperationException(
            message +
            " (another CAT/PTT/CW client owns the transmitter lease)."),

        "ERR INVALID" =>
          new ArgumentException(message),

        "ERR TIMEOUT" =>
          new TimeoutException(message),

        _ =>
          new IOException(message)
      };
    }

    public async ValueTask DisposeAsync()
    {
      if (disposed)
        return;

      disposed = true;

      requestLock.Dispose();
      await writer.DisposeAsync();
      reader.Dispose();
      client.Dispose();
    }
  }
}
