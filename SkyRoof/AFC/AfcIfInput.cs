using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VE3NEA;
using SkyRoof.IF;

namespace SkyRoof.AFC
{
  /// <summary>True 12-kHz IF PCM input, independent of the CW AF decoder.</summary>
  public sealed class AfcIfInput : IDisposable
  {
    private InputSoundcard<float> input = new(
      captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600,
      channelChoice: AudioChannelChoice.Left);
    private AudioChannelChoice captureChannel = AudioChannelChoice.Left;
    private readonly LoopbackInputSoundcard loopback = new();
    private IfAfService? sharedIf;
    private readonly object sync = new();
    private readonly object displayGate = new();
    private readonly AfcIfDisplayAnalyzer displayAnalyzer = new();
    private int displayBusy;
    private int acquisitionPreset = (int)AfcAcquisitionPreset.Adaptive;
    private long lastDisplayMs;
    private volatile bool displayEnabled;
    public bool DisplayEnabled
    {
      get => displayEnabled;
      set => displayEnabled = value;
    }
    public event Action<AfcIfDisplayFrame>? DisplayFrameAvailable;

    /// <summary>Changes the strategy without reopening USB IF. Discards
    /// previously buffered windows so that the next estimate is fresh.</summary>
    public void SetAcquisitionPreset(AfcAcquisitionPreset preset)
    {
      if (!Enum.IsDefined(typeof(AfcAcquisitionPreset), preset))
        throw new ArgumentOutOfRangeException(nameof(preset));
      lock (sync)
      {
        if (acquisitionPreset == (int)preset) return;
        acquisitionPreset = (int)preset;
        generation++;
        write = used = 0;
        lastResultUtc = default;
        latestObservation = null;
        lastAnalysisMs = 0;
      }
    }

    public AfcAcquisitionPreset AcquisitionPreset
    {
      get { lock (sync) return (AfcAcquisitionPreset)acquisitionPreset; }
    }

    public void ConfigureDisplay(bool blackmanHarris, bool haloReduction,
      double smoothingMs)
    {
      lock (displayGate)
        displayAnalyzer.Configure(blackmanHarris, haloReduction, smoothingMs);
    }
    private readonly float[] buffer = new float[96000];
    private int write, used, busy;
    private long generation, lastAnalysisMs;
    private DateTime lastUtc, lastSourceUtc;
    private long totalSamples, scheduledAnalyses, completedAnalyses, failedAnalyses;
    private DateTime lastResultUtc;
    private AfcObservation? latestObservation;
    private bool disposed;

    /// <summary>
    /// Pipeline health independent from display FFT; a visible spectrum does
    /// not prove that a 0.5–2 second AFC measurement was ever completed.
    /// </summary>
    public readonly record struct PipelineStatus(
      long Samples, int BufferedSamples, long Scheduled, long Completed,
      long Failed, DateTime LastMeasurementUtc, AfcObservation? LastObservation);

    public PipelineStatus Diagnostics
    {
      get
      {
        lock (sync) return new(totalSamples, used, scheduledAnalyses,
          completedAnalyses, failedAnalyses, lastResultUtc, latestObservation);
      }
    }

    /// <summary>
    /// Audio callback times can repeat when the device reports several
    /// adjacent blocks with the same timestamp resolution. Such duplicates
    /// are not new PCM generations and must not discard the IF accumulator.
    /// A genuine backwards jump is still recognized as a discontinuity.
    /// </summary>
    internal static DateTime NormalizePacketUtc(DateTime incoming,
      DateTime previousNormalized, DateTime previousSource, int samples,
      out bool discontinuity)
    {
      discontinuity = false;
      if (incoming.Kind != DateTimeKind.Utc)
        incoming = incoming.Kind == DateTimeKind.Unspecified
          ? DateTime.SpecifyKind(incoming, DateTimeKind.Utc)
          : incoming.ToUniversalTime();
      if (previousNormalized == default) return incoming;
      // Compare original hardware timestamps to each other, not to the
      // synthesized PCM clock. If WASAPI repeats a timestamp for twenty
      // consecutive 33-ms buffers, normalized time is now ~660 ms ahead
      // of that source stamp; that is NOT a backwards device jump.
      if (previousSource != default &&
          (previousSource - incoming).TotalMilliseconds > 250)
      {
        discontinuity = true;
        return incoming;
      }
      DateTime next = previousNormalized.AddSeconds(
        Math.Max(1, samples) / 48000.0);
      // Never let coarse-grained timestamps rewind time; if hardware time
      // leaps forward, take its newer absolute UTC instead.
      return incoming > next ? incoming : next;
    }
    private AfcSignalProcessing processor = new();
    private AfcSignalMode mode;
    private bool pilot;
    private double targetOffset;

