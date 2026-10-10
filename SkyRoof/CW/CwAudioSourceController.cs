using Serilog;
using System.Diagnostics;
using VE3NEA;

namespace SkyRoof.CW
{
  public readonly record struct CwAudioInputStatus(
    bool Enabled,
    CwReceiveAudioSource Source,
    bool Running,
    string DeviceName,
    long AcceptedSamples,
    DateTime? LastAcceptedUtc);

  /// <summary>
  /// Pure source-selection and timeline gate in front of the Pileup frontend.
  /// It contains no hardware APIs, which keeps source-switch semantics fully
  /// testable in CI.
  /// </summary>
  public sealed class CwPcmIngress
  {
    public CwPileupFrontEnd FrontEnd { get; }

    /// <summary>
    /// Raised only after a valid PCM block has entered the audio hub.
    /// Subscribers must post to their own UI/message loop rather than
    /// performing signal processing on the capture callback thread.
    /// </summary>
    public event Action? SamplesAccepted;

    public bool Enabled { get; private set; }
    public CwReceiveAudioSource Source { get; private set; }
    public string? SourceIdentity { get; private set; }
    public long AcceptedSamples { get; private set; }
    public DateTime? LastAcceptedUtc { get; private set; }
    public long TimelineGeneration { get; private set; }

    private long lastAppendTimestamp;
    private long lastAppendIntervalTicks;
    private int lastBlockSamples;
    private long acceptedBlocks;

    public long AcceptedBlocks => Interlocked.Read(ref acceptedBlocks);
    public int LastBlockSamples => Volatile.Read(ref lastBlockSamples);
    public double LastPcmDeliveryIntervalMs =>
      1000.0 * Interlocked.Read(ref lastAppendIntervalTicks) /
        Stopwatch.Frequency;

    public CwPcmIngress(
      CwPileupFrontEnd? frontEnd = null)
    {
      FrontEnd =
        frontEnd ??
        new CwPileupFrontEnd();
    }

    public void Configure(
      bool enabled,
      CwReceiveAudioSource source,
      string? sourceIdentity = null)
    {
      string? normalizedIdentity =
        string.IsNullOrWhiteSpace(sourceIdentity)
          ? null
          : sourceIdentity;

      if (Enabled == enabled &&
          Source == source &&
          string.Equals(
            SourceIdentity,
            normalizedIdentity,
            StringComparison.Ordinal))
        return;

      Enabled = enabled;
      Source = source;
      SourceIdentity = normalizedIdentity;
      ResetTimeline();
    }

    public bool Append(
      CwReceiveAudioSource source,
      float[] data,
      int count,
      DateTime utc)
    {
      ArgumentNullException.ThrowIfNull(data);
      if (count < 0 || count > data.Length)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (utc.Kind != DateTimeKind.Utc)
        throw new ArgumentException(
          "CW PCM timestamps must be UTC.",
          nameof(utc));
      if (!Enabled ||
          source != Source ||
          count == 0)
        return false;

      if (LastAcceptedUtc is DateTime previous &&
          utc < previous)
      {
        // The wall clock can jump backwards after NTP/manual correction.
        // Never feed a non-monotonic timestamp into CwAudioHub; reset the
        // source timeline instead of crashing an audio callback.
        ResetTimeline();
      }

      FrontEnd.AddSamples(
        data,
        count,
        utc);

      // Track actual new-audio delivery separately from FFT/GDI times.
      // On a WASAPI path with a 100-ms update cadence, no timer can paint
      // 30 unique frames/second from fresh PCM. These counters make that
      // cause visible in the CW Spectrum tooltip.
      long now = Stopwatch.GetTimestamp();
      long previousTime = Interlocked.Exchange(ref lastAppendTimestamp, now);
      if (previousTime > 0 && now > previousTime)
        Interlocked.Exchange(ref lastAppendIntervalTicks, now - previousTime);
      Volatile.Write(ref lastBlockSamples, count);
      Interlocked.Increment(ref acceptedBlocks);
      AcceptedSamples += count;
      LastAcceptedUtc = utc;
      // The waterfall display is driven by PCM arrival, not by the
      // quantized WinForms Timer. No FFT or WinForms work happens here.
      SamplesAccepted?.Invoke();
      return true;
    }

