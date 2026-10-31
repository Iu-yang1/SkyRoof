using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VE3NEA;

namespace SkyRoof.AFC
{
  /// <summary>True 12-kHz IF PCM input, independent of the CW AF decoder.</summary>
  public sealed class AfcIfInput : IDisposable
  {
    private readonly InputSoundcard<float> input = new(
      captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600);
    private readonly LoopbackInputSoundcard loopback = new();
    private readonly object sync = new();
    private readonly object displayGate = new();
    private readonly AfcIfDisplayAnalyzer displayAnalyzer = new();
    private int displayBusy;
    private long lastDisplayMs;
    private volatile bool displayEnabled;
    public bool DisplayEnabled
    {
      get => displayEnabled;
      set => displayEnabled = value;
    }
    public event Action<AfcIfDisplayFrame>? DisplayFrameAvailable;

    public void ConfigureDisplay(bool blackmanHarris, bool haloReduction,
      double smoothingMs)
    {
      lock (displayGate)
        displayAnalyzer.Configure(blackmanHarris, haloReduction, smoothingMs);
    }
    private readonly float[] buffer = new float[96000];
    private int write, used, busy;
    private long generation, lastAnalysisMs;
    private DateTime lastUtc;
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
    internal static DateTime NormalizePacketUtc(DateTime incoming, DateTime previous,
      int samples, out bool discontinuity)
    {
      discontinuity = false;
      if (incoming.Kind != DateTimeKind.Utc)
        incoming = incoming.Kind == DateTimeKind.Unspecified
          ? DateTime.SpecifyKind(incoming, DateTimeKind.Utc)
          : incoming.ToUniversalTime();
      if (previous == default || incoming > previous) return incoming;
      if ((previous - incoming).TotalMilliseconds <= 250)
        return previous.AddSeconds(Math.Max(1, samples) / 48000.0);
      discontinuity = true;
      return incoming;
    }
    private AfcSignalProcessing processor = new();
    private AfcSignalMode mode;
    private bool pilot;
    private double targetOffset;

    public event Action<AfcObservation>? ObservationAvailable;
    public bool IsRunning => input.IsRunning || loopback.IsRunning;
    public long Generation { get { lock (sync) return generation; } }
    public string DeviceName => loopback.IsRunning ? loopback.GetDisplayName() : input.GetDisplayName();

    public AfcIfInput()
    {
      input.Retry = true;
      loopback.Retry = true;
      input.SamplesAvailable += OnSamples;
      loopback.SamplesAvailable += OnSamples;
    }

    public void Start(string? device, double referenceHz,
      AfcSignalMode signalMode, bool ssbPilot, double offset,
      AfcIfSource source = AfcIfSource.WasapiCapture, string? loopbackDevice = null)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (source == AfcIfSource.WasapiCapture && string.IsNullOrWhiteSpace(device))
        throw new ArgumentException("Select the 12-kHz IF WASAPI input.");
      if (source == AfcIfSource.PlaybackLoopback && string.IsNullOrWhiteSpace(loopbackDevice))
        throw new ArgumentException("Select the remote IF playback endpoint.");
      var newProcessor = new AfcSignalProcessing(48000, referenceHz);
      input.Enabled = false;
      loopback.Enabled = false;
      if (source == AfcIfSource.PlaybackLoopback)
        loopback.SetDeviceId(loopbackDevice);
      else input.SetDeviceId(device);
      lock (sync)
      {
        generation++;
        write = used = 0;
        lastUtc = default;
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

    public void UpdateTarget(AfcSignalMode signalMode, bool ssbPilot, double offset)
    {
      lock (sync)
      {
        if (mode != signalMode || pilot != ssbPilot || Math.Abs(offset - targetOffset) > .5)
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
        lastUtc = lastResultUtc = default;
        latestObservation = null;
        scheduledAnalyses = completedAnalyses = failedAnalyses = 0;
        lastAnalysisMs = 0;
      }
    }

    public void Stop()
    {
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
        bool refPresent = false;
        double expected = 0;
        AfcSignalProcessing analyzer;
        lock (sync)
        {
          if (disposed) return;
          int accepted = Math.Min(e.Count, e.Data.Length);
          packetUtc = NormalizePacketUtc(e.Utc, lastUtc, accepted,
            out bool discontinuity);
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
          // Original measurement/CAT control scheduling remains unchanged.
          // UI FFT is deliberately independent of the ~550ms AFC loop.
          if (used >= 24000 && tick - lastAnalysisMs >= 550 &&
              Interlocked.CompareExchange(ref busy, 1, 0) == 0)
          {
            lastAnalysisMs = tick;
            scheduledAnalyses++;
            pcm = new float[used];
            int start = (write - used + buffer.Length) % buffer.Length;
            for (int i = 0; i < used; i++)
              pcm[i] = buffer[(start + i) % buffer.Length];
            selected = mode;
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
            double centerLagSeconds = selected == AfcSignalMode.FM
              ? Math.Min(data.Length / 48000.0, 2.0) / 2.0
              : 8192.0 / 24000 / 2.0;
            DateTime analysisUtc = packetUtc.AddSeconds(-centerLagSeconds);
            AfcObservation obs = analyzer.Analyze(data, selected, analysisUtc,
              version, expected, refPresent);
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
