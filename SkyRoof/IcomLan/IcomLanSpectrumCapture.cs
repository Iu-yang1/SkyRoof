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

    private CancellationTokenSource? Cancellation;
    private Task? Worker;
    private IntPtr Handle = IntPtr.Zero;
    private readonly object HandleSync = new();

    private bool HaveSerialSequence;
    private ushort LastSerialSequence;

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
      // Icom LAN serial stream packet:
      // [16] = 0xC1, [17] = byte count for THIS serial chunk,
      // [19..20] = CI-V serial-stream sequence, [21..] = chunk bytes.
      //
      // A 27 00 scope waveform is a ~497-byte CI-V frame, but the RS-BA1 LAN
      // transport fragments that serial frame across multiple C1 datagrams.  The
      // one-byte count is therefore the chunk length, not the complete CI-V frame
      // length.  Feeding bytes beyond the declared chunk length injects transport
      // padding/trailer data into the CI-V stream and prevents fragmented scope frames
      // from ever being reassembled.
      if (payload.Length >= 22 && payload[16] == 0xC1)
      {
        int available = payload.Length - 21;
        int declared = payload[17];
        if (available <= 0 || declared <= 0) return;

        // Normal RS-BA1 serial packets carry one bounded chunk and [17] is its
        // exact length.  Some RS-BA1 scope paths observed in the field can instead
        // place an entire >255-byte CI-V frame in one datagram and expose only the
        // low byte of that length.  Support both encodings so opening the RS-BA1
        // scope and enabling 27 11 directly through SkyCAT use the same decoder.
        bool wrappedBulkLength =
          available > 255 &&
          declared == (byte)(available & 0xFF);

        int count = wrappedBulkLength
          ? available
          : Math.Min(declared, available);

        if (wrappedBulkLength)
          Interlocked.Increment(ref LanLengthOverflowPacketCountValue);

        ushort sequence = (ushort)((payload[19] << 8) | payload[20]);
        Interlocked.Increment(ref SerialChunkCountValue);
        PushSerialChunk(sequence, payload.Slice(21, count));
        return;
      }

      // Diagnostic/fallback path for implementations that expose a raw CI-V frame in
      // the UDP payload rather than the usual 0xC1 wrapper.
      int preamble = FindPreamble(payload);
      if (preamble >= 0)
        StreamAssembler.Feed(payload.Slice(preamble));
    }

    private void PushSerialChunk(ushort sequence, ReadOnlySpan<byte> bytes)
    {
      if (!HaveSerialSequence)
      {
        HaveSerialSequence = true;
        LastSerialSequence = sequence;
        StreamAssembler.Feed(bytes);
        return;
      }

      ushort expected = unchecked((ushort)(LastSerialSequence + 1));
      if (sequence == LastSerialSequence)
      {
        Interlocked.Increment(ref DuplicateChunkCountValue);
        return;
      }

      if (sequence != expected)
      {
        ushort forward = unchecked((ushort)(sequence - expected));

        if (forward < 1024)
        {
          Interlocked.Add(ref SequenceGapCountValue, Math.Max(1, (int)forward));
          StreamAssembler.Reset();
        }
        else
        {
          ushort backward = unchecked((ushort)(expected - sequence));
          if (backward <= 32)
          {
            Interlocked.Increment(ref DuplicateChunkCountValue);
            return;
          }

          // The serial-side sequence commonly restarts when RS-BA1 reconnects.
          Interlocked.Increment(ref SequenceResetCountValue);
          StreamAssembler.Reset();
        }
      }

      LastSerialSequence = sequence;
      StreamAssembler.Feed(bytes);
    }

    private void OnCivFrame(byte[] frame)
    {
      Interlocked.Increment(ref CivFrameCountValue);

      bool isScopeCommand =
        frame.Length >= 6 &&
        frame[0] == 0xFE &&
        frame[1] == 0xFE &&
        frame[4] == 0x27 &&
        frame[5] == 0x00;

      if (!TryParseScopeFrame(frame, out IcomScopeFrame? scope) || scope == null)
      {
        if (isScopeCommand)
          Interlocked.Increment(ref InvalidScopeFrameCountValue);
        return;
      }

      Interlocked.Increment(ref ScopeFrameCountValue);
      Interlocked.Exchange(ref LastScopeFrameTicks, scope.TimestampUtc.Ticks);

      ScopeFrameReceived?.Invoke(scope);
    }

    private static bool TryParseScopeFrame(byte[] frame, out IcomScopeFrame? result)
    {
      result = null;

      // IC-9700 LAN 27 00 full scope frame:
      // FE FE <to> <from> 27 00
      // scope currentDiv maxDiv mode
      // freqA[5] freqB/span[5] outOfRange
      // waveform[475]
      // FD
      //
      // Total = 497 bytes. The radio can also emit short informational 27 00 frames;
      // those are intentionally ignored here because they contain no complete waveform.
      const int FullLanFrameLength = 497;
      const int dataStart = 6;
      const int modeIndex = 9;
      const int frequencyAIndex = 10;
      const int frequencyBIndex = 15;
      const int outOfRangeIndex = 20;
      const int samplesStart = 21;

      if (frame.Length != FullLanFrameLength ||
          frame[0] != 0xFE ||
          frame[1] != 0xFE ||
          frame[4] != 0x27 ||
          frame[5] != 0x00 ||
          frame[^1] != 0xFD)
        return false;

      byte scope = frame[dataStart];
      byte divisionCurrent = frame[dataStart + 1];
      byte divisionMaximum = frame[dataStart + 2];
      byte mode = frame[modeIndex];

      // LAN sends the whole sweep in one division.
      if (scope > 1 ||
          divisionCurrent != 1 ||
          divisionMaximum != 1 ||
          mode > 3)
        return false;

      if (!TryDecodeBcdFrequency(
            new ReadOnlySpan<byte>(frame, frequencyAIndex, 5),
            out long frequencyA) ||
          !TryDecodeBcdFrequency(
            new ReadOnlySpan<byte>(frame, frequencyBIndex, 5),
            out long frequencyB))
        return false;

      byte outOfRange = frame[outOfRangeIndex];
      if (outOfRange > 1)
        return false;

      var samples = new byte[ScopePointCount];
      for (int i = 0; i < ScopePointCount; i++)
      {
        byte sample = frame[samplesStart + i];

        // Official IC-9700 scope data range is 0..160. Values above that indicate
        // a malformed/reassembled frame, not a legitimate high-amplitude bin.
        if (sample > 160)
          return false;

        samples[i] = sample;
      }

      result = new IcomScopeFrame
      {
        TimestampUtc = DateTime.UtcNow,
        Scope = scope,
        DivisionCurrent = divisionCurrent,
        DivisionMaximum = divisionMaximum,
        Mode = mode,
        FrequencyAHz = frequencyA,
        FrequencyBHz = frequencyB,
        OutOfRange = outOfRange != 0,
        Samples = samples
      };

      return true;
    }

    private static bool TryDecodeBcdFrequency(
      ReadOnlySpan<byte> bytes,
      out long value)
    {
      value = 0;
      long multiplier = 1;

      foreach (byte b in bytes)
      {
        int low = b & 0x0F;
        int high = (b >> 4) & 0x0F;

        if (low > 9 || high > 9)
        {
          value = 0;
          return false;
        }

        value += (low + high * 10L) * multiplier;
        multiplier *= 100;
      }

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