    public void ResetTimeline()
    {
      FrontEnd.Reset();
      Interlocked.Exchange(ref lastAppendTimestamp, 0);
      Interlocked.Exchange(ref lastAppendIntervalTicks, 0);
      Interlocked.Exchange(ref acceptedBlocks, 0);
      Volatile.Write(ref lastBlockSamples, 0);
      AcceptedSamples = 0;
      LastAcceptedUtc = null;
      TimelineGeneration++;
    }
  }

  /// <summary>
  /// Owns the optional Windows audio devices for CW receive and routes exactly
  /// one configured source into CwPcmIngress. SDR PCM enters externally from
  /// MainForm/Slicer and therefore never opens a second device.
  /// </summary>
  public sealed class CwAudioSourceController : IDisposable
  {
    private readonly Context ctx;
    // CW waterfall targets a ~33-ms display hop. The legacy soundcard
    // 200-ms WASAPI polling buffer yielded ~100-ms PCM delivery intervals,
    // limiting both Raw and HamNoise to around 10 genuinely new frames/s.
    // Opt in only this CW capture path; ordinary Soundcard users keep
    // their established defaults.
    private readonly InputSoundcard<float> captureInput = new(
      captureBufferMilliseconds: 40,
      readerBlockSizeSamples: 1600);
    private readonly LoopbackInputSoundcard loopbackInput = new();
    private bool disposed;

    public CwPcmIngress Ingress { get; }

    public event EventHandler? StateChanged;

    public CwAudioSourceController(
      Context ctx,
      CwPcmIngress? ingress = null)
    {
      this.ctx =
        ctx ??
        throw new ArgumentNullException(nameof(ctx));
      Ingress =
        ingress ??
        new CwPcmIngress(
          new CwPileupFrontEnd(
            analysisSeconds:
              Math.Clamp(
                ctx.Settings.CwConsole.TrackingAnalysisSeconds,
                0.5,
                10)));

      captureInput.Retry = true;
      loopbackInput.Retry = true;

      captureInput.SamplesAvailable +=
        CaptureInput_SamplesAvailable;
      loopbackInput.SamplesAvailable +=
        LoopbackInput_SamplesAvailable;
      captureInput.StateChanged +=
        Device_StateChanged;
      loopbackInput.StateChanged +=
        Device_StateChanged;
    }

    public void ApplySettings()
    {
      ThrowIfDisposed();

      CwConsoleSettings settings =
        ctx.Settings.CwConsole;

      // Stop device callbacks before changing the timeline/source identity.
      captureInput.Enabled = false;
      loopbackInput.Enabled = false;

      string? loopbackDevice =
        ResolveLoopbackDeviceId(
          settings,
          ctx.Settings.Audio);

      string sourceIdentity =
        settings.AudioSource switch
        {
          CwReceiveAudioSource.SDR =>
            "SDR",
          CwReceiveAudioSource.WasapiCapture =>
            settings.CaptureDeviceId ??
            "<none>",
          CwReceiveAudioSource.RsBa1Loopback =>
            loopbackDevice ??
            "<none>",
          _ => "<unknown>"
        };

      Ingress.Configure(
        settings.ReceiveEnabled,
        settings.AudioSource,
        sourceIdentity);

      captureInput.SetDeviceId(
        settings.CaptureDeviceId);

      loopbackInput.SetDeviceId(
        loopbackDevice);

      if (settings.ReceiveEnabled)
      {
        switch (settings.AudioSource)
        {
          case CwReceiveAudioSource.WasapiCapture:
            if (!string.IsNullOrWhiteSpace(
                  settings.CaptureDeviceId))
              captureInput.Enabled = true;
            break;

          case CwReceiveAudioSource.RsBa1Loopback:
            if (!string.IsNullOrWhiteSpace(
                  loopbackDevice))
              loopbackInput.Enabled = true;
            break;
        }
      }

      StateChanged?.Invoke(
        this,
        EventArgs.Empty);
    }

    public bool AddSamplesFromSdr(
      DataEventArgs<float> args)
    {
      ThrowIfDisposed();
      ArgumentNullException.ThrowIfNull(args);

      return Ingress.Append(
        CwReceiveAudioSource.SDR,
        args.Data,
        args.Count,
        args.Utc);
    }

