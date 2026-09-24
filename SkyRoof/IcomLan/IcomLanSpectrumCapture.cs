using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Serilog;

namespace SkyRoof
{
  /// <summary>
  /// Passive IC-9700 LAN scope receiver. WinDivert is opened in SNIFF + RECV_ONLY
  /// mode, so matching packets are copied to SkyRoof and are never removed from,
  /// modified in, or reinjected into the RS-BA1 connection.
  /// </summary>
  internal sealed class IcomLanSpectrumCapture : IDisposable
  {
    private const int ScopePointCount = 475;
    private const int ErrorNoData = 232;

    private readonly string ConfiguredRadioAddress;
    private readonly int SerialPort;
    private readonly bool UseSkyCatStream;
    private readonly bool AutoDiscoverCivPort;
    private readonly int SkyCatScopePort;
    private readonly CivStreamAssembler StreamAssembler;
    private readonly IcomScopeAssembler ScopeAssembler = new();

    private CancellationTokenSource? Cancellation;
    private Task? Worker;
    private IntPtr Handle = IntPtr.Zero;
    private readonly object HandleSync = new();
    private TcpClient? SkyCatClient;
    private readonly object SkyCatClientSync = new();

    private const int RecentTransportSequenceWindow = 512;
    private readonly HashSet<ushort> RecentTransportSequences = new();
    private readonly Queue<ushort> RecentTransportSequenceOrder = new();
    private bool HaveExpectedTransportSequence;
    private ushort ExpectedTransportSequence;

    private long PacketCountValue;
    private long CapturedBytesValue;
    private long SerialChunkCountValue;
    private long CivFrameCountValue;
    private long ScopeFrameCountValue;
    private long ScopeUpdateCountValue;
    private long DuplicateChunkCountValue;
    private long SequenceGapCountValue;
    private long SequenceResetCountValue;
    private long InvalidScopeFrameCountValue;
    private long LanLengthOverflowPacketCountValue;
    private long LastScopeFrameTicks;
    private IcomScopeFrame? LatestScopeFrameValue;

    internal event Action<IcomScopeFrame>? ScopeFrameReceived;
    internal event Action<string>? StatusChanged;

    internal string? DetectedRadioAddress { get; private set; }
    internal int? DetectedCivPort { get; private set; }
    internal string? LastError { get; private set; }
    internal bool IsRunning { get; private set; }
    internal bool IsSkyCatStream => UseSkyCatStream;
    internal string TransportName =>
      UseSkyCatStream
        ? $"SkyCAT scope TCP/127.0.0.1:{SkyCatScopePort}"
        : $"RS-BA1 LAN UDP/{SerialPort} via WinDivert";

    internal long PacketCount => Interlocked.Read(ref PacketCountValue);
    internal long CapturedBytes => Interlocked.Read(ref CapturedBytesValue);
    internal long SerialChunkCount => Interlocked.Read(ref SerialChunkCountValue);
    internal long CivFrameCount => Interlocked.Read(ref CivFrameCountValue);
    internal long ScopeFrameCount => Interlocked.Read(ref ScopeFrameCountValue);
    internal long ScopeUpdateCount => Interlocked.Read(ref ScopeUpdateCountValue);
    internal long DuplicateChunkCount => Interlocked.Read(ref DuplicateChunkCountValue);
    internal long SequenceGapCount => Interlocked.Read(ref SequenceGapCountValue);
    internal long SequenceResetCount => Interlocked.Read(ref SequenceResetCountValue);
    internal long InvalidScopeFrameCount => Interlocked.Read(ref InvalidScopeFrameCountValue);
    internal long LanLengthOverflowPacketCount => Interlocked.Read(ref LanLengthOverflowPacketCountValue);

    internal DateTime? LastScopeFrameUtc
    {
      get
      {
        long ticks = Interlocked.Read(ref LastScopeFrameTicks);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
      }
    }

    internal IcomScopeFrame? LatestScopeFrame =>
      Volatile.Read(ref LatestScopeFrameValue);

