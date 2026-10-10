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
    private CwConsoleSettings settings;
    private readonly ICwKeyerSessionFactory sessionFactory;
    private readonly ICwTransmitInterlock transmitInterlock;
    private readonly SemaphoreSlim operationLock =
      new(1, 1);

    private ICwKeyerSession? activeSession;
    private CancellationTokenSource? watchdogStop;
    private Task? watchdogTask;
    private Task? interlockMonitorTask;

    private CwTransmitInterlockSnapshot? armedInterlock;
    private CwTransmitInterlockSnapshot? activeInterlock;

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
          new CwKeyerTcpSessionFactory(),
          NullCwTransmitInterlock.Instance)
    {
    }

    public CwTransmitController(
      CwConsoleSettings settings,
      ICwTransmitInterlock transmitInterlock)
      : this(
          settings,
          new CwKeyerTcpSessionFactory(),
          transmitInterlock)
    {
    }

    public CwTransmitController(
      CwConsoleSettings settings,
      ICwKeyerSessionFactory sessionFactory,
      ICwTransmitInterlock? transmitInterlock = null)
    {
      this.settings =
        settings ??
        throw new ArgumentNullException(
          nameof(settings));

      this.sessionFactory =
        sessionFactory ??
        throw new ArgumentNullException(
          nameof(sessionFactory));

      this.transmitInterlock =
        transmitInterlock ??
        NullCwTransmitInterlock.Instance;
    }

    public bool TxCatWritesFrozen =>
      transmitInterlock.TxWritesFrozen;

    public CwTransmitInterlockSnapshot? CurrentInterlock
    {
      get
      {
        lock (this)
          return activeInterlock ??
            armedInterlock;
      }
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

    public async Task ApplySettingsAsync(
      CwConsoleSettings newSettings)
    {
      ThrowIfDisposed();
      ArgumentNullException.ThrowIfNull(
        newSettings);

      bool safetyBoundaryChanged;
      bool mustStop;

      lock (this)
      {
        CwConsoleSettings previous =
          settings;

        safetyBoundaryChanged =
          !newSettings.TransmitEnabled ||
          newSettings.CwKeyerPort !=
            previous.CwKeyerPort;

        settings = newSettings;
        mustStop =
          safetyBoundaryChanged &&
          (armed ||
           activeSession != null);

        if (safetyBoundaryChanged)
        {
          armed = false;
          armedInterlock = null;
        }
      }

      OnStateChanged();

      if (!mustStop)
        return;

      // The new safety setting is already authoritative before touching the
      // network. Even if STOP fails, subsequent Send/Arm calls see the new
      // disabled/changed configuration. An active session does not need the
      // configured port to stop because it retains its original TCP lease.
      await StopAsync();
    }

    public void Arm()
    {
      ThrowIfDisposed();

      if (!settings.TransmitEnabled)
        throw new InvalidOperationException(
          "CW transmit is disabled in Settings.");

      CwTransmitInterlockSnapshot snapshot =
        transmitInterlock.CaptureForArm();

      lock (this)
      {
        armed = true;
        armedInterlock = snapshot;
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
      {
        armed = false;
        armedInterlock = null;
      }

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

        CwTransmitInterlockSnapshot? armedSnapshot;
        lock (this)
          armedSnapshot = armed
            ? armedInterlock
            : null;

        if (armedSnapshot.HasValue)
        {
          CwTransmitInterlockSnapshot current =
            transmitInterlock.PrepareForSend(
              armedSnapshot.Value);
          transmitInterlock.ValidateHardware(
            current,
            status);
        }

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

    public async Task<CwKeyerStatus> SetKeySpeedAsync(
      double wpm,
      CancellationToken cancellationToken = default)
    {
      ThrowIfDisposed();
      int expectedRaw =
        CwMessageTiming.WpmToRawKeySpeed(wpm);

      await operationLock.WaitAsync(
        cancellationToken);

      try
      {
        lock (this)
        {
          if (!settings.TransmitEnabled)
            throw new InvalidOperationException(
              "CW transmit is disabled in Settings.");
          if (activeSession != null)
            throw new InvalidOperationException(
              "CW key speed cannot be changed while a message is active.");
        }

        await using ICwKeyerSession session =
          await sessionFactory.ConnectAsync(
            settings.CwKeyerPort,
            cancellationToken);

        CwKeySpeedResult result =
          await session.SetWpmAsync(
            wpm,
            cancellationToken);

        if (result.KeySpeedRaw != expectedRaw)
          throw new InvalidOperationException(
            $"SkyCAT verified an unexpected key-speed value: requested raw {expectedRaw}, got {result.KeySpeedRaw}.");

        CwKeyerStatus status =
          await session.GetStatusAsync(
            cancellationToken);

        if (status.KeySpeedRaw != result.KeySpeedRaw)
          throw new InvalidOperationException(
            $"CW key-speed verification changed between SETWPM and STATUS: {result.KeySpeedRaw} -> {status.KeySpeedRaw}.");

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
        CwTransmitInterlockSnapshot armSnapshot;

        lock (this)
        {
          if (!settings.TransmitEnabled)
            throw new InvalidOperationException(
              "CW transmit is disabled in Settings.");

          if (!armed)
            throw new InvalidOperationException(
              "CW transmit is not armed.");

          if (!armedInterlock.HasValue)
            throw new InvalidOperationException(
              "CW transmit interlock was not captured while arming.");

          if (activeSession != null)
            throw new InvalidOperationException(
              "A CW message is already active.");

          armSnapshot =
            armedInterlock.Value;
        }

        CwTransmitInterlockSnapshot sendSnapshot =
          transmitInterlock.PrepareForSend(
            armSnapshot);

        transmitInterlock.SetTxWritesFrozen(
          true);

        session =
          await sessionFactory.ConnectAsync(
            settings.CwKeyerPort,
            cancellationToken);

        CwKeyerStatus status =
          await session.GetStatusAsync(
            cancellationToken);

        EnsureReady(status);
        transmitInterlock.ValidateHardware(
          sendSnapshot,
          status);

        // Satellite sends use SkyCAT's SENDHZ command so the actual TX VFO is
        // re-read and compared inside the same serial/lease critical section
        // immediately before Command 17. Terrestrial operation preserves the
        // legacy SEND command.
        if (sendSnapshot.RequiresHardwareFrequencyGuard)
        {
          await session.SendGuardedAsync(
            sendSnapshot.ExpectedCatTxHz,
            sendSnapshot.FrequencyToleranceHz,
            text,
            cancellationToken);
        }
        else
        {
          await session.SendAsync(
            text,
            cancellationToken);
        }

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
          activeInterlock = sendSnapshot;
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

        interlockMonitorTask =
          RunInterlockMonitorAsync(
            sendSnapshot,
            watchdogCts.Token);

        OnStateChanged();
      }
      catch (Exception ex)
      {
        if (session != null)
          await session.DisposeAsync();

        if (activeSession == null)
          transmitInterlock.SetTxWritesFrozen(
            false);

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
        activeInterlock = null;

        watchdogStop?.Cancel();
        watchdogStop?.Dispose();
        watchdogStop = null;
        watchdogDueUtc = null;
        activeText = string.Empty;
      }

      if (session == null)
      {
        transmitInterlock.SetTxWritesFrozen(
          false);
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
        transmitInterlock.SetTxWritesFrozen(
          false);
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

    private async Task RunInterlockMonitorAsync(
      CwTransmitInterlockSnapshot active,
      CancellationToken cancellationToken)
    {
      try
      {
        while (true)
        {
          await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

          transmitInterlock.ValidateDuringSend(
            active);
        }
      }
      catch (OperationCanceledException)
      {
      }
      catch (ObjectDisposedException)
      {
      }
      catch (Exception safetyError)
      {
        lock (this)
        {
          armed = false;
          armedInterlock = null;
        }

        Exception? stopError = null;
        try
        {
          await StopAsync(
            CancellationToken.None);
        }
        catch (Exception ex)
        {
          stopError = ex;
        }

        InvalidOperationException error =
          stopError == null
            ? new InvalidOperationException(
                "CW satellite TX interlock changed during transmission. The message was stopped and TX was disarmed.",
                safetyError)
            : new InvalidOperationException(
                "CW satellite TX interlock changed and the explicit STOP also failed. SkyCAT connection teardown remains the secondary fail-safe.",
                new AggregateException(
                  safetyError,
                  stopError));

        SetError(error);
        OnStateChanged();
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
        armedInterlock = null;
        activeInterlock = null;
        disposed = true;
      }

      watchdogStop?.Cancel();
      watchdogStop?.Dispose();
      transmitInterlock.SetTxWritesFrozen(
        false);
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
