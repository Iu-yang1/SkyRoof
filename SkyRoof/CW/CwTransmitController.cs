namespace SkyRoof.CW
{
  public readonly record struct CwTransmitState(
    bool Armed,
    bool Sending,
    string ActiveText,
    CwKeyerStatus? RadioStatus,
    DateTime? WatchdogDueUtc,
    string? LastError);

  /// <summary>
  /// Application-level CW transmit gate. TransmitEnabled is persistent and
  /// defaults false; Armed is deliberately process/UI-session-only and never
  /// written to Settings. Every send retains one SkyCAT TCP lease until STOP.
  /// </summary>
  public sealed class CwTransmitController :
    IAsyncDisposable
  {
    private readonly CwConsoleSettings settings;
    private readonly ICwKeyerSessionFactory sessionFactory;
    private readonly SemaphoreSlim operationLock =
      new(1, 1);

    private ICwKeyerSession? activeSession;
    private CancellationTokenSource? watchdogStop;
    private Task? watchdogTask;

    private bool armed;
    private string activeText =
      string.Empty;
    private CwKeyerStatus? radioStatus;
    private DateTime? watchdogDueUtc;
    private string? lastError;
    private bool disposed;

    public event EventHandler? StateChanged;

    public CwTransmitController(
      CwConsoleSettings settings)
      : this(
          settings,
          new CwKeyerTcpSessionFactory())
    {
    }

    internal CwTransmitController(
      CwConsoleSettings settings,
      ICwKeyerSessionFactory sessionFactory)
    {
      this.settings =
        settings ??
        throw new ArgumentNullException(
          nameof(settings));

      this.sessionFactory =
        sessionFactory ??
        throw new ArgumentNullException(
          nameof(sessionFactory));
    }

    public CwTransmitState State
    {
      get
      {
        lock (this)
        {
          return new(
            armed,
            activeSession != null,
            activeText,
            radioStatus,
            watchdogDueUtc,
            lastError);
        }
      }
    }

    public void Arm()
    {
      ThrowIfDisposed();

      if (!settings.TransmitEnabled)
        throw new InvalidOperationException(
          "CW transmit is disabled in Settings.");

      lock (this)
      {
        armed = true;
        lastError = null;
      }

      OnStateChanged();
    }

    public async Task DisarmAsync()
    {
      ThrowIfDisposed();

      Exception? stopError = null;

      try
      {
        await StopAsync();
      }
      catch (Exception ex)
      {
        stopError = ex;
      }

      lock (this)
        armed = false;

      OnStateChanged();

      if (stopError != null)
        throw stopError;
    }

    public async Task<CwKeyerStatus>
      QueryStatusAsync(
        CancellationToken cancellationToken = default)
    {
      ThrowIfDisposed();

      await operationLock.WaitAsync(
        cancellationToken);

      try
      {
        await using ICwKeyerSession session =
          await sessionFactory.ConnectAsync(
            settings.CwKeyerPort,
            cancellationToken);

        CwKeyerStatus status =
          await session.GetStatusAsync(
            cancellationToken);

        lock (this)
        {
          radioStatus = status;
          lastError = null;
        }

        OnStateChanged();
        return status;
      }
      catch (Exception ex)
      {
        SetError(ex);
        throw;
      }
      finally
      {
        operationLock.Release();
      }
    }

    public async Task SendAsync(
      string text,
      CancellationToken cancellationToken = default)
    {
      ThrowIfDisposed();
      CwMessageTiming.ValidateText(text);

      await operationLock.WaitAsync(
        cancellationToken);

      ICwKeyerSession? session = null;

      try
      {
        lock (this)
        {
          if (!settings.TransmitEnabled)
            throw new InvalidOperationException(
              "CW transmit is disabled in Settings.");

          if (!armed)
            throw new InvalidOperationException(
              "CW transmit is not armed.");

          if (activeSession != null)
            throw new InvalidOperationException(
              "A CW message is already active.");
        }

        session =
          await sessionFactory.ConnectAsync(
            settings.CwKeyerPort,
            cancellationToken);

        CwKeyerStatus status =
          await session.GetStatusAsync(
            cancellationToken);

        EnsureReady(status);

        // SkyCAT repeats the same preflight under the shared hardware command
        // lock immediately before Command 17, so this local check is only an
        // early operator-facing diagnostic, not the final safety authority.
        await session.SendAsync(
          text,
          cancellationToken);

        TimeSpan watchdog =
          CwMessageTiming.ComputeWatchdog(
            text,
            status.KeySpeedRaw);

        DateTime due =
          DateTime.UtcNow + watchdog;

        CancellationTokenSource watchdogCts =
          new();

        lock (this)
        {
          activeSession = session;
          session = null;
          activeText = text;
          radioStatus = status;
          watchdogDueUtc = due;
          watchdogStop = watchdogCts;
          lastError = null;
        }

        watchdogTask =
          RunWatchdogAsync(
            watchdog,
            watchdogCts.Token);

        OnStateChanged();
      }
      catch (Exception ex)
      {
        if (session != null)
          await session.DisposeAsync();

        SetError(ex);
        throw;
      }
      finally
      {
        operationLock.Release();
      }
    }

    public async Task StopAsync(
      CancellationToken cancellationToken = default)
    {
      ThrowIfDisposed();

      await operationLock.WaitAsync(
        cancellationToken);

      try
      {
        await StopCoreAsync(
          cancellationToken,
          reportError: true);
      }
      finally
      {
        operationLock.Release();
      }
    }

    private async Task StopCoreAsync(
      CancellationToken cancellationToken,
      bool reportError)
    {
      ICwKeyerSession? session;

      lock (this)
      {
        session = activeSession;
        activeSession = null;

        watchdogStop?.Cancel();
        watchdogStop?.Dispose();
        watchdogStop = null;
        watchdogDueUtc = null;
        activeText = string.Empty;
      }

      if (session == null)
      {
        OnStateChanged();
        return;
      }

      Exception? error = null;

      try
      {
        await session.StopAsync(
          cancellationToken);
      }
      catch (Exception ex)
      {
        // Closing the connection is itself a second fail-safe: SkyCAT's
        // per-client disconnect handler issues Command 17 FF and keeps an
        // uncertain lease fail-closed if that STOP cannot be confirmed.
        error = ex;
      }
      finally
      {
        await session.DisposeAsync();
      }

      if (error == null)
      {
        lock (this)
          lastError = null;

        OnStateChanged();
        return;
      }

      SetError(error);

      if (reportError)
        throw error;
    }

    private async Task RunWatchdogAsync(
      TimeSpan delay,
      CancellationToken cancellationToken)
    {
      try
      {
        await Task.Delay(
          delay,
          cancellationToken);

        await StopAsync(
          CancellationToken.None);
      }
      catch (OperationCanceledException)
      {
      }
      catch (ObjectDisposedException)
      {
      }
      catch (Exception ex)
      {
        SetError(
          new InvalidOperationException(
            "CW watchdog STOP failed. SkyCAT connection teardown was used as the secondary fail-safe.",
            ex));
      }
    }

    private static void EnsureReady(
      CwKeyerStatus status)
    {
      if (status.Lease != "IDLE")
        throw new InvalidOperationException(
          $"SkyCAT CW keyer is {status.Lease}.");

      if (status.Mode is not ("CW" or "CW-R"))
        throw new InvalidOperationException(
          "TX VFO must already be in CW or CW-R.");

      if (status.BreakIn == 0)
        throw new InvalidOperationException(
          "IC-9700 BK-IN must already be Semi or Full.");

      if (status.Transmitting)
        throw new InvalidOperationException(
          "The radio is already transmitting.");
    }

    private void SetError(
      Exception ex)
    {
      lock (this)
        lastError =
          ex.Message;

      OnStateChanged();
    }

    private void OnStateChanged() =>
      StateChanged?.Invoke(
        this,
        EventArgs.Empty);

    private void ThrowIfDisposed()
    {
      ObjectDisposedException.ThrowIf(
        disposed,
        this);
    }

    public async ValueTask DisposeAsync()
    {
      if (disposed)
        return;

      Exception? stopError = null;

      try
      {
        await StopAsync();
      }
      catch (Exception ex)
      {
        stopError = ex;
      }

      lock (this)
      {
        armed = false;
        disposed = true;
      }

      watchdogStop?.Cancel();
      watchdogStop?.Dispose();
      operationLock.Dispose();

      OnStateChanged();
      GC.SuppressFinalize(this);

      // Shutdown must continue even if the explicit STOP failed: disposing the
      // TCP session already triggered the SkyCAT disconnect fail-safe. Preserve
      // the error in state rather than throwing from application teardown.
      if (stopError != null)
        SetError(stopError);
    }
  }
}
