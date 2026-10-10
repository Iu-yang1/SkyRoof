using System.Diagnostics;

namespace SkyRoof.CW
{
  /// <summary>
  /// A fully calculated CW waterfall/spectrum frame. The producer never
  /// touches a WinForms control; the UI owns all presentation and painting.
  /// </summary>
  internal readonly record struct CwDisplayFrameResult(
    long TimelineGeneration,
    CwDenoiseMode DenoiseMode,
    CwAudioSpectrumFrame Waterfall,
    CwAudioSpectrumFrame Spectrum,
    double ProcessingMilliseconds = 0);

  /// <summary>
  /// One in-flight display computation at most. Slow HamNoise inference
  /// cannot backlog frames or block the WinForms message pump, nor can it
  /// occupy the receive worker's tracking/inference queue.
  /// </summary>
  internal sealed class CwDisplayFrameProcessor : IDisposable
  {
    private readonly CwAudioWaterfallAnalyzer waterfall;
    private readonly CwAudioWaterfallAnalyzer spectrum;
    private readonly Func<CwAudioSnapshot, CwDenoiseMode,
      CwAudioSnapshot>? transformOverride;
    private HamNoiseAudioDenoiser? denoiser;
    private CwDenoiseMode denoiserMode = CwDenoiseMode.Bypass;
    private Task<CwDisplayFrameResult>? running;
    private bool disposed;
    internal event Action? FrameReady;
    private long busyTicks;
    private long finishedFrames;
    private long processingTicks;
    private long lastProcessingTicks;

    internal long BusyTicks => Interlocked.Read(ref busyTicks);
    internal long FinishedFrames => Interlocked.Read(ref finishedFrames);
    internal double MeanProcessingMilliseconds =>
      FinishedFrames == 0 ? 0 :
      1000.0 * Interlocked.Read(ref processingTicks) /
        Stopwatch.Frequency / FinishedFrames;
    internal double LastProcessingMilliseconds =>
      1000.0 * Interlocked.Read(ref lastProcessingTicks) /
        Stopwatch.Frequency;

    internal void RecordBusyTick() =>
      Interlocked.Increment(ref busyTicks);

    internal CwDisplayFrameProcessor(
      CwAudioWaterfallAnalyzer waterfall,
      CwAudioWaterfallAnalyzer spectrum,
      Func<CwAudioSnapshot, CwDenoiseMode,
        CwAudioSnapshot>? transformOverride = null)
    {
      this.waterfall = waterfall ??
        throw new ArgumentNullException(nameof(waterfall));
      this.spectrum = spectrum ??
        throw new ArgumentNullException(nameof(spectrum));
      this.transformOverride = transformOverride;
    }

    internal bool Busy => running != null;

    /// <summary>
    /// Queue only if the previous job has already been consumed. Caller
    /// supplies a fresh immutable snapshot, avoiding an unbounded queue
    /// when HamNoise takes longer than the display polling interval.
    /// </summary>
    internal bool TryQueue(
      CwAudioSnapshot snapshot,
      long generation,
      CwDenoiseMode mode)
    {
      if (disposed || running != null)
        return false;

      running = Task.Run(() =>
      {
        long start = Stopwatch.GetTimestamp();
        CwAudioSnapshot display =
          transformOverride != null
            ? transformOverride(snapshot, mode)
            : PrepareDisplaySnapshot(snapshot, mode);

        // Both FFTW plans are owned solely by this serialized producer.
        // Compute both traces for each delivered frame: no old 5 Hz cap.
        CwAudioSpectrumFrame waterfallFrame =
          waterfall.Analyze(display);
        CwAudioSpectrumFrame spectrumFrame =
          spectrum.Analyze(display);
        long ticks = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref finishedFrames);
        Interlocked.Add(ref processingTicks, ticks);
        Interlocked.Exchange(ref lastProcessingTicks, ticks);
        return new CwDisplayFrameResult(
          generation, mode, waterfallFrame, spectrumFrame,
          1000.0 * ticks / Stopwatch.Frequency);
      });
      // Notify the UI once a native FFT/HamNoise job is really completed.
      // A WinForms Timer remains as a fallback: event delivery can be
      // throttled or delayed by the message pump. No extra display work is
      // scheduled on this completion thread.
      _ = running.ContinueWith(
        _ =>
        {
          if (!disposed)
            FrameReady?.Invoke();
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
      return true;
    }

    /// <summary>
    /// Non-blocking consumer. GetResult is called only for a completed
    /// task, and propagates errors to the owning UI (which may fall back
    /// from an unavailable denoiser to Raw).
    /// </summary>
    internal bool TryTake(out CwDisplayFrameResult result)
    {
      result = default;
      Task<CwDisplayFrameResult>? task = running;
      if (task == null || !task.IsCompleted)
        return false;

      running = null;
      result = task.GetAwaiter().GetResult();
      return true;
    }

    private CwAudioSnapshot PrepareDisplaySnapshot(
      CwAudioSnapshot raw,
      CwDenoiseMode mode)
    {
      if (mode == CwDenoiseMode.Bypass)
        return raw;

      if (denoiser == null || denoiserMode != mode)
      {
        denoiser = new HamNoiseAudioDenoiser(mode);
        denoiserMode = mode;
      }

      // Display only: raw PCM continues into the tracker and DeepCW.
      // Never execute this resampling / native inference on the UI thread.
      float[] modelRate = CwWindowedSincResampler.Resample(
        raw.Samples, raw.SampleRate, denoiser.SampleRate);
      float[] cleaned = denoiser.Process(
        modelRate, denoiser.SampleRate, wet: 1.0);
      float[] restored = CwWindowedSincResampler.Resample(
        cleaned, denoiser.SampleRate, raw.SampleRate);
      if (restored.Length != raw.Samples.Length)
        Array.Resize(ref restored, raw.Samples.Length);

      return new CwAudioSnapshot(
        raw.SampleRate,
        raw.EndUtc,
        raw.EndSampleIndex,
        restored);
    }

    public void Dispose()
    {
      if (disposed)
        return;
      disposed = true;

      Task<CwDisplayFrameResult>? outstanding = running;
      running = null;
      if (outstanding == null)
      {
        ReleaseAnalyzers();
      }
      else
      {
        // An in-progress native HamNoise call cannot be interrupted.
        // Defer FFTW plan destruction instead of blocking window close
        // or freeing unmanaged buffers while the producer still uses them.
        _ = outstanding.ContinueWith(
          _ => ReleaseAnalyzers(),
          CancellationToken.None,
          TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
      }
    }

    private void ReleaseAnalyzers()
    {
      waterfall.Dispose();
      spectrum.Dispose();
    }
  }
}