    internal IcomLanSpectrumCapture(
      string radioAddress,
      int serialPort,
      bool useSkyCatStream = false,
      int skyCatScopePort = 4535,
      bool autoDiscoverCivPort = false)
    {
      ConfiguredRadioAddress = (radioAddress ?? string.Empty).Trim();
      SerialPort = Math.Clamp(serialPort, 1, 65535);
      UseSkyCatStream = useSkyCatStream;
      AutoDiscoverCivPort = autoDiscoverCivPort;
      SkyCatScopePort = Math.Clamp(skyCatScopePort, 1, 65535);
      StreamAssembler = new CivStreamAssembler(OnCivFrame);
    }

    internal void Start()
    {
      if (Worker != null) return;

      LastError = null;
      Cancellation = new CancellationTokenSource();
      CancellationToken token = Cancellation.Token;
      Worker = UseSkyCatStream
        ? Task.Run(() => SkyCatScopeLoop(token), token)
        : Task.Run(() => CaptureLoop(token), token);
    }

    internal void Stop()
    {
      CancellationTokenSource? cancellation = Cancellation;
      Task? worker = Worker;

      if (cancellation == null && worker == null) return;

      try { cancellation?.Cancel(); } catch { }

      lock (HandleSync)
      {
        if (IsValidHandle(Handle))
          _ = WinDivertNative.Shutdown(Handle, WinDivertNative.ShutdownBoth);
      }

      lock (SkyCatClientSync)
      {
        try { SkyCatClient?.Close(); } catch { }
      }

      if (worker != null && !worker.IsCompleted)
      {
        try { worker.Wait(1500); }
        catch (AggregateException) { }
      }

      cancellation?.Dispose();
      Cancellation = null;
      Worker = null;
    }

    public void Dispose()
    {
      Stop();
    }

