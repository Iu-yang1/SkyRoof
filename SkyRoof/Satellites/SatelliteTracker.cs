using Serilog;
using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.TLE;
using SGPdotNET.Util;

namespace SkyRoof
{
  public class SatelliteTracker
  {
    private Satellite? Satellite;
    private readonly JplSpkKernel? JplKernel;
    private readonly JplBody? EphemerisBody;

    public Tle? Tle;
    public bool Enabled { get; private set; }
    internal bool IsJplEphemeris => EphemerisBody.HasValue;

    public SatelliteTracker(SatnogsDbTle? tle)
    {
      if (tle == null) return;

      Tle = CreateTle(tle);
      if (Tle == null) return;

      Satellite = new(Tle);
      Enabled = true;
    }

    internal SatelliteTracker(
      JplSpkKernel kernel,
      JplBody body)
    {
      JplKernel = kernel;
      EphemerisBody = body;
      Enabled = kernel.Supports(body, DateTime.UtcNow);
    }

    public Tle? CreateTle(SatnogsDbTle tle)
    {
      try
      {
        return new Tle(tle.tle0, tle.tle1, tle.tle2);
      }
      catch (Exception ex)
      {
        Log.Error(
          ex,
          $"Error creating TLE object: |{tle.tle0}|{tle.tle1}|{tle.tle2}|.");
        return null;
      }
    }

    public GeodeticCoordinate? Predict(DateTime? utc = null)
    {
      if (!Enabled || IsJplEphemeris) return null;

      try
      {
        return Satellite!
          .Predict(utc ?? DateTime.UtcNow)
          .ToGeodetic();
      }
      catch (Exception ex)
      {
        Enabled = false;
        Log.Error(
          ex,
          $"Predict failed for {Tle!.Name} ({Tle.NoradNumber}).");
        return null;
      }
    }

    internal TopocentricObservation? Observe(
      GroundStation groundStation,
      DateTime utc)
    {
      if (!Enabled) return null;

      if (EphemerisBody is JplBody body)
      {
        try
        {
          return JplKernel!.Observe(
            body,
            groundStation,
            utc);
        }
        catch (Exception ex)
        {
          Log.Error(
            ex,
            $"JPL ephemeris observation failed for {body}.");
          return null;
        }
      }

      try
      {
        return groundStation.Observe(
          Satellite!,
          utc);
      }
      catch (Exception ex)
      {
        Enabled = false;
        Log.Error(
          ex,
          $"Observe failed for {Tle!.Name} ({Tle.NoradNumber}).");
        return null;
      }
    }

    internal List<SatelliteVisibilityPeriod> ComputePasses(
      GroundStation groundStation,
      DateTime startTime,
      DateTime endTime)
    {
      if (!Enabled) return new();

      if (IsJplEphemeris)
        return ComputeEphemerisPasses(
          groundStation,
          startTime,
          endTime);

      try
      {
        return groundStation.Observe(
          Satellite!,
          startTime,
          endTime,
          TimeSpan.FromSeconds(15),
          clipToStartTime: false);
      }
      catch (Exception ex)
      {
        Enabled = false;
        Log.Error(
          ex,
          $"ComputePasses failed for {Tle!.Name} ({Tle.NoradNumber}).");
        return new();
      }
    }

    private List<SatelliteVisibilityPeriod> ComputeEphemerisPasses(
      GroundStation groundStation,
      DateTime startTime,
      DateTime endTime)
    {
      var result = new List<SatelliteVisibilityPeriod>();
      if (startTime >= endTime) return result;

      TimeSpan step = TimeSpan.FromMinutes(2);
      DateTime previousTime = startTime;
      TopocentricObservation? previous =
        Observe(groundStation, previousTime);
      bool previousAbove =
        previous != null &&
        previous.Elevation.Radians >= 0;

      DateTime? passStart = null;

      if (previousAbove)
      {
        // Match SGP.NET's clipToStartTime:false semantics. When the search
        // begins while Moon/Sun/Venus is already above the horizon, recover
        // the real AOS instead of calling startTime the pass start. A stable
        // AOS is important for pass identity across periodic rebuilds.
        DateTime upper = startTime;
        DateTime lower = upper - step;

        for (int i = 0; i < 8 * 60; i++)
        {
          TopocentricObservation? obs =
            Observe(groundStation, lower);

          if (obs == null ||
              obs.Elevation.Radians < 0)
          {
            passStart =
              RefineHorizonCrossing(
                groundStation,
                lower,
                upper,
                rising: true);
            break;
          }

          upper = lower;
          lower -= step;
        }

        // Circumpolar/polar-day fallback: the body may remain above the
        // horizon throughout our bounded search. Keep the requested start in
        // that exceptional case rather than dropping the target entirely.
        passStart ??= startTime;
      }

      for (DateTime t = startTime + step;
           t <= endTime;
           t += step)
      {
        TopocentricObservation? current =
          Observe(groundStation, t);
        bool currentAbove =
          current != null &&
          current.Elevation.Radians >= 0;

        if (!previousAbove && currentAbove)
        {
          passStart = RefineHorizonCrossing(
            groundStation,
            previousTime,
            t,
            rising: true);
        }
        else if (previousAbove &&
                 !currentAbove &&
                 passStart != null)
        {
          DateTime passEnd =
            RefineHorizonCrossing(
              groundStation,
              previousTime,
              t,
              rising: false);

          result.Add(
            BuildVisibilityPeriod(
              groundStation,
              passStart.Value,
              passEnd));

          passStart = null;
        }

        previousTime = t;
        previousAbove = currentAbove;
      }

      if (passStart != null && previousAbove)
      {
        DateTime probe = endTime;
        DateTime passEnd = endTime;

        // Continue at most 16 hours so a pass that crosses the caller's end
        // remains a complete rise/set interval like SGP.NET visibility periods.
        for (int i = 0; i < 8 * 60; i++)
        {
          DateTime next =
            probe + step;
          TopocentricObservation? obs =
            Observe(groundStation, next);
          bool above =
            obs != null &&
            obs.Elevation.Radians >= 0;

          if (!above)
          {
            passEnd = RefineHorizonCrossing(
              groundStation,
              probe,
              next,
              rising: false);
            break;
          }

          probe = next;
          passEnd = probe;
        }

        result.Add(
          BuildVisibilityPeriod(
            groundStation,
            passStart.Value,
            passEnd));
      }

      return result;
    }