    public event Action<AfcObservation>? ObservationAvailable;
    public bool IsRunning => input.IsRunning || loopback.IsRunning ||
      sharedIf?.IsRunning == true;
    public long Generation { get { lock (sync) return generation; } }
    public string DeviceName => sharedIf?.IsRunning == true
      ? "Shared USB 12 kHz IF" : loopback.IsRunning ? loopback.GetDisplayName()
        : input.GetDisplayName();

    public AfcIfInput()
    {
      input.Retry = true;
      loopback.Retry = true;
      input.SamplesAvailable += OnSamples;
      loopback.SamplesAvailable += OnSamples;
    }

    public void Start(string? device, double referenceHz,
      AfcSignalMode signalMode, bool ssbPilot, double offset,
      AfcIfSource source = AfcIfSource.WasapiCapture, string? loopbackDevice = null,
      AudioChannelChoice ifChannel = AudioChannelChoice.Left)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (source == AfcIfSource.WasapiCapture && string.IsNullOrWhiteSpace(device))
        throw new ArgumentException("Select the 12-kHz IF WASAPI input.");
      if (source == AfcIfSource.PlaybackLoopback && string.IsNullOrWhiteSpace(loopbackDevice))
        throw new ArgumentException("Select the remote IF playback endpoint.");
      var newProcessor = new AfcSignalProcessing(48000, referenceHz);
      DetachShared();
      input.Enabled = false;
      loopback.Enabled = false;
      if (source == AfcIfSource.WasapiCapture && ifChannel != captureChannel)
      {
        // The channel selector is part of the WASAPI reader pipeline.
        // Recreate it only on an explicit operator channel change.
        input.SamplesAvailable -= OnSamples;
        input.Dispose();
        input = new InputSoundcard<float>(
          captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600,
          channelChoice: ifChannel) { Retry = true };
        input.SamplesAvailable += OnSamples;
        captureChannel = ifChannel;
      }
      if (source == AfcIfSource.PlaybackLoopback)
        loopback.SetDeviceId(loopbackDevice);
      else input.SetDeviceId(device);
      lock (sync)
      {
        generation++;
        write = used = 0;
        lastUtc = lastSourceUtc = default;
        lastResultUtc = default;
        latestObservation = null;
        totalSamples = scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
        lastDisplayMs = 0;
        processor = newProcessor;
        mode = signalMode;
        pilot = ssbPilot;
        targetOffset = offset;
      }
      if (source == AfcIfSource.PlaybackLoopback) loopback.Enabled = true;
      else input.Enabled = true;
    }

    public void StartShared(IfAfService hub, double referenceHz,
      AfcSignalMode signalMode, bool ssbPilot, double offset)
    {
      ArgumentNullException.ThrowIfNull(hub);
      if (!hub.IsRunning) throw new InvalidOperationException("Shared IF input stopped");
      Stop();
      lock (sync)
      {
        generation++;
        processor = new AfcSignalProcessing(48000, referenceHz);
        mode = signalMode;
        pilot = ssbPilot;
        targetOffset = offset;
      }
      sharedIf = hub;
      hub.RawIfAvailable += OnSamples;
    }

    private void DetachShared()
    {
      var hub = sharedIf;
      sharedIf = null;
      if (hub != null) hub.RawIfAvailable -= OnSamples;
    }

    public void UpdateTarget(AfcSignalMode signalMode, bool ssbPilot, double offset)
    {
      lock (sync)
      {
        // NaN is the unrestricted-FM sentinel. Its transitions must
        // invalidate the old measurement generation.
        bool targetChanged = double.IsNaN(offset)
          ? !double.IsNaN(targetOffset)
          : double.IsNaN(targetOffset) || Math.Abs(offset - targetOffset) > .5;
        if (mode != signalMode || pilot != ssbPilot || targetChanged)
        {
          generation++;
          write = used = 0;
          lastResultUtc = default;
          latestObservation = null;
          scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
          lastAnalysisMs = 0;
        }
        mode = signalMode;
        pilot = ssbPilot;
        targetOffset = offset;
      }
    }

    public void InvalidateTarget()
    {
      // Keep WASAPI open, but never reinterpret old PCM as the new satellite,
      // transponder, IF source, or selected CW lane.
      lock (sync)
      {
        generation++;
        write = used = 0;
        lastUtc = lastSourceUtc = lastResultUtc = default;
        latestObservation = null;
        scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
        lastAnalysisMs = 0;
      }
    }

    public void Stop()
    {
      DetachShared();
      input.Enabled = false;
      loopback.Enabled = false;
      lock (sync)
      {
        generation++;
        write = used = 0;
        lastUtc = lastResultUtc = default;
        latestObservation = null;
        scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
        lastAnalysisMs = 0;
      }
    }

