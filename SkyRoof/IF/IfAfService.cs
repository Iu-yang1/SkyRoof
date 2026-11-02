using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using VE3NEA;

namespace SkyRoof.IF
{
  public readonly record struct IfAfStatus(bool Running, IfAfMode Mode,
    long InputSamples, long OutputSamples, long DroppedBlocks,
    long ClippedSamples, DateTime LastInputUtc, double InputRmsDbfs,
    double ProcessingQueueLagMs, string Detail,
    int RequestedRenderPeriodMs, bool RenderRunning,
    string VirtualDeviceStatus, long OutputUnderrunSamples,
    double OutputRmsDbfs = double.NaN,
    double OutputPeakBeforeLimitDbfs = double.NaN,
    long LastBlockClippedSamples = 0,
    double AppliedAfGainDb = double.NaN,
    double PeakReductionDb = 0,
    long PeakLimitedBlocks = 0);

  /// <summary>Single-owner 48-kHz IF WASAPI stream; fans out unmodified IF
  /// to AFC and SSTV synchronously and produces filtered AF on a separate
  /// bounded worker for VAC. No downstream processor opens the same device
  /// when it can subscribe to this service.</summary>
  public sealed class IfAfService : IDisposable
  {
    private InputSoundcard<float>? capture;
    private OutputSoundcard<float> vac = new(samplingRate: 48000,
      latencyMilliseconds: 40, padSilenceOnUnderrun: true);
    private string? resolvedRenderId;
    private string virtualDeviceStatus = "Not probed";
    private Channel<Frame>? queue;
    private CancellationTokenSource? cancel;
    private Task? worker;
    private long lastProcessingLagUs;
    private IfAfSettings? settings;
    private int requestedMode;
    private int generation;
    private long inputSamples, outputSamples, dropped, clipped;
    private long lastUtcTicks;
    private double lastRmsDbfs = double.NegativeInfinity;
    private double lastOutputRmsDbfs = double.NegativeInfinity;
    private double lastOutputPeakBeforeLimitDbfs = double.NegativeInfinity;
    private long lastBlockClippedSamples;
    private long peakLimitedBlocks;
    private double lastAppliedAfGainDb;
    private double lastPeakReductionDb;
    private string detail = "Stopped";
    private bool disposed;
    public event EventHandler<DataEventArgs<float>>? RawIfAvailable;
    private readonly record struct Frame(float[] Data, DateTime Utc,
      int Version, long FirstSampleIndex);

    // A missing 10-ms chunk does not exceed the 250-ms timestamp tolerance.
    internal static bool FramesContinuous(long expectedStart, long actualStart) =>
      expectedStart < 0 || expectedStart == actualStart;

    public bool IsRunning => capture?.IsRunning == true && vac.IsRunning;
    public string? InputDeviceId => settings?.InputDeviceId;
    public string? VacRenderDeviceId => resolvedRenderId;
    public AudioChannelChoice CaptureChannel => settings?.CaptureChannel ?? AudioChannelChoice.Left;
    public IfAfStatus Status => new(IsRunning, (IfAfMode)Volatile.Read(ref requestedMode),
      Interlocked.Read(ref inputSamples), Interlocked.Read(ref outputSamples),
      Interlocked.Read(ref dropped), Interlocked.Read(ref clipped),
      new DateTime(Math.Max(0, Interlocked.Read(ref lastUtcTicks)), DateTimeKind.Utc),
      Volatile.Read(ref lastRmsDbfs),
      Interlocked.Read(ref lastProcessingLagUs) / 1000.0, detail,
      vac.RequestedLatencyMs, vac.IsRunning, virtualDeviceStatus,
      vac.UnderrunSamples,
      Volatile.Read(ref lastOutputRmsDbfs),
      Volatile.Read(ref lastOutputPeakBeforeLimitDbfs),
      Interlocked.Read(ref lastBlockClippedSamples),
      Volatile.Read(ref lastAppliedAfGainDb),
      Volatile.Read(ref lastPeakReductionDb),
      Interlocked.Read(ref peakLimitedBlocks));