    private SatelliteVisibilityPeriod BuildVisibilityPeriod(
      GroundStation groundStation,
      DateTime start,
      DateTime end)
    {
      if (end <= start)
        end =
          start + TimeSpan.FromSeconds(1);

      TimeSpan duration = end - start;
      TimeSpan coarseStep =
        duration > TimeSpan.FromHours(2)
          ? TimeSpan.FromMinutes(5)
          : TimeSpan.FromMinutes(1);

      DateTime maxTime = start;
      double maxElevation =
        double.NegativeInfinity;

      for (DateTime t = start;
           t <= end;
           t += coarseStep)
      {
        double elevation =
          Observe(groundStation, t)?
            .Elevation.Radians
          ?? double.NegativeInfinity;

        if (elevation > maxElevation)
        {
          maxElevation = elevation;
          maxTime = t;
        }
      }

      DateTime before =
        maxTime - coarseStep < start
          ? start
          : maxTime - coarseStep;
      DateTime after =
        maxTime + coarseStep > end
          ? end
          : maxTime + coarseStep;

      for (int i = 0; i < 20; i++)
      {
        TimeSpan span = after - before;
        DateTime t1 =
          before +
          TimeSpan.FromTicks(
            span.Ticks / 3);
        DateTime t2 =
          after -
          TimeSpan.FromTicks(
            span.Ticks / 3);

        double e1 =
          Observe(groundStation, t1)?
            .Elevation.Radians
          ?? double.NegativeInfinity;
        double e2 =
          Observe(groundStation, t2)?
            .Elevation.Radians
          ?? double.NegativeInfinity;

        if (e1 < e2)
          before = t1;
        else
          after = t2;
      }

      maxTime =
        before +
        TimeSpan.FromTicks(
          (after - before).Ticks / 2);
      maxElevation =
        Observe(groundStation, maxTime)?
          .Elevation.Radians
        ?? 0;

      return new SatelliteVisibilityPeriod(
        null!,
        start,
        end,
        Angle.FromRadians(maxElevation),
        maxTime,
        groundStation.Location);
    }

    private DateTime RefineHorizonCrossing(
      GroundStation groundStation,
      DateTime a,
      DateTime b,
      bool rising)
    {
      for (int i = 0; i < 28; i++)
      {
        DateTime midpoint =
          a +
          TimeSpan.FromTicks(
            (b - a).Ticks / 2);

        double elevation =
          Observe(groundStation, midpoint)?
            .Elevation.Radians
          ?? -Math.PI / 2;

        if (rising)
        {
          if (elevation >= 0)
            b = midpoint;
          else
            a = midpoint;
        }
        else
        {
          if (elevation >= 0)
            a = midpoint;
          else
            b = midpoint;
        }
      }

      return rising ? b : a;
    }

    internal bool IsGeoStationary()
    {
      if (!Enabled || IsJplEphemeris)
        return false;

      return
        Math.Abs(
          Tle!.MeanMotionRevPerDay -
          1) <
        0.1f;
    }

    internal List<SatelliteVisibilityPeriod> ComputeGeostationaryPasses(
      GroundStation groundStation)
    {
      var observation =
        Observe(
          groundStation,
          DateTime.UtcNow);

      if (observation == null ||
          observation.Elevation.Radians < 0)
        return new();

      return new()
      {
        new SatelliteVisibilityPeriod(
          null!,
          DateTime.UtcNow,
          DateTime.UtcNow + TimeSpan.FromDays(1),
          observation.Elevation,
          DateTime.UtcNow + TimeSpan.FromHours(12),
          groundStation.Location)
      };
    }
  }
}