    private void OnSamples(object? sender, DataEventArgs<float> e)
    {
      try
      {
        if (disposed || e.Count <= 0) return;
        float[]? pcm = null, displayPcm = null;
        long gen = 0;
        DateTime packetUtc;
        AfcSignalMode selected = default;
        AfcAcquisitionPreset preset = AfcAcquisitionPreset.Adaptive;
        bool refPresent = false;
        double expected = 0;
        AfcSignalProcessing analyzer;
        lock (sync)
        {
          if (disposed) return;
          int accepted = Math.Min(e.Count, e.Data.Length);
          packetUtc = NormalizePacketUtc(e.Utc, lastUtc, lastSourceUtc,
            accepted, out bool discontinuity);
          if (discontinuity)
          {
            generation++;
            write = used = 0;
            lastResultUtc = default;
            latestObservation = null;
            scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
            lastAnalysisMs = 0;
          }
          lastUtc = packetUtc;
          lastSourceUtc = e.Utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(e.Utc, DateTimeKind.Utc) :
            e.Utc.ToUniversalTime();
          totalSamples += accepted;
          for (int i = 0; i < accepted; i++)
          {
            buffer[write] = e.Data[i];
            write = (write + 1) % buffer.Length;
            used = Math.Min(used + 1, buffer.Length);
          }
          long tick = Environment.TickCount64;
          gen = generation;
          if (displayEnabled && used >= AfcIfDisplayAnalyzer.FftSize &&
              tick - lastDisplayMs >= 65 &&
              Interlocked.CompareExchange(ref displayBusy, 1, 0) == 0)
          {
            lastDisplayMs = tick;
            int n = AfcIfDisplayAnalyzer.FftSize;
            displayPcm = new float[n];
            int start = (write - n + buffer.Length) % buffer.Length;
            for (int i = 0; i < n; i++)
              displayPcm[i] = buffer[(start + i) % buffer.Length];
          }
          // Mode-specific short bursts. Never analyze 2 seconds of stale
          // FM when a 400-ms SO-50 voice transmission is all we have.
          // CW and piloted SSB have dedicated (slightly longer) windows.
          var profile = AfcTrackingProfile.For(mode, pilot,
            (AfcAcquisitionPreset)acquisitionPreset);
          if (used >= profile.MinimumSamples &&
              tick - lastAnalysisMs >= profile.CadenceMilliseconds &&
              Interlocked.CompareExchange(ref busy, 1, 0) == 0)
          {
            lastAnalysisMs = tick;
            scheduledAnalyses++;
            int n = Math.Min(used, profile.WindowSamples);
            pcm = new float[n];
            int start = (write - n + buffer.Length) % buffer.Length;
            for (int i = 0; i < n; i++)
              pcm[i] = buffer[(start + i) % buffer.Length];
            selected = mode;
            preset = (AfcAcquisitionPreset)acquisitionPreset;
            refPresent = pilot;
            expected = targetOffset;
          }
          analyzer = processor;
        }
        if (displayPcm != null)
        {
          float[] displayData = displayPcm;
          _ = Task.Run(() =>
          {
            try
            {
              AfcIfDisplayFrame frame;
              lock (displayGate)
                frame = displayAnalyzer.Analyze(displayData, packetUtc, gen);
              lock (sync)
                if (disposed || gen != generation || !displayEnabled) return;
              DisplayFrameAvailable?.Invoke(frame);
            }
            catch (Exception ex)
            {
              if (!disposed) Log.Warning(ex, "AFC display-only IF FFT failed");
            }
            finally { Interlocked.Exchange(ref displayBusy, 0); }
          });
        }
        if (pcm == null) return;
        float[] data = pcm;
        long version = gen;
        _ = Task.Run(() =>
        {
          try
          {
            // Timestamp the physical center of this actual short window.
            // Different modes do NOT share a fictitious 2-second lag.
            double centerLagSeconds = data.Length / 48000.0 / 2.0;
            DateTime analysisUtc = packetUtc.AddSeconds(-centerLagSeconds);
            AfcObservation obs = analyzer.Analyze(data, selected, analysisUtc,
              version, expected, refPresent, preset);
            lock (sync)
            {
              if (disposed || version != generation) return;
              completedAnalyses++;
              lastResultUtc = obs.SampleUtc;
              latestObservation = obs;
            }
            ObservationAvailable?.Invoke(obs);
          }
          catch (Exception ex)
          {
            lock (sync) failedAnalyses++;
            if (!disposed) Log.Warning(ex, "AFC IF DSP failed");
          }
          finally { Interlocked.Exchange(ref busy, 0); }
        });
      }
      catch (Exception ex)
      {
        Interlocked.Exchange(ref busy, 0);
        Interlocked.Exchange(ref displayBusy, 0);
        Log.Warning(ex, "AFC IF capture failed");
      }
    }

    public void Dispose()
    {
      if (disposed) return;
      Stop();
      disposed = true;
      input.SamplesAvailable -= OnSamples;
      loopback.SamplesAvailable -= OnSamples;
      input.Dispose();
      loopback.Dispose();
      lock (displayGate) displayAnalyzer.Dispose();
      GC.SuppressFinalize(this);
    }
  }
}
