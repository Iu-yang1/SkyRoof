using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using SkyRoof;
using VE3NEA;

namespace SkyRoof
{
  internal enum RotatorContinuousDirection
  {
    Up = 2,
    Down = 4,
    Left = 8,
    Right = 16
  }

  public sealed class RotatorPositionFaultEventArgs : EventArgs
  {
    public Bearing Previous { get; }
    public Bearing Reported { get; }
    public double AzimuthJumpDeg { get; }
    public double ElevationJumpDeg { get; }

    internal RotatorPositionFaultEventArgs(Bearing previous, Bearing reported,
      double azimuthJumpDeg, double elevationJumpDeg)
    {
      Previous = previous;
      Reported = reported;
      AzimuthJumpDeg = azimuthJumpDeg;
      ElevationJumpDeg = elevationJumpDeg;
    }
  }

  public class RotatorControlEngine : ControlEngine
  {
    public volatile Bearing? RequestedBearing, LastReadBearing, LastWrittenBearing;
    private long LastSuccessfulBearingReadTicks;
    public DateTime LastSuccessfulBearingReadUtc =>
      new DateTime(Interlocked.Read(ref LastSuccessfulBearingReadTicks), DateTimeKind.Utc);
    private volatile bool stopRequested = false;
    private volatile bool stopNotSupported = false;
    private int requestedContinuousDirection;
    private int appliedContinuousDirection;
    private int positionFaulted;
    private readonly double positionJumpLimitDegrees;
    public bool PositionFaulted => Volatile.Read(ref positionFaulted) != 0;
    public event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;

    public event EventHandler? BearingChanged;

    public RotatorControlEngine(RotatorSettings settings) : base(settings.Host, settings.Port, settings)
    {
      positionJumpLimitDegrees = Math.Clamp(settings.PositionJumpLimitDegrees, 5, 180);
      StartThread();
    }

    protected override bool Setup()
    {
      // A reconnect may be to a restarted rotctld/controller. Its actual position
      // and the last accepted target are no longer known; resend any pending target.
      LastReadBearing = null;
      LastWrittenBearing = null;
      Volatile.Write(
        ref appliedContinuousDirection,
        0);
      Interlocked.Exchange(ref LastSuccessfulBearingReadTicks, 0);
      return true;
    }

    public void RotateTo(Bearing bearing)
    {
      if (PositionFaulted) return; // Latched until the operator re-applies rotator settings.
      bool continuousWasActive =
        Volatile.Read(ref requestedContinuousDirection) != 0 ||
        Volatile.Read(ref appliedContinuousDirection) != 0;

      Volatile.Write(
        ref requestedContinuousDirection,
        0);
      RequestedBearing = bearing;

      // A position command must not race an earlier continuous M command.
      // Stop the continuous move first, then the normal cycle may issue P.
      if (continuousWasActive)
        stopRequested = true;
    }

    internal void StartContinuousMove(
      RotatorContinuousDirection direction)
    {
      if (PositionFaulted) return;
      if (stopNotSupported)
      {
        Log.Warning(
          "Continuous rotator movement is disabled because this rotctld endpoint did not acknowledge STOP. Reapply rotator settings before trying manual hold control again.");
        return;
      }

      int value = (int)direction;
      if (value is not (2 or 4 or 8 or 16))
        throw new ArgumentOutOfRangeException(
          nameof(direction));

      bool absoluteWasActive =
        RequestedBearing != null ||
        LastWrittenBearing != null;

      RequestedBearing = null;
      LastWrittenBearing = null;

      int applied =
        Volatile.Read(
          ref appliedContinuousDirection);

      // Taking ownership from an absolute P target, or switching direction,
      // is fail-safe: STOP must be acknowledged before the new M command.
      // Starting the same already-active direction remains idempotent.
      if (absoluteWasActive ||
          (applied != 0 &&
           applied != value))
        stopRequested = true;

      Volatile.Write(
        ref requestedContinuousDirection,
        value);
    }

