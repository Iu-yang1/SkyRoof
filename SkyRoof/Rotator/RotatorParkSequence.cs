namespace SkyRoof
{
  // User-saved raw rotctld coordinates (before configured az/el offsets).
  // Keep the last waypoint synchronized with legacy ParkAzimuth/ParkElevation.
  public sealed class RotatorParkWaypoint
  {
    public float Azimuth { get; set; }
    public float Elevation { get; set; }
  }

  internal enum ParkSequenceProgress
  {
    Waiting,
    NextWaypoint,
    Completed,
    Aborted
  }

  // Pure, testable arrival state machine. A waypoint must receive two
  // *distinct fresh* rotctld position replies within tolerance; command
  // acceptance alone never advances the route.
  internal sealed class RotatorParkSequence
  {
    private readonly (double Az, double El)[] Waypoints;
    private DateTime SegmentStartedUtc;
    private long LastObservationTicks;
    private int ConsecutiveArrivals;

    internal int Index { get; private set; }
    internal int Count => Waypoints.Length;
    internal (double Az, double El) Current => Waypoints[Index];

    internal RotatorParkSequence(
      IEnumerable<(double Az, double El)> waypoints,
      DateTime startedUtc)
    {
      Waypoints = waypoints.ToArray();
      if (Waypoints.Length == 0 ||
          Waypoints.Any(w => !double.IsFinite(w.Az) || !double.IsFinite(w.El)))
        throw new ArgumentException("A PARK route needs valid coordinates.", nameof(waypoints));
      SegmentStartedUtc = startedUtc;
    }

    internal ParkSequenceProgress Observe(
      DateTime nowUtc,
      DateTime lastReadUtc,
      double actualAz,
      double actualEl,
      bool connected)
    {
      // Never advance on stale feedback, lost connection, or a stuck motor.
      if (!connected ||
          !double.IsFinite(actualAz) ||
          !double.IsFinite(actualEl) ||
          nowUtc - lastReadUtc > TimeSpan.FromSeconds(12) ||
          nowUtc - SegmentStartedUtc > TimeSpan.FromMinutes(5))
        return ParkSequenceProgress.Aborted;

      if (lastReadUtc.Ticks <= LastObservationTicks)
        return ParkSequenceProgress.Waiting;
      LastObservationTicks = lastReadUtc.Ticks;

      // Compare mechanical coordinates, NOT modulo-360 degrees: the rotator
      // may be configured for a 450-degree azimuth sweep.
      var target = Current;
      if (Math.Abs(actualAz - target.Az) <= 1.5 &&
          Math.Abs(actualEl - target.El) <= 1.5)
        ConsecutiveArrivals++;
      else
        ConsecutiveArrivals = 0;

      if (ConsecutiveArrivals < 2)
        return ParkSequenceProgress.Waiting;

      Index++;
      if (Index >= Waypoints.Length)
        return ParkSequenceProgress.Completed;

      ConsecutiveArrivals = 0;
      SegmentStartedUtc = nowUtc;
      return ParkSequenceProgress.NextWaypoint;
    }
  }
}
