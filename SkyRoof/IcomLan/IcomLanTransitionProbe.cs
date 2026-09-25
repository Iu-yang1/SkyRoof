using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyRoof
{
  internal enum IcomLanProbePhase
  {
    ClosedBefore = 0,
    Open = 1,
    ClosedAfter = 2
  }

  internal sealed class IcomLanTransitionProbe : IDisposable
  {
    private const int ErrorNoData = 232;
    private const int SignatureLimit = 512;

    private readonly IPAddress RadioAddress;
    private readonly string RadioAddressText;
    private readonly object Sync = new();
    private readonly Dictionary<IcomLanProbePhase, PhaseStats> Phases =
      new()
      {
        [IcomLanProbePhase.ClosedBefore] = new PhaseStats(),
        [IcomLanProbePhase.Open] = new PhaseStats(),
        [IcomLanProbePhase.ClosedAfter] = new PhaseStats()
      };

    private CancellationTokenSource? Cancellation;
    private Task? Worker;
    private IntPtr Handle = IntPtr.Zero;
    private readonly object HandleSync = new();
    private int CurrentPhaseValue = (int)IcomLanProbePhase.ClosedBefore;
    private DateTime ProbeStartedUtc;
    private DateTime? MarkOpenUtc;
    private DateTime? MarkClosedUtc;

    internal string? LastError { get; private set; }
    internal bool IsRunning { get; private set; }
    internal IcomLanProbePhase CurrentPhase =>
      (IcomLanProbePhase)Volatile.Read(ref CurrentPhaseValue);

    internal DateTime CurrentPhaseStartedUtc
    {
      get
      {
        lock (Sync)
          return Phases[CurrentPhase].StartedUtc;
      }
    }

    internal IcomLanTransitionProbe(string radioAddress)
    {
      if (!IPAddress.TryParse(radioAddress, out IPAddress? address) ||
          address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new ArgumentException(
          $"A concrete IC-9700 IPv4 address is required for transition capture: '{radioAddress}'.");

      RadioAddress = address;
      RadioAddressText = address.ToString();
    }

    internal void Start()
    {
      if (Worker != null) return;

      LastError = null;
      ProbeStartedUtc = DateTime.UtcNow;

      lock (Sync)
      {
        foreach (PhaseStats stats in Phases.Values)
          stats.Reset();

        Phases[IcomLanProbePhase.ClosedBefore].StartedUtc =
          ProbeStartedUtc;
      }

      Volatile.Write(
        ref CurrentPhaseValue,
        (int)IcomLanProbePhase.ClosedBefore);

      Cancellation = new CancellationTokenSource();
      CancellationToken token = Cancellation.Token;
      Worker = Task.Run(() => CaptureLoop(token), token);
    }

    internal void BeginOpenWindow()
    {
      TransitionTo(
        IcomLanProbePhase.Open,
        out DateTime transitionUtc);
      MarkOpenUtc = transitionUtc;
    }

    internal void BeginClosedAfterWindow()
    {
      TransitionTo(
        IcomLanProbePhase.ClosedAfter,
        out DateTime transitionUtc);
      MarkClosedUtc = transitionUtc;
    }

    private void TransitionTo(
      IcomLanProbePhase next,
      out DateTime transitionUtc)
    {
      transitionUtc = DateTime.UtcNow;

      lock (Sync)
      {
        IcomLanProbePhase current = CurrentPhase;

        if ((int)next != (int)current + 1)
          throw new InvalidOperationException(
            $"Invalid transition-capture phase change {current} -> {next}.");

        Phases[current].EndedUtc = transitionUtc;
        Phases[next].StartedUtc = transitionUtc;
        Volatile.Write(ref CurrentPhaseValue, (int)next);
      }
    }

    internal string StopAndBuildReport()
    {
      Stop();

      DateTime endedUtc = DateTime.UtcNow;

      lock (Sync)
      {
        PhaseStats current = Phases[CurrentPhase];
        if (current.StartedUtc != default &&
            current.EndedUtc == default)
          current.EndedUtc = endedUtc;

        return BuildReportLocked(endedUtc);
      }
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
          _ = WinDivertNative.Shutdown(
            Handle,
            WinDivertNative.ShutdownBoth);
      }

      if (worker != null && !worker.IsCompleted)
      {
        try { worker.Wait(2500); }
        catch (AggregateException) { }
      }

      cancellation?.Dispose();
      Cancellation = null;
      Worker = null;
    }

    public void Dispose() => Stop();

    private void CaptureLoop(CancellationToken token)
    {
      IntPtr localHandle = IntPtr.Zero;

      try
      {
        string filter =
          $"ip and udp and (ip.SrcAddr == {RadioAddressText} or " +
          $"ip.DstAddr == {RadioAddressText})";

        localHandle = WinDivertNative.Open(
          filter,
          WinDivertNative.LayerNetwork,
          priority: -20,
          WinDivertNative.FlagSniff |
          WinDivertNative.FlagRecvOnly);

        if (!IsValidHandle(localHandle))
        {
          int error = Marshal.GetLastWin32Error();
          throw new Win32Exception(
            error,
            $"WinDivertOpen failed ({error}). Filter: {filter}");
        }

        lock (HandleSync)
          Handle = localHandle;

        IsRunning = true;
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

            if (token.IsCancellationRequested ||
                error == ErrorNoData)
              break;

            throw new Win32Exception(
              error,
              $"WinDivertRecv failed ({error}).");
          }

          if (received == 0) continue;

          if (TryInspectPacket(
                packet.AsSpan(0, checked((int)received)),
                RadioAddress,
                out ProbePacketInfo info))
          {
            lock (Sync)
              Phases[CurrentPhase].Add(info);
          }
        }
      }
      catch (DllNotFoundException ex)
      {
        LastError =
          "WinDivert.dll was not found. Reinstall SkyRoof using an installer that includes WinDivert.";
        _ = ex;
      }
      catch (BadImageFormatException ex)
      {
        LastError =
          "The installed WinDivert.dll architecture does not match this x64 SkyRoof build.";
        _ = ex;
      }
      catch (Exception ex)
      {
        if (!token.IsCancellationRequested)
          LastError = ex.Message;
      }
      finally
      {
        IsRunning = false;

        lock (HandleSync)
        {
          if (Handle == localHandle)
            Handle = IntPtr.Zero;
        }

        if (IsValidHandle(localHandle))
          _ = WinDivertNative.Close(localHandle);
      }
    }

    internal static bool TryInspectPacket(
      ReadOnlySpan<byte> packet,
      IPAddress radioAddress,
      out ProbePacketInfo info)
    {
      info = default;

      if (packet.Length < 28 ||
          packet[0] >> 4 != 4)
        return false;

      int ipHeaderLength = (packet[0] & 0x0F) * 4;
      if (ipHeaderLength < 20 ||
          packet.Length < ipHeaderLength + 8 ||
          packet[9] != 17)
        return false;

      Span<byte> radioBytes = stackalloc byte[4];
      if (!radioAddress.TryWriteBytes(
            radioBytes,
            out int written) ||
          written != 4)
        return false;

      bool sourceIsRadio =
        packet.Slice(12, 4).SequenceEqual(radioBytes);
      bool destinationIsRadio =
        packet.Slice(16, 4).SequenceEqual(radioBytes);

      if (!sourceIsRadio && !destinationIsRadio)
        return false;

      int udpOffset = ipHeaderLength;
      int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(
        packet.Slice(udpOffset, 2));
      int destinationPort = BinaryPrimitives.ReadUInt16BigEndian(
        packet.Slice(udpOffset + 2, 2));
      int udpLength = BinaryPrimitives.ReadUInt16BigEndian(
        packet.Slice(udpOffset + 4, 2));

      if (udpLength < 8)
        return false;

      int payloadOffset = udpOffset + 8;
      int payloadLength = Math.Min(
        udpLength - 8,
        packet.Length - payloadOffset);

      if (payloadLength < 0)
        return false;

      ReadOnlySpan<byte> payload =
        packet.Slice(payloadOffset, payloadLength);

      bool radioToPc = sourceIsRadio;
      int radioPort =
        radioToPc ? sourcePort : destinationPort;
      int localPort =
        radioToPc ? destinationPort : sourcePort;

      ushort? outerType = null;
      byte? transportMarker = null;
      int? declaredCivLength = null;
      string? civSignature = null;
      bool combinedScope = false;
      bool chunkedScope = false;

      if (payload.Length >= 6)
        outerType = BinaryPrimitives.ReadUInt16LittleEndian(
          payload.Slice(4, 2));

      if (payload.Length >= 17)
        transportMarker = payload[16];

      if (payload.Length >= 21 &&
          payload[16] == 0xC1)
      {
        declaredCivLength =
          BinaryPrimitives.ReadUInt16LittleEndian(
            payload.Slice(17, 2));

        int available = payload.Length - 21;
        int civLength = Math.Min(
          declaredCivLength.Value,
          Math.Max(0, available));

        if (civLength > 0)
        {
          ReadOnlySpan<byte> serial =
            payload.Slice(21, civLength);

          if (TryFindFirstCompleteCivFrame(
                serial,
                out ReadOnlySpan<byte> frame))
          {
            civSignature = DescribeCivFrame(
              frame,
              out combinedScope,
              out chunkedScope);
          }
        }
      }

      string packetSignature = BuildPacketSignature(
        radioToPc,
        radioPort,
        localPort,
        payloadLength,
        outerType,
        transportMarker,
        civSignature);

      string? payloadHex =
        payloadLength <= 192
          ? Convert.ToHexString(payload)
          : null;

      info = new ProbePacketInfo(
        radioToPc,
        radioPort,
        localPort,
        packet.Length,
        payloadLength,
        outerType,
        transportMarker,
        declaredCivLength,
        civSignature,
        packetSignature,
        payloadHex,
        combinedScope,
        chunkedScope);

      return true;
    }

    private static bool TryFindFirstCompleteCivFrame(
      ReadOnlySpan<byte> serial,
      out ReadOnlySpan<byte> frame)
    {
      frame = default;

      for (int start = 0; start + 2 < serial.Length; start++)
      {
        if (serial[start] != 0xFE ||
            serial[start + 1] != 0xFE)
          continue;

        for (int end = start + 2; end < serial.Length; end++)
        {
          if (serial[end] == 0xFD)
          {
            frame = serial.Slice(
              start,
              end - start + 1);
            return true;
          }
        }

        return false;
      }

      return false;
    }

    private static string DescribeCivFrame(
      ReadOnlySpan<byte> frame,
      out bool combinedScope,
      out bool chunkedScope)
    {
      combinedScope = false;
      chunkedScope = false;

      if (frame.Length < 6)
        return "CI-V short";

      string command =
        $"{frame[4]:X2}-{frame[5]:X2}";

      if (frame[4] == 0x27 &&
          frame[5] == 0x00 &&
          frame.Length >= 10)
      {
        int current =
          DecodeBcdOrRaw(frame[7]);
        int maximum =
          DecodeBcdOrRaw(frame[8]);

        combinedScope =
          current == 1 &&
          maximum == 1;
        chunkedScope =
          maximum > 1;

        return
          $"27-00 div={current:00}/{maximum:00}";
      }

      if (frame[4] == 0x27 &&
          frame[5] is 0x10 or 0x11 or 0x12 or 0x14 or 0x15 or 0x19 or 0x1A)
      {
        string suffix =
          frame.Length > 6
            ? " " + string.Join(
                "-",
                frame.Slice(
                  6,
                  Math.Min(6, frame.Length - 7))
                .ToArray()
                .Select(b => b.ToString("X2")))
            : string.Empty;

        return command + suffix;
      }

      return command;
    }

    private static int DecodeBcdOrRaw(byte value)
    {
      int hi = (value >> 4) & 0x0F;
      int lo = value & 0x0F;

      return hi <= 9 && lo <= 9
        ? hi * 10 + lo
        : value;
    }

    private static string BuildPacketSignature(
      bool radioToPc,
      int radioPort,
      int localPort,
      int payloadLength,
      ushort? outerType,
      byte? transportMarker,
      string? civSignature)
    {
      var sb = new StringBuilder();

      sb.Append(
        radioToPc
          ? "RADIO->PC "
          : "PC->RADIO ");

      sb.Append(radioPort);
      sb.Append("<->");
      sb.Append(localPort);
      sb.Append(" len=");
      sb.Append(payloadLength);

      if (outerType.HasValue)
      {
        sb.Append(" outer=");
        sb.Append(outerType.Value.ToString("X4"));
      }

      if (transportMarker.HasValue)
      {
        sb.Append(" marker=");
        sb.Append(transportMarker.Value.ToString("X2"));
      }

      if (!string.IsNullOrEmpty(civSignature))
      {
        sb.Append(" ");
        sb.Append(civSignature);
      }

      return sb.ToString();
    }

    private string BuildReportLocked(DateTime endedUtc)
    {
      var sb = new StringBuilder();

      sb.AppendLine("SkyRoof RS-BA1 Spectrum transition capture");
      sb.AppendLine($"Radio: {RadioAddressText}");
      sb.AppendLine($"Probe started UTC: {ProbeStartedUtc:O}");
      sb.AppendLine($"OPEN marker UTC: {MarkOpenUtc:O}");
      sb.AppendLine($"CLOSE marker UTC: {MarkClosedUtc:O}");
      sb.AppendLine($"Probe ended UTC: {endedUtc:O}");
      sb.AppendLine();

      foreach (IcomLanProbePhase phase in Enum.GetValues<IcomLanProbePhase>())
      {
        PhaseStats stats = Phases[phase];
        TimeSpan duration = stats.Duration(endedUtc);
        double seconds = Math.Max(0.001, duration.TotalSeconds);

        sb.AppendLine($"[{phase}] {seconds:0.000} s");
        sb.AppendLine(
          $"  packets={stats.PacketCount:N0} " +
          $"bytes={stats.Bytes:N0} " +
          $"rate={stats.PacketCount / seconds:0.0} pkt/s " +
          $"C1={stats.C1Packets:N0} " +
          $"combined27_00={stats.CombinedScopeFrames:N0} " +
          $"chunked27_00={stats.ChunkedScopeFrames:N0}");
        sb.AppendLine();
      }

      sb.AppendLine("Flow comparison (OPEN delta vs max(CLOSED before/after)):");
      foreach (FlowComparison comparison in BuildFlowComparisonsLocked(endedUtc)
        .Take(20))
      {
        sb.AppendLine(
          $"  {comparison.Flow,-34} " +
          $"closed1={comparison.ClosedBeforeRate,7:0.0}/s " +
          $"open={comparison.OpenRate,7:0.0}/s " +
          $"closed2={comparison.ClosedAfterRate,7:0.0}/s " +
          $"delta={comparison.Delta,7:+0.0;-0.0;0.0}/s " +
          $"openCombined={comparison.OpenCombinedScope:N0}");
      }

      sb.AppendLine();
      sb.AppendLine("CI-V signature comparison:");
      foreach (string signature in UnionKeys(
        Phases[IcomLanProbePhase.ClosedBefore].CivSignatures,
        Phases[IcomLanProbePhase.Open].CivSignatures,
        Phases[IcomLanProbePhase.ClosedAfter].CivSignatures)
        .OrderByDescending(key =>
          GetCount(
            Phases[IcomLanProbePhase.Open].CivSignatures,
            key)))
      {
        long before = GetCount(
          Phases[IcomLanProbePhase.ClosedBefore].CivSignatures,
          signature);
        long open = GetCount(
          Phases[IcomLanProbePhase.Open].CivSignatures,
          signature);
        long after = GetCount(
          Phases[IcomLanProbePhase.ClosedAfter].CivSignatures,
          signature);

        if (before == 0 && open == 0 && after == 0)
          continue;

        sb.AppendLine(
          $"  {signature,-32} " +
          $"closed1={before,8:N0} " +
          $"open={open,8:N0} " +
          $"closed2={after,8:N0}");
      }

      sb.AppendLine();
      sb.AppendLine("Outbound small/control packet samples (chronological):");
      foreach (IcomLanProbePhase phase in Enum.GetValues<IcomLanProbePhase>())
      {
        sb.AppendLine($"  [{phase}]");

        foreach (ProbeEvent sample in Phases[phase].Events
          .Where(x => !x.RadioToPc)
          .Take(60))
        {
          sb.AppendLine(
            $"    +{sample.OffsetSeconds,7:0.000}s " +
            $"PC:{sample.LocalPort}->RADIO:{sample.RadioPort} " +
            $"len={sample.PayloadLength} " +
            $"{sample.CivSignature ?? "-"} " +
            $"{sample.PayloadHex}");
        }
      }

      sb.AppendLine();
      sb.AppendLine("Top packet signatures by OPEN-only increase:");
      foreach (SignatureComparison comparison in
        BuildSignatureComparisonsLocked(endedUtc).Take(30))
      {
        sb.AppendLine(
          $"  delta={comparison.Delta,7:+0.0;-0.0;0.0}/s " +
          $"closed1={comparison.ClosedBeforeRate,7:0.0}/s " +
          $"open={comparison.OpenRate,7:0.0}/s " +
          $"closed2={comparison.ClosedAfterRate,7:0.0}/s  " +
          comparison.Signature);
      }

      sb.AppendLine();
      AppendMeasuredConclusionLocked(sb, endedUtc);

      return sb.ToString();
    }

    private void AppendMeasuredConclusionLocked(
      StringBuilder sb,
      DateTime endedUtc)
    {
      PhaseStats before =
        Phases[IcomLanProbePhase.ClosedBefore];
      PhaseStats open =
        Phases[IcomLanProbePhase.Open];
      PhaseStats after =
        Phases[IcomLanProbePhase.ClosedAfter];

      double beforeSeconds =
        Math.Max(0.001, before.Duration(endedUtc).TotalSeconds);
      double openSeconds =
        Math.Max(0.001, open.Duration(endedUtc).TotalSeconds);
      double afterSeconds =
        Math.Max(0.001, after.Duration(endedUtc).TotalSeconds);

      double beforeCombined =
        before.CombinedScopeFrames / beforeSeconds;
      double openCombined =
        open.CombinedScopeFrames / openSeconds;
      double afterCombined =
        after.CombinedScopeFrames / afterSeconds;

      sb.AppendLine("Measured conclusion:");

      if (openCombined >= 5 &&
          openCombined >= Math.Max(
            beforeCombined,
            afterCombined) * 3 + 1)
      {
        sb.AppendLine(
          $"  Native combined CI-V 27 00 is gated by the RS-BA1 Spectrum state in this capture: " +
          $"CLOSED-before {beforeCombined:0.0}/s, OPEN {openCombined:0.0}/s, " +
          $"CLOSED-after {afterCombined:0.0}/s.");
      }
      else
      {
        sb.AppendLine(
          $"  No decisive combined-27 00 gating result from this capture: " +
          $"CLOSED-before {beforeCombined:0.0}/s, OPEN {openCombined:0.0}/s, " +
          $"CLOSED-after {afterCombined:0.0}/s.");
      }

      FlowComparison? candidate =
        BuildFlowComparisonsLocked(endedUtc)
          .FirstOrDefault(x => x.Delta > 1);

      if (candidate != null)
      {
        sb.AppendLine(
          $"  Strongest flow delta: {candidate.Flow}; " +
          $"OPEN increase {candidate.Delta:+0.0;-0.0;0.0} pkt/s.");
      }
    }

    private IEnumerable<FlowComparison> BuildFlowComparisonsLocked(
      DateTime endedUtc)
    {
      PhaseStats before =
        Phases[IcomLanProbePhase.ClosedBefore];
      PhaseStats open =
        Phases[IcomLanProbePhase.Open];
      PhaseStats after =
        Phases[IcomLanProbePhase.ClosedAfter];

      double beforeSeconds =
        Math.Max(0.001, before.Duration(endedUtc).TotalSeconds);
      double openSeconds =
        Math.Max(0.001, open.Duration(endedUtc).TotalSeconds);
      double afterSeconds =
        Math.Max(0.001, after.Duration(endedUtc).TotalSeconds);

      return UnionKeys(
          before.Flows,
          open.Flows,
          after.Flows)
        .Select(flow =>
        {
          FlowStats beforeStats =
            GetFlow(before.Flows, flow);
          FlowStats openStats =
            GetFlow(open.Flows, flow);
          FlowStats afterStats =
            GetFlow(after.Flows, flow);

          double beforeRate =
            beforeStats.Packets / beforeSeconds;
          double openRate =
            openStats.Packets / openSeconds;
          double afterRate =
            afterStats.Packets / afterSeconds;

          return new FlowComparison(
            flow,
            beforeRate,
            openRate,
            afterRate,
            openRate - Math.Max(beforeRate, afterRate),
            openStats.CombinedScopeFrames);
        })
        .OrderByDescending(x => x.Delta);
    }

    private IEnumerable<SignatureComparison>
      BuildSignatureComparisonsLocked(DateTime endedUtc)
    {
      PhaseStats before =
        Phases[IcomLanProbePhase.ClosedBefore];
      PhaseStats open =
        Phases[IcomLanProbePhase.Open];
      PhaseStats after =
        Phases[IcomLanProbePhase.ClosedAfter];

      double beforeSeconds =
        Math.Max(0.001, before.Duration(endedUtc).TotalSeconds);
      double openSeconds =
        Math.Max(0.001, open.Duration(endedUtc).TotalSeconds);
      double afterSeconds =
        Math.Max(0.001, after.Duration(endedUtc).TotalSeconds);

      return UnionKeys(
          before.PacketSignatures,
          open.PacketSignatures,
          after.PacketSignatures)
        .Select(signature =>
        {
          double beforeRate =
            GetCount(before.PacketSignatures, signature) /
            beforeSeconds;
          double openRate =
            GetCount(open.PacketSignatures, signature) /
            openSeconds;
          double afterRate =
            GetCount(after.PacketSignatures, signature) /
            afterSeconds;

          return new SignatureComparison(
            signature,
            beforeRate,
            openRate,
            afterRate,
            openRate - Math.Max(beforeRate, afterRate));
        })
        .OrderByDescending(x => x.Delta);
    }

    private static IEnumerable<string> UnionKeys<T>(
      Dictionary<string, T> first,
      Dictionary<string, T> second,
      Dictionary<string, T> third) =>
      first.Keys
        .Concat(second.Keys)
        .Concat(third.Keys)
        .Distinct(StringComparer.Ordinal);

    private static long GetCount(
      Dictionary<string, long> dictionary,
      string key) =>
      dictionary.TryGetValue(key, out long value)
        ? value
        : 0;

    private static FlowStats GetFlow(
      Dictionary<string, FlowStats> dictionary,
      string key) =>
      dictionary.TryGetValue(key, out FlowStats? value)
        ? value
        : new FlowStats();

    private static bool IsValidHandle(IntPtr handle) =>
      handle != IntPtr.Zero &&
      handle != WinDivertNative.InvalidHandleValue;

    private sealed class PhaseStats
    {
      internal DateTime StartedUtc;
      internal DateTime EndedUtc;
      internal long PacketCount;
      internal long Bytes;
      internal long C1Packets;
      internal long CombinedScopeFrames;
      internal long ChunkedScopeFrames;
      internal readonly Dictionary<string, FlowStats> Flows =
        new(StringComparer.Ordinal);
      internal readonly Dictionary<string, long> PacketSignatures =
        new(StringComparer.Ordinal);
      internal readonly Dictionary<string, long> CivSignatures =
        new(StringComparer.Ordinal);
      internal readonly List<ProbeEvent> Events = new();

      internal void Reset()
      {
        StartedUtc = default;
        EndedUtc = default;
        PacketCount = 0;
        Bytes = 0;
        C1Packets = 0;
        CombinedScopeFrames = 0;
        ChunkedScopeFrames = 0;
        Flows.Clear();
        PacketSignatures.Clear();
        CivSignatures.Clear();
        Events.Clear();
      }

      internal TimeSpan Duration(DateTime fallbackEnd)
      {
        if (StartedUtc == default)
          return TimeSpan.Zero;

        DateTime end =
          EndedUtc == default
            ? fallbackEnd
            : EndedUtc;

        return end > StartedUtc
          ? end - StartedUtc
          : TimeSpan.Zero;
      }

      internal void Add(ProbePacketInfo info)
      {
        PacketCount++;
        Bytes += info.PacketLength;

        if (info.TransportMarker == 0xC1)
          C1Packets++;

        if (info.CombinedScope)
          CombinedScopeFrames++;

        if (info.ChunkedScope)
          ChunkedScopeFrames++;

        string flow =
          info.RadioToPc
            ? $"RADIO:{info.RadioPort}->PC:{info.LocalPort}"
            : $"PC:{info.LocalPort}->RADIO:{info.RadioPort}";

        if (!Flows.TryGetValue(flow, out FlowStats? flowStats))
        {
          flowStats = new FlowStats();
          Flows[flow] = flowStats;
        }

        flowStats.Packets++;
        flowStats.Bytes += info.PacketLength;

        if (info.CombinedScope)
          flowStats.CombinedScopeFrames++;

        if (info.ChunkedScope)
          flowStats.ChunkedScopeFrames++;

        IncrementBounded(
          PacketSignatures,
          info.PacketSignature);

        if (!string.IsNullOrEmpty(info.CivSignature))
          IncrementBounded(
            CivSignatures,
            (info.RadioToPc ? "RADIO->PC " : "PC->RADIO ") +
            info.CivSignature);

        if (Events.Count < 180 &&
            info.PayloadHex != null)
        {
          double offset =
            StartedUtc == default
              ? 0
              : Math.Max(
                  0,
                  (DateTime.UtcNow - StartedUtc).TotalSeconds);

          Events.Add(
            new ProbeEvent(
              offset,
              info.RadioToPc,
              info.RadioPort,
              info.LocalPort,
              info.PayloadLength,
              info.CivSignature,
              info.PayloadHex));
        }
      }

      private static void IncrementBounded(
        Dictionary<string, long> dictionary,
        string key)
      {
        if (dictionary.TryGetValue(key, out long current))
        {
          dictionary[key] = current + 1;
          return;
        }

        if (dictionary.Count < SignatureLimit)
          dictionary[key] = 1;
      }
    }

    private sealed class FlowStats
    {
      internal long Packets;
      internal long Bytes;
      internal long CombinedScopeFrames;
      internal long ChunkedScopeFrames;
    }

    private sealed record FlowComparison(
      string Flow,
      double ClosedBeforeRate,
      double OpenRate,
      double ClosedAfterRate,
      double Delta,
      long OpenCombinedScope);

    private sealed record SignatureComparison(
      string Signature,
      double ClosedBeforeRate,
      double OpenRate,
      double ClosedAfterRate,
      double Delta);

    private sealed record ProbeEvent(
      double OffsetSeconds,
      bool RadioToPc,
      int RadioPort,
      int LocalPort,
      int PayloadLength,
      string? CivSignature,
      string PayloadHex);
  }

  internal readonly record struct ProbePacketInfo(
    bool RadioToPc,
    int RadioPort,
    int LocalPort,
    int PacketLength,
    int PayloadLength,
    ushort? OuterType,
    byte? TransportMarker,
    int? DeclaredCivLength,
    string? CivSignature,
    string PacketSignature,
    string? PayloadHex,
    bool CombinedScope,
    bool ChunkedScope);
}
