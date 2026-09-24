using System.ComponentModel;
using System.Net;
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
    private readonly CivStreamAssembler StreamAssembler;
    private readonly IcomScopeAssembler ScopeAssembler = new();

    private CancellationTokenSource? Cancellation;
    private Task? Worker;
    private IntPtr Handle = IntPtr.Zero;
    private readonly object HandleSync = new();

    private bool HaveTransportSequence;
    private ushort LastTransportSequence;

    private long PacketCountValue;
    private long CapturedBytesValue;
    private long SerialChunkCountValue;
    private long CivFrameCountValue;
    private long ScopeFrameCountValue;
    private long DuplicateChunkCountValue;
    private long SequenceGapCountValue;
    private long SequenceResetCountValue;
    private long InvalidScopeFrameCountValue;
    private long LanLengthOverflowPacketCountValue;
    private long LastScopeFrameTicks;

    internal event Action<IcomScopeFrame>? ScopeFrameReceived;
    internal event Action<string>? StatusChanged;

    internal string? DetectedRadioAddress { get; private set; }
    internal string? LastError { get; private set; }
    internal bool IsRunning { get; private set; }

    internal long PacketCount => Interlocked.Read(ref PacketCountValue);
    internal long CapturedBytes => Interlocked.Read(ref CapturedBytesValue);
    internal long SerialChunkCount => Interlocked.Read(ref SerialChunkCountValue);
    internal long CivFrameCount => Interlocked.Read(ref CivFrameCountValue);
    internal long ScopeFrameCount => Interlocked.Read(ref ScopeFrameCountValue);
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

    internal IcomLanSpectrumCapture(string radioAddress, int serialPort)
    {
      ConfiguredRadioAddress = (radioAddress ?? string.Empty).Trim();
      SerialPort = Math.Clamp(serialPort, 1, 65535);
      StreamAssembler = new CivStreamAssembler(OnCivFrame);
    }

    internal void Start()
    {
      if (Worker != null) return;

      LastError = null;
      Cancellation = new CancellationTokenSource();
      CancellationToken token = Cancellation.Token;
      Worker = Task.Run(() => CaptureLoop(token), token);
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
          string.IsNullOrWhiteSpace(ConfiguredRadioAddress)
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
      string filter = $"inbound and ip and udp.SrcPort == {SerialPort}";

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
      if (sourcePort != SerialPort) return;

      int udpLength = ReadUInt16Network(packet, udpOffset + 4);
      if (udpLength < 8) return;

      int payloadOffset = udpOffset + 8;
      int payloadLength = Math.Min(
        udpLength - 8,
        packetLength - payloadOffset);
      if (payloadLength <= 0) return;

      string sourceAddress =
        $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}";
      DetectedRadioAddress ??= sourceAddress;

      ProcessLanPayload(new ReadOnlySpan<byte>(packet, payloadOffset, payloadLength));
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
      if (!HaveTransportSequence)
      {
        HaveTransportSequence = true;
        LastTransportSequence = sequence;
        return true;
      }

      ushort expected = unchecked((ushort)(LastTransportSequence + 1));
      if (sequence == LastTransportSequence)
      {
        Interlocked.Increment(ref DuplicateChunkCountValue);
        return false;
      }

      if (sequence != expected)
      {
        ushort forward = unchecked((ushort)(sequence - expected));

        if (forward < 0x8000)
        {
          // A missing tracked packet may only have been an idle packet.  Do not reset
          // CI-V assembly here; if actual serial data was lost, the CI-V frame parser
          // will reject the incomplete frame and resynchronize at the next FE FE.
          Interlocked.Add(ref SequenceGapCountValue, Math.Max(1, (int)forward));
        }
        else
        {
          ushort backward = unchecked((ushort)(expected - sequence));
          if (backward <= 128)
          {
            // Late retransmission / duplicate packet.
            Interlocked.Increment(ref DuplicateChunkCountValue);
            return false;
          }

          // The tracked transport sequence can restart when RS-BA1 reconnects.
          Interlocked.Increment(ref SequenceResetCountValue);
          StreamAssembler.Reset();
        }
      }

      LastTransportSequence = sequence;
      return true;
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

      // A valid multi-frame sweep yields null for sequences 01..10 and produces
      // the completed 475-bin frame only when the final sequence arrives.
      if (scope == null)
        return;

      Interlocked.Increment(ref ScopeFrameCountValue);
      Interlocked.Exchange(ref LastScopeFrameTicks, scope.TimestampUtc.Ticks);
      ScopeFrameReceived?.Invoke(scope);
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
          for (int i = 2; i < Buffer.Count; i++)
          {
            if (Buffer[i] == 0xFD)
            {
              end = i;
              break;
            }
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