    private void OnBearingChanged()
    {
      syncContext.Post(s => BearingChanged?.Invoke(this, EventArgs.Empty), null);
    }

    // UI-thread safe: sets a request flag and returns. The processing thread
    // sends the S command from Cycle() so no socket I/O ever runs on the caller.
    public void StopRotation()
    {
      RequestedBearing = LastWrittenBearing = null;
      Volatile.Write(
        ref requestedContinuousDirection,
        0);
      stopRequested = true;
    }

    protected override void Cycle()
    {
      if (TcpClient == null || !TcpClient.Connected) return;

      if (stopRequested)
      {
        stopRequested = false;
        bool stopped =
          SendStopCommand();
        Volatile.Write(
          ref appliedContinuousDirection,
          0);

        if (!stopped)
        {
          // Never start a new direction when STOP could not be confirmed.
          Volatile.Write(
            ref requestedContinuousDirection,
            0);
          return;
        }

        // A direction switch or transition back to absolute positioning may
        // already be queued. Continue this cycle after the acknowledged stop
        // so the motor does not sit idle for an unnecessary polling interval.
      }

      if (PositionFaulted)
      {
        // Keep polling to show the actual reported position, but never resume motion.
        ReadBearing();
        return;
      }

      int requestedMove =
        Volatile.Read(
          ref requestedContinuousDirection);
      if (requestedMove != 0)
      {
        int appliedMove =
          Volatile.Read(
            ref appliedContinuousDirection);

        if (requestedMove != appliedMove)
        {
          // Hamlib rotctld M: 2=UP, 4=DOWN, 8=LEFT/CCW, 16=RIGHT/CW.
          // Speed -1 means keep the controller/backend's current speed.
          if (SendWriteCommand(
                $"M {requestedMove} -1"))
          {
            Volatile.Write(
              ref appliedContinuousDirection,
              requestedMove);
          }
          else
          {
            // Fail closed. Do not hammer an unsupported backend with M every
            // cycle and do not synthesize relative P commands behind the user's
            // back.
            Volatile.Write(
              ref requestedContinuousDirection,
              0);
            Volatile.Write(
              ref appliedContinuousDirection,
              0);
          }
        }

        ReadBearing();
        return;
      }

      WriteBearing();
      ReadBearing();
    }

