using Serilog;
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

    public bool Enabled { get; private set; }
    public CwReceiveAudioSource Source { get; private set; }
    public string? SourceIdentity { get; private set; }
    public long AcceptedSamples { get; private set; }
    public DateTime? LastAcceptedUtc { get; private set; }
    public long TimelineGeneration { get; private set; }

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
      AcceptedSamples += count;
      LastAcceptedUtc = utc;
      return true;
    }

    public void ResetTimeline()
    {
      FrontEnd.Reset();
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
    private readonly InputSoundcard<float> captureInput = new();
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