    public CwAudioInputStatus GetStatus()
    {
      CwConsoleSettings settings =
        ctx.Settings.CwConsole;
      bool running;
      string deviceName;

      switch (settings.AudioSource)
      {
        case CwReceiveAudioSource.SDR:
          running =
            settings.ReceiveEnabled &&
            ctx.Sdr?.IsRunning() == true;
          deviceName =
            ctx.Sdr?.Info?.Name ??
            "SDR";
          break;

        case CwReceiveAudioSource.WasapiCapture:
          running =
            captureInput.IsRunning;
          deviceName =
            captureInput.GetDisplayName();
          break;

        case CwReceiveAudioSource.RsBa1Loopback:
          running =
            loopbackInput.IsRunning;
          deviceName =
            loopbackInput.GetDisplayName();
          break;

        default:
          running = false;
          deviceName = "Unknown";
          break;
      }

      return new(
        Ingress.Enabled,
        Ingress.Source,
        running,
        deviceName,
        Ingress.AcceptedSamples,
        Ingress.LastAcceptedUtc);
    }

    internal static string SourceDisplayName(
      CwReceiveAudioSource source) =>
      source switch
      {
        CwReceiveAudioSource.SDR =>
          "SkyRoof SDR audio",
        CwReceiveAudioSource.WasapiCapture =>
          "IC-9700 USB AF input",
        CwReceiveAudioSource.RsBa1Loopback =>
          "RS-BA1 playback loopback",
        _ => source.ToString()
      };

    internal static string SourceRoleHint(
      CwReceiveAudioSource source) =>
      source switch
      {
        CwReceiveAudioSource.SDR =>
          "internal 48 kHz AF",
        CwReceiveAudioSource.WasapiCapture =>
          "Windows recording input; radio USB output must be AF, not IF",
        CwReceiveAudioSource.RsBa1Loopback =>
          "Windows playback loopback; use the endpoint RS-BA1 plays into",
        _ => string.Empty
      };

    internal static string? ResolveLoopbackDeviceId(
      CwConsoleSettings cw,
      AudioSettings audio)
    {
      ArgumentNullException.ThrowIfNull(cw);
      ArgumentNullException.ThrowIfNull(audio);

      return !string.IsNullOrWhiteSpace(
          cw.RsBa1LoopbackDeviceId)
        ? cw.RsBa1LoopbackDeviceId
        : audio.RsBa1PlaybackDeviceId;
    }

    private void CaptureInput_SamplesAvailable(
      object? sender,
      DataEventArgs<float> e)
    {
      try
      {
        Ingress.Append(
          CwReceiveAudioSource.WasapiCapture,
          e.Data,
          e.Count,
          e.Utc);
      }
      catch (Exception ex)
      {
        // Device reader threads must never be terminated by a downstream DSP
        // exception. Log and leave retry/reconfiguration to the normal path.
        Log.Warning(
          ex,
          "CW WASAPI capture block was rejected.");
      }
    }

    private void LoopbackInput_SamplesAvailable(
      object? sender,
      DataEventArgs<float> e)
    {
      try
      {
        Ingress.Append(
          CwReceiveAudioSource.RsBa1Loopback,
          e.Data,
          e.Count,
          e.Utc);
      }
      catch (Exception ex)
      {
        Log.Warning(
          ex,
          "CW RS-BA1 loopback block was rejected.");
      }
    }

    private void Device_StateChanged(
      object? sender,
      EventArgs e) =>
      StateChanged?.Invoke(
        this,
        EventArgs.Empty);

    private void ThrowIfDisposed()
    {
      ObjectDisposedException.ThrowIf(
        disposed,
        this);
    }

    public void Dispose()
    {
      if (disposed)
        return;
      disposed = true;

      captureInput.SamplesAvailable -=
        CaptureInput_SamplesAvailable;
      loopbackInput.SamplesAvailable -=
        LoopbackInput_SamplesAvailable;
      captureInput.StateChanged -=
        Device_StateChanged;
      loopbackInput.StateChanged -=
        Device_StateChanged;

      captureInput.Dispose();
      loopbackInput.Dispose();
      Ingress.ResetTimeline();

      GC.SuppressFinalize(this);
    }
  }
}
