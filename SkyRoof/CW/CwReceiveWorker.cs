namespace SkyRoof.CW
{
  public sealed class CwReceiveWorkerOptions
  {
    public TimeSpan TrackingInterval { get; init; } =
      TimeSpan.FromMilliseconds(120);
    public double DecodeWindowSeconds { get; init; } = 6.0;
    public double DecodeHopSeconds { get; init; } = 1.0;
    public int MaxDecodeLanes { get; init; } = 5;
    public double LaneBandwidthHz { get; init; } = 240;
    public double TargetCenterHz { get; init; } = 800;
    public TimeSpan ModelRetryInterval { get; init; } =
      TimeSpan.FromSeconds(5);
  }

  public enum CwReceiveModelState
  {
    Unknown,
    NotInstalled,
    Ready,
    Error
  }

  public readonly record struct CwReceiveWorkerStatus(
    bool Running,
    CwReceiveModelState ModelState,
    bool InferenceBusy,
    long TimelineGeneration,
    long TrackingCycles,
    long CompletedInferenceWindows,
    long SkippedInferenceWindows,
    int TrackCount,
    string? LastError,
    long RidgeStftCacheHits = 0,
    long RidgeStftCacheMisses = 0,
    long DeepCwStftCacheHits = 0,
    long DeepCwStftCacheMisses = 0,
    double MeanOnnxInferenceMs = 0);

  public sealed class CwTracksUpdatedEventArgs : EventArgs
  {
    public long TimelineGeneration { get; }
    public IReadOnlyList<CwSignalTrack> Tracks { get; }

    public CwTracksUpdatedEventArgs(
      long timelineGeneration,
      IReadOnlyList<CwSignalTrack> tracks)
    {
      TimelineGeneration = timelineGeneration;
      Tracks = tracks;
    }
  }

  public sealed class CwDecodeUpdatedEventArgs : EventArgs
  {
    public long TimelineGeneration { get; }
    public CwContinuousDecodeBatch Batch { get; }

    public CwDecodeUpdatedEventArgs(
      long timelineGeneration,
      CwContinuousDecodeBatch batch)
    {
      TimelineGeneration = timelineGeneration;
      Batch = batch;
    }
  }

  internal interface ICwInferenceSession : IDisposable
  {
    CwContinuousDecodeBatch Decode(
      CwAudioSnapshot snapshot,
      IReadOnlyList<CwSignalTrack> tracks);

    void Reset();
  }

  internal sealed class DeepCwInferenceSession :
    ICwInferenceSession
  {
    private readonly DeepCwOnnxDecoder onnx;
    private readonly CwContinuousDeepCwDecoder continuous;

    internal DeepCwInferenceSession(
      CwReceiveWorkerOptions options)
    {
      onnx = DeepCwOnnxDecoder.OpenInstalled();

      var decoder =
        new DeepCwMultiLaneDecoder(
          onnx.Metadata,
          onnx)
        {
          MaxLanes = options.MaxDecodeLanes,
          LaneBandwidthHz =
            options.LaneBandwidthHz,
          TargetCenterHz =
            options.TargetCenterHz
        };

      continuous =
        new CwContinuousDeepCwDecoder(
          decoder);
    }

    public CwContinuousDecodeBatch Decode(
      CwAudioSnapshot snapshot,
      IReadOnlyList<CwSignalTrack> tracks) =>
      continuous.Decode(
        snapshot.Samples,
        snapshot.SampleRate,
        snapshot.EndUtc,
        tracks,
        snapshot.EndSampleIndex);

    public void Reset() =>
      continuous.Reset();

    public void Dispose() =>
      onnx.Dispose();
  }

  /// <summary>
  /// Receive-side scheduler that keeps carrier tracking independent from
  /// neural inference. The tracker runs at a fixed cadence; DeepCW consumes
  /// immutable PCM snapshots on a separate latest-only lane. If inference is
  /// still busy when the next hop becomes due, that old window is skipped
  /// rather than queued.
  /// </summary>
  public sealed class CwReceiveWorker : IDisposable
  {
    private readonly object sync = new();
    private readonly CwPcmIngress ingress;
    private readonly CwReceiveWorkerOptions options;
    private readonly Func<ICwInferenceSession?> sessionFactory;
    private readonly Func<double, IReadOnlyList<CwSignalTrack>>
      trackAnalyzer;

    private CancellationTokenSource? stop;
    private Task? trackingTask;
    private Task? inferenceTask;
    private ICwInferenceSession? session;

    private IReadOnlyList<CwSignalTrack> latestTracks =
      Array.Empty<CwSignalTrack>();
    private CwContinuousDecodeBatch? latestDecode;

    private long observedGeneration = -1;
    private long lastInferenceAttemptSampleIndex = -1;
    private long trackingCycles;
    private long completedInferenceWindows;
    private long skippedInferenceWindows;

    private int inferenceBusy;
    private int pendingSessionReset;
    private bool disposed;
    private CwReceiveModelState modelState =
      CwReceiveModelState.Unknown;
    private DateTime nextModelAttemptUtc =
      DateTime.MinValue;
    private string? lastError;
    private double knownDopplerRateHzPerSecond;

    public event EventHandler<CwTracksUpdatedEventArgs>?
      TracksUpdated;
    public event EventHandler<CwDecodeUpdatedEventArgs>?
      DecodeUpdated;
    public event EventHandler? StatusChanged;

    public CwReceiveWorker(
      CwPcmIngress ingress,
      CwReceiveWorkerOptions? options = null)
      : this(
          ingress,
          options,
          null,
          null)
    {
    }

    internal CwReceiveWorker(
      CwPcmIngress ingress,
      CwReceiveWorkerOptions? options,
      Func<ICwInferenceSession?>? sessionFactory,
      Func<double, IReadOnlyList<CwSignalTrack>>?
        trackAnalyzer)
    {
      this.ingress =
        ingress ??
        throw new ArgumentNullException(nameof(ingress));
      this.options =
        options ??
        new CwReceiveWorkerOptions();

      ValidateOptions(
        this.options);

      if (this.options.DecodeWindowSeconds >
          ingress.FrontEnd.Audio.CapacitySeconds)
        throw new ArgumentException(
          "CW audio history is shorter than the decode window.",
          nameof(options));

      this.sessionFactory =
        sessionFactory ??
        (() =>
        {
          if (!DeepCwModelManager.IsInstalled())
            return null;

          return new DeepCwInferenceSession(
            this.options);
        });

      this.trackAnalyzer =
        trackAnalyzer ??
        (dopplerRate =>
          this.ingress.FrontEnd.Analyze(
            dopplerRate));
    }

    public CwReceiveWorkerOptions Options =>
      options;

    public double KnownDopplerRateHzPerSecond
    {
      get => Volatile.Read(
        ref knownDopplerRateHzPerSecond);
      set
      {
        if (!double.IsFinite(value))
          throw new ArgumentOutOfRangeException(
            nameof(value));
        Volatile.Write(
          ref knownDopplerRateHzPerSecond,
          value);
      }
    }

    public IReadOnlyList<CwSignalTrack> LatestTracks
    {
      get
      {
        lock (sync)
          return latestTracks.ToArray();
      }
    }

    public CwContinuousDecodeBatch? LatestDecode
    {
      get
      {
        lock (sync)
          return latestDecode;
      }
    }

    public void Start()
    {
      ThrowIfDisposed();

      lock (sync)
      {
        if (trackingTask != null)
          return;

        stop =
          new CancellationTokenSource();
        trackingTask =
          Task.Run(
            () => TrackingLoopAsync(
              stop.Token));
      }

      OnStatusChanged();
    }

    public async Task StopAsync()
    {
      Task? tracker;
      Task? inference;
      CancellationTokenSource? tokenSource;

      lock (sync)
      {
        tracker = trackingTask;
        inference = inferenceTask;
        tokenSource = stop;
        trackingTask = null;
        stop = null;
      }

      if (tracker == null &&
          inference == null)
        return;

      try
      {
        tokenSource?.Cancel();
      }
      catch (ObjectDisposedException)
      {
      }

      if (tracker != null)
      {
        try
        {
          await tracker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
      }

      if (inference != null)
      {
        try
        {
          await inference.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
      }

      tokenSource?.Dispose();

      lock (sync)
      {
        inferenceTask = null;
        session?.Dispose();
        session = null;
        latestTracks =
          Array.Empty<CwSignalTrack>();
        latestDecode = null;
      }

      Interlocked.Exchange(
        ref inferenceBusy,
        0);

      OnStatusChanged();
    }

    private async Task TrackingLoopAsync(
      CancellationToken cancellationToken)
    {
      using var timer =
        new PeriodicTimer(
          options.TrackingInterval);

      HandleTimelineGeneration();

      while (await timer.WaitForNextTickAsync(
        cancellationToken)
        .ConfigureAwait(false))
      {
        HandleTimelineGeneration();

        if (!ingress.Enabled)
          continue;

        try
        {
          IReadOnlyList<CwSignalTrack> tracks =
            trackAnalyzer(
              Volatile.Read(
                ref knownDopplerRateHzPerSecond));

          CwSignalTrack[] snapshot =
            tracks.ToArray();

          lock (sync)
            latestTracks = snapshot;

          Interlocked.Increment(
            ref trackingCycles);

          TracksUpdated?.Invoke(
            this,
            new CwTracksUpdatedEventArgs(
              observedGeneration,
              snapshot));

          TryScheduleInference(
            snapshot);
        }
        catch (Exception ex)
        {
          SetError(ex);
        }
      }
    }

    private void HandleTimelineGeneration()
    {
      long generation =
        ingress.TimelineGeneration;
      if (generation ==
          observedGeneration)
        return;

      observedGeneration =
        generation;
      lastInferenceAttemptSampleIndex =
        -1;
      Interlocked.Exchange(
        ref pendingSessionReset,
        1);

      lock (sync)
      {
        latestTracks =
          Array.Empty<CwSignalTrack>();
        latestDecode = null;
      }

      OnStatusChanged();
    }

    private void TryScheduleInference(
      IReadOnlyList<CwSignalTrack> tracks)
    {
      if (!tracks.Any(x => x.Confirmed))
        return;

      CwAudioHub audio =
        ingress.FrontEnd.Audio;
      long totalSamples =
        audio.TotalSamplesWritten;
      long windowSamples =
        checked((long)Math.Round(
          options.DecodeWindowSeconds *
          audio.SampleRate));
      long hopSamples =
        checked((long)Math.Round(
          options.DecodeHopSeconds *
          audio.SampleRate));

      if (totalSamples < windowSamples)
        return;

      bool due =
        lastInferenceAttemptSampleIndex < 0 ||
        totalSamples -
          lastInferenceAttemptSampleIndex >=
          hopSamples;
      if (!due)
        return;

      // Mark this hop consumed before checking Busy. A slow model must not
      // create a backlog of all missed windows.
      lastInferenceAttemptSampleIndex =
        totalSamples;

      if (Interlocked.CompareExchange(
            ref inferenceBusy,
            1,
            0) != 0)
      {
        Interlocked.Increment(
          ref skippedInferenceWindows);
        OnStatusChanged();
        return;
      }

      if (!audio.TrySnapshot(
            options.DecodeWindowSeconds,
            out CwAudioSnapshot pcm))
      {
        Interlocked.Exchange(
          ref inferenceBusy,
          0);
        return;
      }

      CwSignalTrack[] trackSnapshot =
        tracks.ToArray();
      long generation =
        observedGeneration;

      Task task =
        Task.Run(
          () => RunInference(
            pcm,
            trackSnapshot,
            generation));

      lock (sync)
        inferenceTask = task;

      OnStatusChanged();
    }

    private void RunInference(
      CwAudioSnapshot pcm,
      IReadOnlyList<CwSignalTrack> tracks,
      long generation)
    {
      try
      {
        if (generation !=
            ingress.TimelineGeneration)
          return;

        ICwInferenceSession? decoder =
          GetOrCreateSession();
        if (decoder == null)
          return;

        if (Interlocked.Exchange(
              ref pendingSessionReset,
              0) != 0)
          decoder.Reset();

        if (generation !=
            ingress.TimelineGeneration)
          return;

        CwContinuousDecodeBatch batch =
          decoder.Decode(
            pcm,
            tracks);

        Interlocked.Increment(
          ref completedInferenceWindows);

        if (generation !=
            ingress.TimelineGeneration)
          return;

        lock (sync)
          latestDecode = batch;

        DecodeUpdated?.Invoke(
          this,
          new CwDecodeUpdatedEventArgs(
            generation,
            batch));
      }
      catch (Exception ex)
      {
        SetError(ex);

        lock (sync)
        {
          session?.Dispose();
          session = null;
          modelState =
            CwReceiveModelState.Error;
          nextModelAttemptUtc =
            DateTime.UtcNow +
            options.ModelRetryInterval;
        }
      }
      finally
      {
        Interlocked.Exchange(
          ref inferenceBusy,
          0);
        OnStatusChanged();
      }
    }

    private ICwInferenceSession?
      GetOrCreateSession()
    {
      lock (sync)
      {
        if (session != null)
          return session;

        DateTime now =
          DateTime.UtcNow;
        if (now < nextModelAttemptUtc)
          return null;

        ICwInferenceSession? created =
          sessionFactory();
        if (created == null)
        {
          modelState =
            CwReceiveModelState.NotInstalled;
          nextModelAttemptUtc =
            now +
            options.ModelRetryInterval;
          lastError = null;
          return null;
        }

        session = created;
        modelState =
          CwReceiveModelState.Ready;
        nextModelAttemptUtc =
          DateTime.MinValue;
        lastError = null;
        return session;
      }
    }

    public CwReceiveWorkerStatus GetStatus()
    {
      lock (sync)
      {
        return new(
          Running:
            trackingTask != null,
          ModelState:
            modelState,
          InferenceBusy:
            Volatile.Read(
              ref inferenceBusy) != 0,
          TimelineGeneration:
            observedGeneration,
          TrackingCycles:
            Interlocked.Read(
              ref trackingCycles),
          CompletedInferenceWindows:
            Interlocked.Read(
              ref completedInferenceWindows),
          SkippedInferenceWindows:
            Interlocked.Read(
              ref skippedInferenceWindows),
          TrackCount:
            latestTracks.Count,
          LastError:
            lastError);
      }
    }

    private void SetError(
      Exception ex)
    {
      lock (sync)
        lastError =
          ex.Message;

      OnStatusChanged();
    }

    private void OnStatusChanged() =>
      StatusChanged?.Invoke(
        this,
        EventArgs.Empty);

    private static void ValidateOptions(
      CwReceiveWorkerOptions value)
    {
      if (value.TrackingInterval <
            TimeSpan.FromMilliseconds(20) ||
          value.TrackingInterval >
            TimeSpan.FromSeconds(2))
        throw new ArgumentOutOfRangeException(
          nameof(value.TrackingInterval));
      if (!double.IsFinite(
            value.DecodeWindowSeconds) ||
          value.DecodeWindowSeconds < 1 ||
          value.DecodeWindowSeconds > 30)
        throw new ArgumentOutOfRangeException(
          nameof(value.DecodeWindowSeconds));
      if (!double.IsFinite(
            value.DecodeHopSeconds) ||
          value.DecodeHopSeconds <= 0 ||
          value.DecodeHopSeconds >
            value.DecodeWindowSeconds)
        throw new ArgumentOutOfRangeException(
          nameof(value.DecodeHopSeconds));
      if (value.MaxDecodeLanes is < 1 or > 8)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxDecodeLanes));
      if (!double.IsFinite(
            value.LaneBandwidthHz) ||
          value.LaneBandwidthHz <= 0 ||
          !double.IsFinite(
            value.TargetCenterHz) ||
          value.TargetCenterHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.LaneBandwidthHz));
      if (value.ModelRetryInterval <
            TimeSpan.FromSeconds(1) ||
          value.ModelRetryInterval >
            TimeSpan.FromMinutes(5))
        throw new ArgumentOutOfRangeException(
          nameof(value.ModelRetryInterval));
    }

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

      StopAsync()
        .GetAwaiter()
        .GetResult();

      disposed = true;
      GC.SuppressFinalize(this);
    }
  }
}
