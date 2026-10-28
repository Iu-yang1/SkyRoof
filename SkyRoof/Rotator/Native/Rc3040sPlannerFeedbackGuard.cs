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

  internal void Reset()
  {
    lastConsumedReadUtc=DateTime.MinValue;
    lastProgressUtc=DateTime.MinValue;
    lastProgressPosition=default;
    awaitingPhysicalProgress=false;
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
    if(Math.Abs(requested.Azimuth-measured.Azimuth)<=1.5 &&
       Math.Abs(requested.Elevation-measured.Elevation)<=1.5)
    {
      awaitingPhysicalProgress=false;
      return true;
    }

    stalled=lastProgressUtc!=DateTime.MinValue &&
      readUtc-lastProgressUtc>=ProgressTimeout;
    return true;
  }

  internal RotatorPosition BoundSetpoint(
    RotatorPosition actual,RotatorPosition desired)
  {
    if(!NativeRotatorCodec.Valid(actual) ||
       !NativeRotatorCodec.Valid(desired))
      throw new ArgumentOutOfRangeException(nameof(actual));
    return new RotatorPosition(
      Math.Clamp(desired.Azimuth,
        actual.Azimuth-MaximumLeadDegrees,
        actual.Azimuth+MaximumLeadDegrees),
      Math.Clamp(desired.Elevation,
        actual.Elevation-MaximumLeadDegrees,
        actual.Elevation+MaximumLeadDegrees));
  }

  internal void MarkTransmitted(
    DateTime timeUtc, RotatorPosition actual,
    RotatorPosition commanded)
  {
    if(timeUtc.Kind!=DateTimeKind.Utc)
      throw new ArgumentException("UTC required",nameof(timeUtc));
    // Ignore a zero-distance initial W caused by integer rounding.
    if(Math.Abs(commanded.Azimuth-actual.Azimuth)<0.75 &&
       Math.Abs(commanded.Elevation-actual.Elevation)<0.75)
      return;
    if(!awaitingPhysicalProgress)
    {
      lastProgressUtc=timeUtc;
      lastProgressPosition=actual;
      awaitingPhysicalProgress=true;
    }
  }
}
