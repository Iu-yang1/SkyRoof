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

    public event EventHandler? BearingChanged;

    public RotatorControlEngine(RotatorSettings settings) : base(settings.Host, settings.Port, settings)
    {
      StartThread();
    }

    protected override bool Setup()
    {
      // A reconnect may be to a restarted rotctld/controller. Its actual position
      // and the last accepted target are no longer known; resend any pending target.
      LastReadBearing = null;
      LastWrittenBearing = null;
      Interlocked.Exchange(ref LastSuccessfulBearingReadTicks, 0);
      return true;
    }

    public void RotateTo(Bearing bearing)
    {
      bool continuousWasActive =
        Volatile.Read(ref requestedContinuousDirection) != 0 ||
        Volatile.Read(ref appliedContinuousDirection) != 0;

      Volatile.Write(
        ref requestedContinuousDirection,
        0);
      RequestedBearing = bearing;

      // A position command must not race an earlier continuous M command.
      // Stop the continuous move first, then the normal cycle may issue P.
      stopRequested =
        continuousWasActive;
    }

    internal void StartContinuousMove(
      RotatorContinuousDirection direction)
    {
      int value = (int)direction;
      if (value is not (2 or 4 or 8 or 16))
        throw new ArgumentOutOfRangeException(
          nameof(direction));

      RequestedBearing = null;
      LastWrittenBearing = null;

      int applied =
        Volatile.Read(
          ref appliedContinuousDirection);

      // Switching axes/direction is fail-safe: queue an all-stop before the
      // new move. Starting the same direction again is idempotent.
      stopRequested =
        applied != 0 &&
        applied != value;

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
        SendStopCommand();
        Volatile.Write(
          ref appliedContinuousDirection,
          0);

        // A direction switch or transition back to absolute positioning may
        // already be queued. Continue this cycle after the stop so the motor
        // does not sit idle for an unnecessary full polling interval.
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
    private void SendStopCommand()
    {
      if (stopNotSupported) return;

      try
      {
        SendWriteCommand("S");
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
      {
        stopNotSupported = true;
        Log.Warning("The rotator controller does not reply to the Stop command. " +
          "SkyRoof will not send this command again.");
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

      // Every successful 'p' reply is a fresh observation, including when
      // the antenna is stationary. PARK uses this for two-sample arrival.
      Interlocked.Exchange(ref LastSuccessfulBearingReadTicks, DateTime.UtcNow.Ticks);
      if (bearing == LastReadBearing) return;

      LastReadBearing = bearing;
      OnBearingChanged();
    }
  }
}
