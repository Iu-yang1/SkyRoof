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
    CwAudioSpectrumFrame Spectrum);

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
    /// when HamNoise takes longer than the 50 ms display timer period.
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
        CwAudioSnapshot display =
          transformOverride != null
            ? transformOverride(snapshot, mode)
            : PrepareDisplaySnapshot(snapshot, mode);

        // Both FFTW plans are owned solely by this serialized producer.
        // Compute both traces for each delivered frame: no old 5 Hz cap.
        return new CwDisplayFrameResult(
          generation, mode,
          waterfall.Analyze(display),
          spectrum.Analyze(display));
      });
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