    public void Apply(IfAfSettings config, IfAfMode mode)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      ArgumentNullException.ThrowIfNull(config);
      Stop();
      if (!config.Enabled) return;
      if (string.IsNullOrWhiteSpace(config.InputDeviceId))
        throw new InvalidOperationException("Select the IF capture endpoint.");
      if (!Enum.IsDefined(typeof(IfAfLatencyPreset), config.LatencyPreset) ||
          !Enum.IsDefined(typeof(IfAfAudioRoute), config.AudioRoute))
        throw new ArgumentOutOfRangeException(nameof(config),
          "Invalid IF-AF audio route or buffer preset.");
      var signed = config.AudioRoute == IfAfAudioRoute.AutoSignedCable
        ? IfAfSignedCableDiscovery.Detect()
        : default;
      virtualDeviceStatus = config.AudioRoute == IfAfAudioRoute.AutoSignedCable
        ? signed.Status
        : "Operator-selected virtual render endpoint (capture pair not verified)";
      string? outputId = IfAfSettings.ResolveRenderEndpoint(config.AudioRoute,
        config.VacRenderDeviceId, signed.RenderId);
      // This recording end belongs to WSJT-X; using it as IF creates feedback.
      if (config.AudioRoute == IfAfAudioRoute.AutoSignedCable &&
          string.Equals(config.InputDeviceId, signed.CaptureId,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
          "IF capture must not be the selected virtual cable recording endpoint. Select IC-9700 USB IF.");
      if (config.AudioRoute == IfAfAudioRoute.AutoSignedCable && !signed.Ready)
        throw new InvalidOperationException(signed.Status +
          " Get VB-CABLE from https://vb-audio.com/Cable/ or use ExistingVirtualCable.");
      if (string.IsNullOrWhiteSpace(outputId))
        throw new InvalidOperationException("Choose a virtual audio render device.");
      if (!Enum.IsDefined(typeof(IfAfSsbBandwidth), config.SsbBandwidth) ||
          !Enum.IsDefined(typeof(IfAfCwBandwidth), config.CwBandwidth) ||
          !Enum.IsDefined(typeof(IfAfFmBandwidth), config.FmBandwidth) ||
          config.FmGainDb is < -40 or > 12 ||
          config.SsbGainDb is < -40 or > 24 ||
          config.CwGainDb is < -40 or > 24 ||
          config.SsbCwAgcTargetDbfs is < -36 or > -12 ||
          config.SsbCwAgcMaxGainDb is < 0 or > 36)
        throw new ArgumentOutOfRangeException(nameof(config),
          "Invalid mode filter preset or output gain.");
      IfAfFilterSettings.ValidateAll(config.EffectiveSsbLowHz,
        config.EffectiveSsbHighHz, config.EffectiveCwCenterHz,
        config.EffectiveCwBandwidthHz, config.EffectiveFmIfCutoffHz,
        config.EffectiveFmAfCutoffHz);
      // Never play the demodulated receive audio into a radio's TX CODEC.
      var vacEntry = Soundcard.ListDevices(NAudio.CoreAudioApi.DataFlow.Render);
      AudioDeviceEntry? target = Array.Find(vacEntry,
        x => string.Equals(x.Id, outputId, StringComparison.Ordinal));
      if (target == null ||
          target.Name.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase) ||
          target.Name.Contains("ICOM", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
          "Choose a dedicated virtual cable RENDER endpoint, not the radio USB TX device.");
      settings = new IfAfSettings
      {
        Enabled = config.Enabled,
        InputDeviceId = config.InputDeviceId,
        CaptureChannel = config.CaptureChannel,
        Mode = config.Mode,
        VacRenderDeviceId = config.VacRenderDeviceId,
        AudioRoute = config.AudioRoute,
        LatencyPreset = config.LatencyPreset,
        SsbGainDb = config.SsbGainDb,
        CwGainDb = config.CwGainDb,
        FmGainDb = config.FmGainDb,
        SsbCwAgcEnabled = config.SsbCwAgcEnabled,
        SsbCwAgcTargetDbfs = config.SsbCwAgcTargetDbfs,
        SsbCwAgcMaxGainDb = config.SsbCwAgcMaxGainDb,
        SsbBandwidth = config.SsbBandwidth,
        CwBandwidth = config.CwBandwidth,
        FmBandwidth = config.FmBandwidth
      };
      Volatile.Write(ref requestedMode, (int)mode);
      int version = ++generation;
      Interlocked.Exchange(ref inputSamples, 0);
      Interlocked.Exchange(ref outputSamples, 0);
      Interlocked.Exchange(ref dropped, 0);
      Interlocked.Exchange(ref clipped, 0);
      Interlocked.Exchange(ref lastProcessingLagUs, 0);
      Interlocked.Exchange(ref lastBlockClippedSamples, 0);
      Interlocked.Exchange(ref peakLimitedBlocks, 0);
      Volatile.Write(ref lastAppliedAfGainDb, 0);
      Volatile.Write(ref lastPeakReductionDb, 0);
      Volatile.Write(ref lastRmsDbfs, double.NegativeInfinity);
      Volatile.Write(ref lastOutputRmsDbfs, double.NegativeInfinity);
      Volatile.Write(ref lastOutputPeakBeforeLimitDbfs, double.NegativeInfinity);
      detail = "Starting";
      // 8 x ~10-ms IF chunks caps queueing to ~80 ms. A stalled
      // consumer drops excess frames instead of accumulating seconds of
      // stale audio, which would ruin fast satellite receive tracking.
      var next = Channel.CreateBounded<Frame>(new BoundedChannelOptions(8)
      {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
      });
      var source = new CancellationTokenSource();
      queue = next;
      cancel = source;
      vac.Dispose();
      vac = new OutputSoundcard<float>(samplingRate: 48000,
        latencyMilliseconds: (int)config.LatencyPreset,
        padSilenceOnUnderrun: true);
      resolvedRenderId = outputId;
      capture = new InputSoundcard<float>(config.InputDeviceId, 48000,
        captureBufferMilliseconds: 20, readerBlockSizeSamples: 480,
        channelChoice: config.CaptureChannel, idleSleepMilliseconds: 2);
      capture.SamplesAvailable += CaptureSamples;
      try
      {
        vac.SetDeviceId(outputId);
        vac.Volume = 1;
        vac.Enabled = true;
        if (!vac.IsRunning) throw new InvalidOperationException("Virtual audio playback unavailable.");
        worker = Task.Run(() => RunAsync(next.Reader, source.Token, version,
          settings!));
        capture.Enabled = true;
        if (!capture.IsRunning) throw new InvalidOperationException("IF capture unavailable.");
        detail = $"48-kHz IF -> {target.Name}, requested render period {(int)config.LatencyPreset} ms";
      }
      catch
      {
        Stop();
        throw;
      }
    }

