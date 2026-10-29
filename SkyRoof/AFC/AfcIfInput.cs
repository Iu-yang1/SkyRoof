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
    private readonly float[] buffer = new float[96000];
    private int write, used, busy;
    private long generation, lastAnalysisMs;
    private DateTime lastUtc;
    private bool disposed;
    private AfcSignalProcessing processor = new();
    private AfcSignalMode mode;
    private bool pilot;
    private double targetOffset;

    public event Action<AfcObservation>? ObservationAvailable;
    public bool IsRunning => input.IsRunning || loopback.IsRunning;
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
        lastUtc = default;
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
        lastUtc = default;
      }
    }

    private void OnSamples(object? sender, DataEventArgs<float> e)
    {
      try
      {
        if (disposed || e.Count <= 0) return;
        float[]? pcm = null;
        long gen = 0;
        AfcSignalMode selected = default;
        bool refPresent = false;
        double expected = 0;
        AfcSignalProcessing analyzer;
        lock (sync)
        {
          if (lastUtc != default && e.Utc <= lastUtc)
          {
            generation++;
            write = used = 0;
          }
          lastUtc = e.Utc;
          for (int i = 0; i < Math.Min(e.Count, e.Data.Length); i++)
          {
            buffer[write] = e.Data[i];
            write = (write + 1) % buffer.Length;
            used = Math.Min(used + 1, buffer.Length);
          }
          long tick = Environment.TickCount64;
          if (used >= 24000 && tick - lastAnalysisMs >= 550 &&
              Interlocked.CompareExchange(ref busy, 1, 0) == 0)
          {
            lastAnalysisMs = tick;
            pcm = new float[used];
            int start = (write - used + buffer.Length) % buffer.Length;
            for (int i = 0; i < used; i++)
              pcm[i] = buffer[(start + i) % buffer.Length];
            gen = generation;
            selected = mode;
            refPresent = pilot;
            expected = targetOffset;
          }
          analyzer = processor;
        }
        if (pcm == null) return;
        float[] data = pcm;
        long version = gen;
        DateTime packetUtc = e.Utc;
        _ = Task.Run(() =>
        {
          try
          {
            AfcObservation obs = analyzer.Analyze(data, selected, packetUtc,
              version, expected, refPresent);
            lock (sync) if (disposed || version != generation) return;
            ObservationAvailable?.Invoke(obs);
          }
          catch (Exception ex)
          {
            Log.Warning(ex, "AFC IF DSP failed");
          }
          finally
          {
            Interlocked.Exchange(ref busy, 0);
          }
        });
      }
      catch (Exception ex)
      {
        Interlocked.Exchange(ref busy, 0);
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
      GC.SuppressFinalize(this);
    }
  }
}
