using System;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Supervises feedback-paced endpoint references, not motor steps. The
/// RC-3040S owns the stepper timing and can report internally accumulated
/// position rather than independently measured shaft position.
/// Never infer that physical movement matches a C2 report.
/// </summary>
internal sealed class Rc3040sPlannerFeedbackGuard
{
  // Limit each planned W endpoint to one/two integer degrees beyond the
  // most recent controller feedback, NOT multiple degrees of accumulated
  // open-loop model trajectory.
  internal const double MaximumLeadDegrees = 1.5;
  internal const double MinimumObservedProgressDegrees = 0.5;
  internal static readonly TimeSpan ProgressTimeout =
    TimeSpan.FromSeconds(4);

  private DateTime lastConsumedReadUtc = DateTime.MinValue;
  private DateTime lastProgressUtc = DateTime.MinValue;
  private RotatorPosition lastProgressPosition;
  private bool awaitingPhysicalProgress;
  private RotatorPosition? inFlightSetpoint;
  private int inFlightSettledReads;
  private DateTime firstSettledReadUtc=DateTime.MinValue;

  // A new C2 sample proves that the controller responded, but not that it
  // finished its preceding absolute target. The scheduler must never send
  // a new W while an earlier W is still in flight.
  internal bool HasInFlightSetpoint=>inFlightSetpoint.HasValue;

  /// <summary>
  /// The previous W target must be reached before the engine can clear
  /// a possible-motion STOP-after-reconnect interlock. Checking only
  /// the latest externally requested target is insufficient: a new
  /// satellite target could reverse or return to the current C2 angle
  /// while the previous motor command remains in flight.
  /// </summary>
  internal static bool IsLastWireAtReportedPosition(
    RotatorPosition actual, RotatorPosition? lastWritten,
    bool advancedPlanner)
  {
    if(!advancedPlanner)return true;
    return lastWritten is { } last &&
      Math.Abs(actual.Azimuth-last.Azimuth)<=0.5 &&
      Math.Abs(actual.Elevation-last.Elevation)<=0.5;
  }

  internal void Reset()
  {
    lastConsumedReadUtc=DateTime.MinValue;
    lastProgressUtc=DateTime.MinValue;
    lastProgressPosition=default;
    awaitingPhysicalProgress=false;
    inFlightSetpoint=null;
    inFlightSettledReads=0;
    firstSettledReadUtc=DateTime.MinValue;
  }

  internal bool TryConsumeFreshFeedback(
    DateTime readUtc, RotatorPosition measured,
    RotatorPosition requested, out bool stalled)
  {
    stalled=false;
    if(readUtc.Kind!=DateTimeKind.Utc ||
       readUtc<=lastConsumedReadUtc ||
       !NativeRotatorCodec.Valid(measured))
      return false;

    lastConsumedReadUtc=readUtc;
    if(inFlightSetpoint is { } commanded)
    {
      // C2 is not an independent shaft encoder, but it is the only
      // observable completion handshake on this absolute-target protocol.
      // Require two fresh C2 reports at the LAST transmitted endpoint,
      // separated in time, before planning another waypoint.
      bool reached=
        Math.Abs(measured.Azimuth-commanded.Azimuth)<=0.5 &&
        Math.Abs(measured.Elevation-commanded.Elevation)<=0.5;
      if(reached)
      {
        if(inFlightSettledReads==0)
          firstSettledReadUtc=readUtc;
        inFlightSettledReads++;
        if(inFlightSettledReads>=2 &&
           (readUtc-firstSettledReadUtc).TotalMilliseconds>=200)
        {
          inFlightSetpoint=null;
          inFlightSettledReads=0;
          firstSettledReadUtc=DateTime.MinValue;
          awaitingPhysicalProgress=false;
        }
      }
      else
      {
        inFlightSettledReads=0;
        firstSettledReadUtc=DateTime.MinValue;
      }
    }
    if(!awaitingPhysicalProgress)
      return true;

    if(Math.Abs(measured.Azimuth-lastProgressPosition.Azimuth)>=
         MinimumObservedProgressDegrees ||
       Math.Abs(measured.Elevation-lastProgressPosition.Elevation)>=
         MinimumObservedProgressDegrees)
    {
      lastProgressPosition=measured;
      lastProgressUtc=readUtc;
    }

    // Already reached the final target: an idle C2 sequence is not
    // evidence that a motor has stalled.
    if(!inFlightSetpoint.HasValue &&
       Math.Abs(requested.Azimuth-measured.Azimuth)<=1.5 &&
       Math.Abs(requested.Elevation-measured.Elevation)<=1.5)
    {
      awaitingPhysicalProgress=false;
      return true;
    }

    stalled=lastProgressUtc!=DateTime.MinValue &&
      readUtc-lastProgressUtc>=ProgressTimeout;
    return true;
  }