    public void UpdateMode(IfAfMode mode) =>
      Volatile.Write(ref requestedMode, (int)mode);

    private void CaptureSamples(object? sender, DataEventArgs<float> e)
    {
      if (disposed || e.Count <= 0) return;
      var target = queue;
      if (target == null) return;
      int count = Math.Min(e.Count, e.Data.Length);
      try { RawIfAvailable?.Invoke(this, e); }
      catch (Exception ex) { Log.Warning(ex, "IF shared consumer failed"); }
      var data = new float[count];
      Array.Copy(e.Data, data, count);
      long startIndex = Interlocked.Add(ref inputSamples, count) - count;
      Interlocked.Exchange(ref lastUtcTicks, e.Utc.Ticks);
      if (!target.Writer.TryWrite(new Frame(data, e.Utc, generation, startIndex)))
      {
        Interlocked.Increment(ref dropped);
        detail = "PCM overrun; decoder will reset on discontinuity";
      }
    }

    private async Task RunAsync(ChannelReader<Frame> reader, CancellationToken stop,
      int version, IfAfSettings audioConfig)
    {
      var dsp = new IfAfDemodulator((IfAfMode)Volatile.Read(ref requestedMode),
        audioConfig.EffectiveFmDeviationHz,
        audioConfig.EffectiveSsbLowHz, audioConfig.EffectiveSsbHighHz,
        audioConfig.EffectiveCwCenterHz, audioConfig.EffectiveCwBandwidthHz,
        audioConfig.EffectiveFmIfCutoffHz, audioConfig.EffectiveFmAfCutoffHz);
      var leveler = new IfAfAudioLeveler(audioConfig);
      DateTime previous = default;
      int previousSamples = 0;
      long expectedStart = -1;
      try
      {
        await foreach (var frame in reader.ReadAllAsync(stop))
        {
          if (version != generation) return;
          IfAfMode mode = (IfAfMode)Volatile.Read(ref requestedMode);
          if (mode != dsp.Mode) dsp.Configure(mode);
          // Timestamp of captured block is its end. Discontinuities should
          // not splice unrelated IF phases into one decodable WSJT symbol.
          bool discontinuity = !FramesContinuous(expectedStart, frame.FirstSampleIndex);
          if (previous != default)
          {
            double elapsed = (frame.Utc - previous).TotalSeconds;
            double expected = previousSamples / 48000.0;
            if (elapsed < -0.05 || elapsed > expected + .25)
              discontinuity = true;
          }
          if (discontinuity)
          {
            dsp.Reset();
            leveler.Reset(); // never adapt gain across missing IF samples
          }
          expectedStart = frame.FirstSampleIndex + frame.Data.Length;
          previous = frame.Utc;
          previousSamples = frame.Data.Length;
          double queueLagMs = (DateTime.UtcNow - frame.Utc).TotalMilliseconds;
          if (queueLagMs >= 0 && queueLagMs < 60000)
            Interlocked.Exchange(ref lastProcessingLagUs,
              (long)Math.Round(queueLagMs * 1000));
          float[] af = new float[frame.Data.Length];
          dsp.Process(frame.Data, af);
          double inputPower = 0;
          for (int i = 0; i < frame.Data.Length; i++)
            inputPower += (double)frame.Data[i] * frame.Data[i];
          // The stream-continual DDC and AFC input remain bit-for-bit
          // unchanged. The mode-aware leveler operates on decoded AF only.
          IfAfLevelResult lev = leveler.Process(af, mode);
          if (lev.ClippedSamples > 0)
            Interlocked.Add(ref clipped, lev.ClippedSamples);
          if (lev.PeakLimited) Interlocked.Increment(ref peakLimitedBlocks);
          Volatile.Write(ref lastRmsDbfs, 10 *
            Math.Log10(Math.Max(inputPower / frame.Data.Length, 1e-15)));
          Volatile.Write(ref lastOutputRmsDbfs, lev.OutputRmsDbfs);
          Volatile.Write(ref lastOutputPeakBeforeLimitDbfs, lev.PreLimitPeakDbfs);
          Volatile.Write(ref lastAppliedAfGainDb, lev.EffectiveGainDb);
          Volatile.Write(ref lastPeakReductionDb, lev.PeakReductionDb);
          Interlocked.Exchange(ref lastBlockClippedSamples, lev.ClippedSamples);
          // OutputSoundcard has an independent bounded audio ring; no VAC
          // writes, CAT or UI work on the WASAPI capture callback.
          vac.AddSamples(af, 0, af.Length);
          Interlocked.Add(ref outputSamples, af.Length);
        }
      }
      catch (OperationCanceledException) { }
      catch (Exception ex)
      {
        detail = "IF-AF worker failed: " + ex.Message;
        Log.Error(ex, "IF-AF receiver pipeline failed");
      }
    }

    public void Stop()
    {
      generation++;
      queue?.Writer.TryComplete();
      queue = null;
      cancel?.Cancel();
      cancel = null;
      if (capture != null)
      {
        capture.SamplesAvailable -= CaptureSamples;
        capture.Enabled = false;
        capture.Dispose();
        capture = null;
      }
      vac.Enabled = false;
      settings = null;
      resolvedRenderId = null;
      detail = "Stopped";
    }

    public void Dispose()
    {
      if (disposed) return;
      Stop();
      disposed = true;
      vac.Dispose();
    }
  }
}
