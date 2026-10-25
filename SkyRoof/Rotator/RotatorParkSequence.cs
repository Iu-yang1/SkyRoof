namespace SkyRoof
{
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
    Aborted,
    StopRequested
  }

  /// <summary>
  /// A position readback is not a limit switch, absolute encoder, or verified
  /// mechanical home. RC-3040S mode requires a transmitted W, fresh C2 replies,
  /// a settling dwell, a transmitted S, and additional C2 replies after S.
  /// Other rotator protocols retain their existing two-sample behavior.
  /// </summary>
  internal sealed class RotatorParkSequence
  {
    private readonly (double Az, double El)[] Waypoints;
    private readonly bool verifyStopReadback;
    private DateTime segmentStartedUtc,settlingFirstUtc,stopRequestedUtc;
    private DateTime postStopFirstUtc;
    private long lastObservationTicks;
    private int consecutiveArrivals,postStopReads;
    private double previousAz,previousEl;
    private bool havePrevious,stopPending;

    internal int Index { get; private set; }
    internal int Count => Waypoints.Length;
    internal (double Az, double El) Current => Waypoints[Index];
    internal bool IsSettling => verifyStopReadback &&
      consecutiveArrivals>0 && !stopPending;
    internal bool AwaitingStopReadback => stopPending;
    internal bool MechanicalHomeVerified => false;
    internal string AbortReason { get; private set; } = "";

    internal RotatorParkSequence(
      IEnumerable<(double Az, double El)> waypoints,
      DateTime startedUtc,bool verifyStopReadback=false)
    {
      Waypoints=waypoints.ToArray();
      if(Waypoints.Length==0 ||
         Waypoints.Any(w=>!double.IsFinite(w.Az)||!double.IsFinite(w.El)))
        throw new ArgumentException("PARK needs valid coordinates.",nameof(waypoints));
      this.verifyStopReadback=verifyStopReadback;
      segmentStartedUtc=startedUtc;
    }

    internal ParkSequenceProgress Observe(DateTime nowUtc,DateTime lastReadUtc,
      double actualAz,double actualEl,bool connected,
      DateTime commandSentUtc=default,DateTime stopSentUtc=default)
    {
      if(!connected || !double.IsFinite(actualAz) || !double.IsFinite(actualEl))
        return Abort("Control link lost or position invalid");
      if(lastReadUtc==default || nowUtc-lastReadUtc>TimeSpan.FromSeconds(12))
        return Abort("No fresh C2 position feedback");
      if(nowUtc-segmentStartedUtc>TimeSpan.FromMinutes(5))
        return Abort("PARK waypoint timeout");

      // A timer or duplicate GUI event may never count the same reply twice.
      if(lastReadUtc.Ticks<=lastObservationTicks)
      {
        if(stopPending &&
           nowUtc-stopRequestedUtc>TimeSpan.FromSeconds(8))
          return Abort("STOP transmission/readback timeout");
        return ParkSequenceProgress.Waiting;
      }
      lastObservationTicks=lastReadUtc.Ticks;

      if(!verifyStopReadback)
      {
        if(Within(actualAz,actualEl,1.5))consecutiveArrivals++;
        else consecutiveArrivals=0;
        if(consecutiveArrivals<2)return ParkSequenceProgress.Waiting;
        return Advance(nowUtc);
      }

      // Command enqueue/acceptance is not sufficient: wait until the motor I/O
      // worker has actually transmitted the W frame for this exact waypoint.
      if(commandSentUtc<=segmentStartedUtc)
        return ParkSequenceProgress.Waiting;

      if(stopPending)
      {
        if(nowUtc-stopRequestedUtc>TimeSpan.FromSeconds(8))
          return Abort("STOP not followed by verified C2 feedback");
        if(stopSentUtc<=stopRequestedUtc || lastReadUtc<=stopSentUtc)
          return ParkSequenceProgress.Waiting;
        if(!Within(actualAz,actualEl,1.0) ||
           havePrevious && (Math.Abs(actualAz-previousAz)>0.5 ||
                            Math.Abs(actualEl-previousEl)>0.5))
          return Abort("Position changed after PARK STOP");
        if(postStopReads==0)postStopFirstUtc=lastReadUtc;
        postStopReads++;
        previousAz=actualAz;previousEl=actualEl;havePrevious=true;
        if(postStopReads<2 ||
           lastReadUtc-postStopFirstUtc<TimeSpan.FromSeconds(1))
          return ParkSequenceProgress.Waiting;
        Index++;
        return ParkSequenceProgress.Completed;
      }

      if(lastReadUtc<=commandSentUtc)
        return ParkSequenceProgress.Waiting;

      // 2.5 seconds and >=3 distinct C2 replies, with no more than half a
      // degree of movement between replies. Both axes must be in tolerance.
      if(!Within(actualAz,actualEl,1.0) ||
         havePrevious && (Math.Abs(actualAz-previousAz)>0.5 ||
                          Math.Abs(actualEl-previousEl)>0.5))
      {
        consecutiveArrivals=0;
        settlingFirstUtc=default;
      }
      else
      {
        if(consecutiveArrivals==0)settlingFirstUtc=lastReadUtc;
        consecutiveArrivals++;
      }
      previousAz=actualAz;previousEl=actualEl;havePrevious=true;
      if(consecutiveArrivals<3 ||
         lastReadUtc-settlingFirstUtc<TimeSpan.FromSeconds(2.5))
        return ParkSequenceProgress.Waiting;

      if(Index==Waypoints.Length-1)
      {
        stopPending=true;
        stopRequestedUtc=nowUtc;
        postStopReads=0;
        havePrevious=false;
        return ParkSequenceProgress.StopRequested;
      }
      return Advance(nowUtc);
    }

    private bool Within(double az,double el,double tolerance) =>
      Math.Abs(az-Current.Az)<=tolerance &&
      Math.Abs(el-Current.El)<=tolerance;

    private ParkSequenceProgress Advance(DateTime nowUtc)
    {
      Index++;
      if(Index>=Waypoints.Length)return ParkSequenceProgress.Completed;
      segmentStartedUtc=nowUtc;
      consecutiveArrivals=0;
      lastObservationTicks=0;
      settlingFirstUtc=default;
      havePrevious=false;
      return ParkSequenceProgress.NextWaypoint;
    }

    private ParkSequenceProgress Abort(string reason)
    {
      AbortReason=reason;
      return ParkSequenceProgress.Aborted;
    }
  }
}