    private async Task SkyCatScopeLoop(CancellationToken token)
    {
      while (!token.IsCancellationRequested)
      {
        TcpClient? client = null;

        try
        {
          LastError = null;
          PublishStatus(
            $"Connecting to SkyCAT native scope stream 127.0.0.1:{SkyCatScopePort}...");

          client = new TcpClient
          {
            NoDelay = true,
            ReceiveBufferSize = 64 * 1024
          };

          lock (SkyCatClientSync) SkyCatClient = client;

          await client.ConnectAsync(
            IPAddress.Loopback,
            SkyCatScopePort,
            token);

          IsRunning = true;
          DetectedRadioAddress = "SkyCAT";
          PublishStatus(
            $"Receiving IC-9700 scope frames directly from SkyCAT on 127.0.0.1:{SkyCatScopePort}.");

          NetworkStream stream = client.GetStream();
          byte[] lengthBytes = new byte[4];

          while (!token.IsCancellationRequested)
          {
            if (!await ReadExactAsync(stream, lengthBytes, token))
              break;

            int length =
              lengthBytes[0] |
              (lengthBytes[1] << 8) |
              (lengthBytes[2] << 16) |
              (lengthBytes[3] << 24);

            if (length < 7 || length > 4096)
              throw new InvalidDataException(
                $"Invalid SkyCAT scope frame length {length}.");

            byte[] frame = new byte[length];
            if (!await ReadExactAsync(stream, frame, token))
              break;

            Interlocked.Increment(ref PacketCountValue);
            Interlocked.Increment(ref SerialChunkCountValue);
            Interlocked.Add(ref CapturedBytesValue, length + 4);
            OnCivFrame(frame);
          }
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (Exception ex) when (
          ex is SocketException ||
          ex is IOException ||
          ex is InvalidDataException)
        {
          if (!token.IsCancellationRequested)
          {
            IsRunning = false;
            PublishStatus(
              $"SkyCAT scope stream unavailable ({ex.Message}). Retrying...");
          }
        }
        finally
        {
          IsRunning = false;

          lock (SkyCatClientSync)
          {
            if (ReferenceEquals(SkyCatClient, client))
              SkyCatClient = null;
          }

          try { client?.Close(); } catch { }
        }

        if (!token.IsCancellationRequested)
        {
          try { await Task.Delay(300, token); }
          catch (OperationCanceledException) { break; }
        }
      }

      if (token.IsCancellationRequested)
        PublishStatus("SkyCAT scope capture stopped.");
    }

    private static async Task<bool> ReadExactAsync(
      NetworkStream stream,
      byte[] buffer,
      CancellationToken token)
    {
      int offset = 0;

      while (offset < buffer.Length)
      {
        int read = await stream.ReadAsync(
          buffer.AsMemory(offset, buffer.Length - offset),
          token);

        if (read == 0)
          return false;

        offset += read;
      }

      return true;
    }

    private void CaptureLoop(CancellationToken token)
    {
      IntPtr localHandle = IntPtr.Zero;

      try
      {
        string filter = BuildFilter();

        localHandle = WinDivertNative.Open(
          filter,
          WinDivertNative.LayerNetwork,
          priority: 0,
          WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);

        if (!IsValidHandle(localHandle))
        {
          int error = Marshal.GetLastWin32Error();
          throw new Win32Exception(
            error,
            $"WinDivertOpen failed ({error}). Filter: {filter}");
        }

        lock (HandleSync) Handle = localHandle;

        IsRunning = true;
        PublishStatus(
          AutoDiscoverCivPort
            ? string.IsNullOrWhiteSpace(ConfiguredRadioAddress)
              ? "Passive Icom LAN capture started · auto-discovering negotiated CI-V UDP port and radio IP"
              : $"Passive Icom LAN capture started · {ConfiguredRadioAddress} · auto-discovering negotiated CI-V UDP port"
            : string.IsNullOrWhiteSpace(ConfiguredRadioAddress)
              ? $"Passive Icom LAN capture started · inbound UDP/{SerialPort} · auto radio IP"
              : $"Passive Icom LAN capture started · {ConfiguredRadioAddress}:{SerialPort}");

        var packet = new byte[65535];

        while (!token.IsCancellationRequested)
        {
          if (!WinDivertNative.Recv(
                localHandle,
                packet,
                (uint)packet.Length,
                out uint received,
                IntPtr.Zero))
          {
            int error = Marshal.GetLastWin32Error();
            if (token.IsCancellationRequested || error == ErrorNoData) break;
            throw new Win32Exception(error, $"WinDivertRecv failed ({error}).");
          }

          if (received == 0) continue;

          Interlocked.Increment(ref PacketCountValue);
          Interlocked.Add(ref CapturedBytesValue, received);
          ProcessNetworkPacket(packet, checked((int)received));
        }
      }
      catch (DllNotFoundException ex)
      {
        Fail(
          "WinDivert.dll was not found. Reinstall SkyRoof using an installer that includes the Icom LAN Spectrum component.",
          ex);
      }
      catch (BadImageFormatException ex)
      {
        Fail("The installed WinDivert.dll architecture does not match this x64 SkyRoof build.", ex);
      }
      catch (Exception ex)
      {
        if (!token.IsCancellationRequested)
          Fail(ex.Message, ex);
      }
      finally
      {
        IsRunning = false;

        lock (HandleSync)
        {
          if (Handle == localHandle) Handle = IntPtr.Zero;
        }

        if (IsValidHandle(localHandle))
          _ = WinDivertNative.Close(localHandle);

        if (token.IsCancellationRequested)
          PublishStatus("Icom LAN capture stopped.");
      }
    }

    private string BuildFilter()
    {
      // Icom LAN does not guarantee that the negotiated CI-V media socket uses
      // the well-known control-adjacent port. The control login exchanges a
      // client-selected local CI-V port and the radio replies with its actual
      // remote CI-V port. RS-BA1 can therefore use a negotiated source port that
      // is not 50002. In auto-discovery mode capture inbound UDP and identify the
      // CI-V stream from the 0xC1 transport wrapper instead of hard-coding a port.
      string filter = AutoDiscoverCivPort
        ? "inbound and ip and udp and udp.PayloadLength >= 24 and " +
          "udp.Payload[16] == 0xC1 and udp.Payload[21] == 0xFE and udp.Payload[22] == 0xFE"
        : $"inbound and ip and udp.SrcPort == {SerialPort}";

      if (!string.IsNullOrWhiteSpace(ConfiguredRadioAddress))
      {
        if (!IPAddress.TryParse(ConfiguredRadioAddress, out IPAddress? address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
          throw new ArgumentException(
            $"Invalid IC-9700 IPv4 address: '{ConfiguredRadioAddress}'.");

        filter += $" and ip.SrcAddr == {address}";
      }

      return filter;
    }

    private void ProcessNetworkPacket(byte[] packet, int packetLength)
    {
      if (packetLength < 28) return;

      int version = packet[0] >> 4;
      int ipHeaderLength = (packet[0] & 0x0F) * 4;
      if (version != 4 || ipHeaderLength < 20 || packetLength < ipHeaderLength + 8) return;
      if (packet[9] != 17) return; // UDP

      int udpOffset = ipHeaderLength;
      int sourcePort = ReadUInt16Network(packet, udpOffset);
      if (!AutoDiscoverCivPort && sourcePort != SerialPort) return;

      int udpLength = ReadUInt16Network(packet, udpOffset + 4);
      if (udpLength < 8) return;

      int payloadOffset = udpOffset + 8;
      int payloadLength = Math.Min(
        udpLength - 8,
        packetLength - payloadOffset);
      if (payloadLength <= 0) return;

      string sourceAddress =
        $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}";

      ReadOnlySpan<byte> payload =
        new ReadOnlySpan<byte>(packet, payloadOffset, payloadLength);

      // In discovery mode do not claim arbitrary UDP traffic as radio traffic.
      // Only lock onto a source after it exposes the RS-BA1/Icom C1 CI-V wrapper.
      if (AutoDiscoverCivPort)
      {
        if (!LooksLikeIcomCivTransport(payload))
          return;

        DetectedRadioAddress ??= sourceAddress;
        DetectedCivPort ??= sourcePort;
      }
      else
      {
        DetectedRadioAddress ??= sourceAddress;
        DetectedCivPort ??= sourcePort;
      }

      ProcessLanPayload(payload);
    }

    private void ProcessLanPayload(ReadOnlySpan<byte> payload)
    {
      // RS-BA1 tracked serial packets use the OUTER transport sequence at [6..7]
      // (little-endian).  The [19..20] field is a separate inner serial sequence and
      // must not be used to deduplicate receive packets: a long 27 00 CI-V frame can
      // be fragmented across multiple C1 datagrams while that inner value is not a
      // reliable packet-order key.
      //
      // Track type-0 serial packets, including idle/open-close packets, so gaps caused
      // by non-C1 traffic are not mistaken for lost scope fragments.  Pings use a
      // different counter and are intentionally excluded.
      if (payload.Length >= 16 &&
          payload[4] == 0x00 &&
          payload[5] == 0x00)
      {
        ushort transportSequence =
          (ushort)(payload[6] | (payload[7] << 8));

        if (!AcceptTransportSequence(transportSequence))
          return;
      }

      // Icom LAN CI-V data packet (21-byte transport header):
      // [16]    = 0xC1
      // [17..18]= CI-V payload length, little-endian uint16
      // [19..20]= inner CI-V stream sequence, big-endian uint16
      // [21..]  = CI-V bytes
      //
      // The previous decoder treated [17] as an 8-bit length. That truncates any
      // CI-V payload longer than 255 bytes (including full single-frame scope sweeps)
      // and also corrupts stream assembly. The field is 16-bit on the wire.
      if (TryGetSerialPayload(payload, out ReadOnlySpan<byte> serialBytes))
      {
        if (serialBytes.Length > 255)
          Interlocked.Increment(ref LanLengthOverflowPacketCountValue);

        Interlocked.Increment(ref SerialChunkCountValue);

        // Each RS-BA1 C1 datagram normally carries one complete CI-V payload.
        // Decode complete datagrams independently so one reordered/lost packet cannot
        // contaminate the following high-rate scope frames. Retain the stream
        // assembler only as a fallback for an implementation that genuinely splits
        // a CI-V frame across C1 datagrams.
        if (!TryDeliverCompleteCivPayload(serialBytes))
          StreamAssembler.Feed(serialBytes);

        return;
      }

      // Diagnostic/fallback path for implementations that expose a raw CI-V frame in
      // the UDP payload rather than the usual 0xC1 wrapper.
      int preamble = FindPreamble(payload);
      if (preamble >= 0)
        StreamAssembler.Feed(payload.Slice(preamble));
    }

    private bool AcceptTransportSequence(ushort sequence)
    {
      // Match the IC-9700 LAN receive semantics used by mature RS-BA1-compatible
      // implementations: suppress only a sequence number that was actually seen
      // recently. Reordered packets are still delivered. A passive sniffer must not
      // infer "old" from numeric direction alone because retransmission and rollover
      // can legally move backwards in the 16-bit sequence space.
      if (RecentTransportSequences.Contains(sequence))
      {
        Interlocked.Increment(ref DuplicateChunkCountValue);
        return false;
      }

      if (!HaveExpectedTransportSequence)
      {
        HaveExpectedTransportSequence = true;
        ExpectedTransportSequence = unchecked((ushort)(sequence + 1));
      }
      else
      {
        short distance = unchecked((short)(sequence - ExpectedTransportSequence));

        if (distance > 0)
        {
          Interlocked.Add(ref SequenceGapCountValue, distance);
          ExpectedTransportSequence = unchecked((ushort)(sequence + 1));
        }
        else if (distance == 0)
        {
          ExpectedTransportSequence = unchecked((ushort)(sequence + 1));
        }
        else
        {
          // Reordered/retransmitted-but-not-duplicate packet. Deliver it without
          // moving the expected high-water mark backwards.
          Interlocked.Increment(ref SequenceResetCountValue);
        }
      }

      RecentTransportSequences.Add(sequence);
      RecentTransportSequenceOrder.Enqueue(sequence);

      while (RecentTransportSequenceOrder.Count > RecentTransportSequenceWindow)
        RecentTransportSequences.Remove(RecentTransportSequenceOrder.Dequeue());

      return true;
    }

    private bool TryDeliverCompleteCivPayload(ReadOnlySpan<byte> bytes)
    {
      if (bytes.Length < 3 ||
          bytes[0] != 0xFE ||
          bytes[1] != 0xFE ||
          bytes[^1] != 0xFD)
        return false;

      int start = 0;
      bool delivered = false;

      for (int i = 2; i < bytes.Length; i++)
      {
        if (bytes[i] != 0xFD)
          continue;

        int length = i - start + 1;
        if (length < 3 ||
            bytes[start] != 0xFE ||
            bytes[start + 1] != 0xFE)
          return false;

        OnCivFrame(bytes.Slice(start, length).ToArray());
        delivered = true;
        start = i + 1;

        if (start < bytes.Length &&
            (start + 1 >= bytes.Length ||
             bytes[start] != 0xFE ||
             bytes[start + 1] != 0xFE))
          return false;
      }

      return delivered && start == bytes.Length;
    }

    private void OnCivFrame(byte[] frame)
    {
      Interlocked.Increment(ref CivFrameCountValue);

      if (!IcomScopeAssembler.IsScopeFrame(frame))
        return;

      if (!ScopeAssembler.TryFeed(frame, out IcomScopeFrame? scope))
      {
        Interlocked.Increment(ref InvalidScopeFrameCountValue);
        return;
      }

      if (scope == null)
        return;

      // Multi-frame serial/virtual-COM data now emits a display update for each
      // waveform division. Count those separately from completed sweeps so the UI
      // can report both the smooth display cadence and the true sweep cadence.
      Interlocked.Increment(ref ScopeUpdateCountValue);
      if (scope.SweepComplete)
        Interlocked.Increment(ref ScopeFrameCountValue);

      Interlocked.Exchange(ref LastScopeFrameTicks, scope.TimestampUtc.Ticks);
      Volatile.Write(ref LatestScopeFrameValue, scope);
      ScopeFrameReceived?.Invoke(scope);
    }

    private static bool LooksLikeIcomCivTransport(ReadOnlySpan<byte> payload)
    {
      if (payload.Length < 21 || payload[16] != 0xC1)
        return false;

      int declaredLength =
        payload[17] |
        (payload[18] << 8);

      if (declaredLength <= 0 ||
          payload.Length < 21 + declaredLength)
        return false;

      ReadOnlySpan<byte> data = payload.Slice(21, declaredLength);
      return data.Length >= 3 &&
             data[0] == 0xFE &&
             data[1] == 0xFE;
    }

    internal static bool TryGetSerialPayload(
      ReadOnlySpan<byte> payload,
      out ReadOnlySpan<byte> serialBytes)
    {
      serialBytes = default;

      const int headerLength = 21;
      if (payload.Length < headerLength ||
          payload[16] != 0xC1)
        return false;

      int declaredLength =
        payload[17] |
        (payload[18] << 8);

      if (declaredLength <= 0 ||
          payload.Length < headerLength + declaredLength)
        return false;

      serialBytes = payload.Slice(headerLength, declaredLength);
      return true;
    }

    private static int FindPreamble(ReadOnlySpan<byte> bytes)
    {
      for (int i = 0; i + 1 < bytes.Length; i++)
        if (bytes[i] == 0xFE && bytes[i + 1] == 0xFE)
          return i;

      return -1;
    }

    private static int ReadUInt16Network(byte[] bytes, int offset) =>
      (bytes[offset] << 8) | bytes[offset + 1];

    private static bool IsValidHandle(IntPtr handle) =>
      handle != IntPtr.Zero && handle != WinDivertNative.InvalidHandleValue;

    private void Fail(string message, Exception ex)
    {
      LastError = message;
      Log.Error(ex, "Icom LAN spectrum capture failed: {Message}", message);
      PublishStatus(message);
    }

    private void PublishStatus(string message)
    {
      StatusChanged?.Invoke(message);
    }


    private sealed class CivStreamAssembler
    {
      private const int MaximumBufferedBytes = 16384;

      private readonly Action<byte[]> FrameCallback;
      private readonly List<byte> Buffer = new(2048);

      internal CivStreamAssembler(Action<byte[]> frameCallback)
      {
        FrameCallback = frameCallback;
      }

      internal void Reset()
      {
        Buffer.Clear();
      }

      internal void Feed(ReadOnlySpan<byte> bytes)
      {
        for (int i = 0; i < bytes.Length; i++)
          Buffer.Add(bytes[i]);

        Parse();
      }

      private void Parse()
      {
        while (true)
        {
          int start = FindStart();
          if (start < 0)
          {
            if (Buffer.Count > 0 && Buffer[^1] == 0xFE)
            {
              byte last = Buffer[^1];
              Buffer.Clear();
              Buffer.Add(last);
            }
            else
            {
              Buffer.Clear();
            }
            return;
          }

          if (start > 0)
            Buffer.RemoveRange(0, start);

          int end = -1;
          int nestedStart = -1;

          for (int i = 2; i < Buffer.Count; i++)
          {
            if (i + 1 < Buffer.Count &&
                Buffer[i] == 0xFE &&
                Buffer[i + 1] == 0xFE)
            {
              nestedStart = i;
              break;
            }

            if (Buffer[i] == 0xFD)
            {
              end = i;
              break;
            }
          }

          if (nestedStart >= 0)
          {
            Buffer.RemoveRange(0, nestedStart);
            continue;
          }

          if (end < 0)
          {
            if (Buffer.Count > MaximumBufferedBytes)
              Buffer.Clear();
            return;
          }

          byte[] frame = Buffer.GetRange(0, end + 1).ToArray();
          Buffer.RemoveRange(0, end + 1);
          FrameCallback(frame);
        }
      }

      private int FindStart()
      {
        for (int i = 0; i + 1 < Buffer.Count; i++)
          if (Buffer[i] == 0xFE && Buffer[i + 1] == 0xFE)
            return i;

        return -1;
      }
    }
  }
}