  internal static bool ValidQuantizedLimits(Rc3040sMotionPlanner.Limits limits) =>
    Rc3040sMotionPlanner.ValidLimits(limits) &&
    // GS-232B RC-3040S W encodes integer AZ=000..450, EL=000..180.
    // Non-Direct must fail closed when user limits cannot be represented.
    limits.MinAz>=0 && limits.MaxAz<=450 &&
    limits.MinEl>=0 && limits.MaxEl<=180 &&
    Math.Ceiling(limits.MinAz)<=Math.Floor(limits.MaxAz) &&
    Math.Ceiling(limits.MinEl)<=Math.Floor(limits.MaxEl);

  internal RotatorPosition BoundSetpoint(
    RotatorPosition actual,RotatorPosition desired,
    Rc3040sMotionPlanner.Limits limits)
  {
    if(!NativeRotatorCodec.Valid(actual) ||
       !NativeRotatorCodec.Valid(desired) ||
       !ValidQuantizedLimits(limits))
      throw new ArgumentOutOfRangeException(nameof(limits));
    return new RotatorPosition(
      BoundIntegerAxis(actual.Azimuth,desired.Azimuth,
        limits.MinAz,limits.MaxAz),
      BoundIntegerAxis(actual.Elevation,desired.Elevation,
        limits.MinEl,limits.MaxEl));
  }

  private static double BoundIntegerAxis(
    double actual,double desired,double min,double max)
  {
    double low=Math.Max(Math.Ceiling(min),Math.Ceiling(actual-MaximumLeadDegrees));
    double high=Math.Min(Math.Floor(max),Math.Floor(actual+MaximumLeadDegrees));
    if(low>high)
      throw new InvalidOperationException(
        "RC-3040S has no safe integer-degree waypoint within feedback lead and software limits");
    // Both limits are integer-degree wire endpoints, so the final W
    // cannot cross a fractional mechanical software limit after rounding.
    return Math.Clamp(Math.Round(desired,MidpointRounding.AwayFromZero),
      low,high);
  }

  internal void MarkTransmitted(
    DateTime timeUtc, RotatorPosition actual,
    RotatorPosition commanded)
  {
    if(timeUtc.Kind!=DateTimeKind.Utc)
      throw new ArgumentException("UTC required",nameof(timeUtc));
    // Track EVERY W actually transmitted, including a quantized W
    // only 0.5° from C2. Such a command can still start a physical step
    // or reset the controller ramp. A no-op W will be acknowledged by
    // two stable C2 reads and does not warrant an optimistic bypass.
    if(inFlightSetpoint.HasValue)
      throw new InvalidOperationException(
        "Previous RC-3040S W command is still awaiting endpoint confirmation.");
    inFlightSetpoint=commanded;
    inFlightSettledReads=0;
    firstSettledReadUtc=DateTime.MinValue;
    lastProgressUtc=timeUtc;
    lastProgressPosition=actual;
    awaitingPhysicalProgress=true;
  }
}
