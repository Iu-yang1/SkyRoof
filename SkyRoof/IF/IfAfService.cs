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
    double ProcessingQueueLagMs, string Detail);

  /// <summary>Single-owner 48-kHz IF WASAPI stream; fans out unmodified IF
  /// to AFC and SSTV synchronously and produces filtered AF on a separate
  /// bounded worker for VAC. No downstream processor opens the same device
  /// when it can subscribe to this service.</summary>
  public sealed class IfAfService : IDisposable
  {
    private InputSoundcard<float>? capture;
    private readonly OutputSoundcard<float> vac = new(samplingRate: 48000);
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
    private string detail = "Stopped";
    private bool disposed;
    public event EventHandler<DataEventArgs<float>>? RawIfAvailable;
    private readonly record struct Frame(float[] Data, DateTime Utc, int Version);

    public bool IsRunning => capture?.IsRunning == true;
    public string? InputDeviceId => settings?.InputDeviceId;
    public string? VacRenderDeviceId => settings?.VacRenderDeviceId;
    public AudioChannelChoice CaptureChannel => settings?.CaptureChannel ?? AudioChannelChoice.Left;
    public IfAfStatus Status => new(IsRunning, (IfAfMode)Volatile.Read(ref requestedMode),
      Interlocked.Read(ref inputSamples), Interlocked.Read(ref outputSamples),
      Interlocked.Read(ref dropped), Interlocked.Read(ref clipped),
      new DateTime(Math.Max(0, Interlocked.Read(ref lastUtcTicks)), DateTimeKind.Utc),
      Volatile.Read(ref lastRmsDbfs),
      Interlocked.Read(ref lastProcessingLagUs) / 1000.0, detail);

    public void Apply(IfAfSettings config, IfAfMode mode)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      ArgumentNullException.ThrowIfNull(config);
      Stop();
      if (!config.Enabled) return;
      if (string.IsNullOrWhiteSpace(config.InputDeviceId) ||
          string.IsNullOrWhiteSpace(config.VacRenderDeviceId))
        throw new InvalidOperationException("Select both the IF capture and VAC render endpoints.");
      if (config.FmDeviationHz is < 1000 or > 6000 ||
          config.GainDb is < -40 or > 12 ||
          config.SsbLowHz is < 100 or > 750 ||
          config.SsbHighHz is < 1800 or > 4000 ||
          config.SsbHighHz - config.SsbLowHz < 1200)
        throw new ArgumentOutOfRangeException(nameof(config),
          "FM deviation or output gain outside the safe test range.");
      // Never play the demodulated receive audio into a radio's TX CODEC.
      var vacEntry = Soundcard.ListDevices(NAudio.CoreAudioApi.DataFlow.Render);
      AudioDeviceEntry? target = Array.Find(vacEntry,
        x => string.Equals(x.Id, config.VacRenderDeviceId, StringComparison.Ordinal));
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
        GainDb = config.GainDb,
        FmDeviationHz = config.FmDeviationHz,
        SsbLowHz = config.SsbLowHz,
        SsbHighHz = config.SsbHighHz
      };
      Volatile.Write(ref requestedMode, (int)mode);
      int version = ++generation;
      Interlocked.Exchange(ref inputSamples, 0);
      Interlocked.Exchange(ref outputSamples, 0);
      Interlocked.Exchange(ref dropped, 0);
      Interlocked.Exchange(ref clipped, 0);
      Interlocked.Exchange(ref lastProcessingLagUs, 0);
      detail = "Starting";
      var next = Channel.CreateBounded<Frame>(new BoundedChannelOptions(20)
      {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
      });
      var source = new CancellationTokenSource();
      queue = next;
      cancel = source;
      capture = new InputSoundcard<float>(config.InputDeviceId, 48000,
        captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600,
        channelChoice: config.CaptureChannel);
      capture.SamplesAvailable += CaptureSamples;
      try
      {
        vac.SetDeviceId(config.VacRenderDeviceId);
        vac.Volume = 1;
        vac.Enabled = true;
        if (!vac.IsRunning) throw new InvalidOperationException("Virtual audio playback unavailable.");
        worker = Task.Run(() => RunAsync(next.Reader, source.Token, version,
          config.FmDeviationHz, config.GainDb,
          config.SsbLowHz, config.SsbHighHz));
        capture.Enabled = true;
        if (!capture.IsRunning) throw new InvalidOperationException("IF capture unavailable.");
        detail = "48-kHz IF -> independent VAC AF";
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
      Interlocked.Add(ref inputSamples, count);
      Interlocked.Exchange(ref lastUtcTicks, e.Utc.Ticks);
      if (!target.Writer.TryWrite(new Frame(data, e.Utc, generation)))
      {
        Interlocked.Increment(ref dropped);
        detail = "PCM overrun; decoder will reset on discontinuity";
      }
    }

    private async Task RunAsync(ChannelReader<Frame> reader, CancellationToken stop,
      int version, int deviation, int gainDb,
      int ssbLowHz, int ssbHighHz)
    {
      var dsp = new IfAfDemodulator((IfAfMode)Volatile.Read(ref requestedMode),
        deviation, ssbLowHz, ssbHighHz);
      double scale = Math.Pow(10, gainDb / 20.0);
      DateTime previous = default;
      int previousSamples = 0;
      try
      {
        await foreach (var frame in reader.ReadAllAsync(stop))
        {
          if (version != generation) return;
          IfAfMode mode = (IfAfMode)Volatile.Read(ref requestedMode);
          if (mode != dsp.Mode) dsp.Configure(mode);
          // Timestamp of captured block is its end. Discontinuities should
          // not splice unrelated IF phases into one decodable WSJT symbol.
          if (previous != default)
          {
            double elapsed = (frame.Utc - previous).TotalSeconds;
            double expected = previousSamples / 48000.0;
            if (elapsed < -0.05 || elapsed > expected + .25)
              dsp.Reset();
          }
          previous = frame.Utc;
          previousSamples = frame.Data.Length;
          double queueLagMs = (DateTime.UtcNow - frame.Utc).TotalMilliseconds;
          if (queueLagMs >= 0 && queueLagMs < 60000)
            Interlocked.Exchange(ref lastProcessingLagUs,
              (long)Math.Round(queueLagMs * 1000));
          float[] af = new float[frame.Data.Length];
          dsp.Process(frame.Data, af);
          double power = 0;
          for (int i = 0; i < af.Length; i++)
          {
            double v = af[i] * scale;
            power += frame.Data[i] * frame.Data[i];
            if (Math.Abs(v) > .999)
            {
              Interlocked.Increment(ref clipped);
              v = Math.Clamp(v, -.999, .999);
            }
            af[i] = (float)v;
          }
          Volatile.Write(ref lastRmsDbfs, 10 *
            Math.Log10(Math.Max(power / frame.Data.Length, 1e-15)));
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