    // some rotator servers accept the stop command but never reply to it, and the read then times
    // out. Do not drop the connection when that happens, and do not send the command again until
    // the rotator settings are re-applied and this engine is re-created
    private bool SendStopCommand()
    {
      if (stopNotSupported)
        return false;

      try
      {
        if (SendWriteCommand("S"))
          return true;

        stopNotSupported = true;
        Log.Warning(
          "The rotator controller rejected the Stop command. Continuous manual movement is disabled until rotator settings are reapplied.");
        return false;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
      {
        stopNotSupported = true;
        Log.Warning(
          "The rotator controller does not reply to the Stop command. Continuous manual movement is disabled until rotator settings are reapplied.");
        return false;
      }
    }

    private void WriteBearing()
    {
      // Snapshot the target: a UI request may change while a TCP write is pending.
      var requested = RequestedBearing;
      if (requested == null || requested == LastWrittenBearing) return;

      // A rejected command must remain pending for the next cycle. Transport
      // exceptions propagate to ControlEngine so it can reconnect.
      if (SendWriteCommand($"P {requested.AzDeg.ToString("F1", CultureInfo.InvariantCulture)} " +
        $"{requested.ElDeg.ToString("F1", CultureInfo.InvariantCulture)}"))
        LastWrittenBearing = requested;
    }

    private void ReadBearing()
    {
      // Hamlib rotctld's 'p' reply consists of TWO newline-terminated lines:
      // azimuth followed by elevation. SendReadCommand consumes only the first
      // line, and ReadLine retains any already-received second line in its buffer.
      string? azimuthLine = SendReadCommand("p");
      if (azimuthLine == null) return;

      // A failed query returns a single "RPRT <code>" line; do not block waiting
      // for an elevation that will never arrive.
      if (azimuthLine.StartsWith("RPRT ", StringComparison.Ordinal))
      {
        BadReply(azimuthLine);
        return;
      }

      string elevationLine = ReadLine().Trim();
      if (log) Log.Information("Rotator position reply: {Azimuth} / {Elevation}",
        azimuthLine, elevationLine);

      if (!double.TryParse(azimuthLine.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double azimuth) ||
          !double.TryParse(elevationLine, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double elevation) ||
          !double.IsFinite(azimuth) || !double.IsFinite(elevation))
      {
        BadReply($"{azimuthLine} / {elevationLine}");
        return;
      }

      var bearing = new Bearing(
        azimuth * Math.PI / 180.0,
        elevation * Math.PI / 180.0);

      // Only compare consecutive fresh observations from the same connection;
      // an initial read or a read delayed by reconnect/network loss is not
      // evidence that the physical rotor jumped.
      long nowTicks = DateTime.UtcNow.Ticks;
      long previousTicks = Interlocked.Exchange(ref LastSuccessfulBearingReadTicks, nowTicks);
      Bearing? previous = LastReadBearing;
      if (!PositionFaulted && previous != null && previousTicks != 0 &&
          IsSuspiciousBearingJump(previous, bearing,
            TimeSpan.FromTicks(nowTicks - previousTicks).TotalSeconds,
            positionJumpLimitDegrees, out double azJump, out double elJump))
      {
        // Latch before posting a user notification: a queued tracking/PARK
        // request cannot race the STOP or resume motion after acknowledging UI.
        Interlocked.Exchange(ref positionFaulted, 1);
        RequestedBearing = LastWrittenBearing = null;
        Volatile.Write(ref requestedContinuousDirection, 0);
        // Already on the rotctld worker thread: send STOP immediately, not
        // after another Delay-cycle. Keep the latch even if STOP is rejected.
        stopRequested = false;
        SendStopCommand();
        Log.Error("Rotator feedback jump: az {Az:F1} degrees, el {El:F1} degrees. STOP requested; inspect actual position.",
          azJump, elJump);
        var fault = new RotatorPositionFaultEventArgs(previous, bearing, azJump, elJump);
        syncContext.Post(_ => PositionFaultDetected?.Invoke(this, fault), null);
      }

      if (bearing == LastReadBearing) return;
      LastReadBearing = bearing;
      OnBearingChanged();
    }

    // Azimuth is a circular measurement: 359 -> 1 must not be mistaken for a
    // 358-degree physical jump. Elevation is not circular.
    internal static bool IsSuspiciousBearingJump(
      Bearing previous, Bearing reported, double elapsedSeconds,
      double thresholdDegrees, out double azJump, out double elJump)
    {
      azJump = elJump = 0;
      if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 ||
          elapsedSeconds > 2.0 || !double.IsFinite(thresholdDegrees) ||
          thresholdDegrees <= 0)
        return false;

      double azA = ((previous.AzDeg % 360) + 360) % 360;
      double azB = ((reported.AzDeg % 360) + 360) % 360;
      double rawAz = Math.Abs(azA - azB);
      // Retain mechanical-turn information if the controller reports an
      // extended azimuth outside the normal 0..360 interval (e.g. 450°).
      // Ordinary 359° -> 1° readings must still use the shortest arc.
      bool multiTurn = previous.AzDeg < 0 || previous.AzDeg > 360 ||
                       reported.AzDeg < 0 || reported.AzDeg > 360;
      azJump = multiTurn
        ? Math.Abs(previous.AzDeg - reported.AzDeg)
        : Math.Min(rawAz, 360 - rawAz);
      elJump = Math.Abs(previous.ElDeg - reported.ElDeg);
      return azJump > thresholdDegrees || elJump > thresholdDegrees;
    }
  }
}
