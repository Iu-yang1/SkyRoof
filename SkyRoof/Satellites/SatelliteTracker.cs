using Serilog;
using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.TLE;
using SGPdotNET.Util;
using VE3NEA;

namespace SkyRoof
{
  public class SatelliteTracker
  {
    private Satellite? Satellite;
    private readonly MoonEphemeris? MoonFileEphemeris;
    private readonly bool UseBuiltInMoonFallback;

    public Tle? Tle;
    public bool Enabled { get; private set; }
    public bool IsMoon { get; }

    public SatelliteTracker(SatnogsDbTle? tle)
    {
      if (tle == null) return;

      Tle = CreateTle(tle);
      if (Tle == null) return;

      Satellite = new(Tle);
      Enabled = true;
    }

    internal SatelliteTracker(
      MoonEphemeris? moonFileEphemeris,
      bool useBuiltInMoonFallback)
    {
      IsMoon = true;
      MoonFileEphemeris = moonFileEphemeris;
      UseBuiltInMoonFallback = useBuiltInMoonFallback;
      Enabled = moonFileEphemeris != null || useBuiltInMoonFallback;
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
      if (!Enabled || IsMoon) return null;

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

      if (IsMoon)
        return ObserveMoon(groundStation, utc);

      try
      {
        return groundStation.Observe(Satellite!, utc);
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

    private TopocentricObservation? ObserveMoon(
      GroundStation groundStation,
      DateTime utc)
    {
      Bearing? bearing = MoonFileEphemeris?.GetBearing(utc);

      if (bearing == null && UseBuiltInMoonFallback)
      {
        var observer = new GeoPoint(
          groundStation.ObserverLatRad * Geo.DinR,
          groundStation.ObserverLonRad * Geo.DinR);

        bearing = MoonEphemeris.GetBuiltInMoonBearing(
          utc,
          observer,
          groundStation.ObserverAltKm * 1000.0);
      }

      if (bearing == null) return null;

      return new TopocentricObservation(
        Angle.FromRadians(bearing.Az),
        Angle.FromRadians(bearing.El),
        384400.0,
        0.0,
        groundStation.Location);
    }

    internal List<SatelliteVisibilityPeriod> ComputePasses(
      GroundStation groundStation,
      DateTime startTime,
      DateTime endTime)
    {
      if (!Enabled) return new();

      if (IsMoon)
        return ComputeMoonPasses(
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

    private List<SatelliteVisibilityPeriod> ComputeMoonPasses(
      GroundStation groundStation,
      DateTime startTime,
      DateTime endTime)
    {
      var result = new List<SatelliteVisibilityPeriod>();
      if (startTime >= endTime) return result;

      TimeSpan step = TimeSpan.FromMinutes(2);
      DateTime previousTime = startTime;
      TopocentricObservation? previous =
        ObserveMoon(groundStation, previousTime);
      bool previousAbove =
        previous != null && previous.Elevation.Radians >= 0;

      DateTime? passStart =
        previousAbove ? previousTime : null;

      DateTime t = startTime + step;

      while (t <= endTime)
      {
        TopocentricObservation? current =
          ObserveMoon(groundStation, t);
        bool currentAbove =
          current != null && current.Elevation.Radians >= 0;

        if (!previousAbove && currentAbove)
        {
          passStart = RefineMoonHorizonCrossing(
            groundStation,
            previousTime,
            t,
            rising: true);
        }
        else if (previousAbove && !currentAbove && passStart != null)
        {
          DateTime passEnd = RefineMoonHorizonCrossing(
            groundStation,
            previousTime,
            t,
            rising: false);

          result.Add(
            BuildMoonVisibilityPeriod(
              groundStation,
              passStart.Value,
              passEnd));

          passStart = null;
        }

        previousTime = t;
        previous = current;
        previousAbove = currentAbove;
        t += step;
      }

      if (passStart != null && previousAbove)
      {
        DateTime searchEnd = endTime;
        DateTime probe = endTime;

        // Match SGP.NET's non-clipped pass behavior closely enough for the
        // existing pass/rotator UI: if the Moon is still up at the requested
        // horizon, continue until LOS (or one sidereal-day bound).
        for (int i = 0; i < 12 * 60 / 2; i++)
        {
          DateTime next = probe + step;
          TopocentricObservation? obs =
            ObserveMoon(groundStation, next);
          bool above =
            obs != null && obs.Elevation.Radians >= 0;

          if (!above)
          {
            searchEnd = RefineMoonHorizonCrossing(
              groundStation,
              probe,
              next,
              rising: false);
            break;
          }

          probe = next;
          searchEnd = probe;
        }

        result.Add(
          BuildMoonVisibilityPeriod(
            groundStation,
            passStart.Value,
            searchEnd));
      }

      return result;
    }

    private SatelliteVisibilityPeriod BuildMoonVisibilityPeriod(
      GroundStation groundStation,
      DateTime start,
      DateTime end)
    {
      if (end <= start) end = start + TimeSpan.FromSeconds(1);

      TimeSpan duration = end - start;
      TimeSpan step =
        duration > TimeSpan.FromHours(2)
          ? TimeSpan.FromMinutes(5)
          : TimeSpan.FromMinutes(1);

      DateTime maxTime = start;
      double maxElevation = double.NegativeInfinity;

      for (DateTime t = start; t <= end; t += step)
      {
        double elevation =
          ObserveMoon(groundStation, t)?
            .Elevation.Radians
          ?? double.NegativeInfinity;

        if (elevation > maxElevation)
        {
          maxElevation = elevation;
          maxTime = t;
        }
      }

      DateTime before =
        maxTime - step < start ? start : maxTime - step;
      DateTime after =
        maxTime + step > end ? end : maxTime + step;

      for (int i = 0; i < 20; i++)
      {
        TimeSpan span = after - before;
        DateTime t1 = before + TimeSpan.FromTicks(span.Ticks / 3);
        DateTime t2 = after - TimeSpan.FromTicks(span.Ticks / 3);

        double e1 =
          ObserveMoon(groundStation, t1)?
            .Elevation.Radians
          ?? double.NegativeInfinity;
        double e2 =
          ObserveMoon(groundStation, t2)?
            .Elevation.Radians
          ?? double.NegativeInfinity;

        if (e1 < e2) before = t1;
        else after = t2;
      }

      maxTime = before + TimeSpan.FromTicks((after - before).Ticks / 2);
      maxElevation =
        ObserveMoon(groundStation, maxTime)?
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

    private DateTime RefineMoonHorizonCrossing(
      GroundStation groundStation,
      DateTime a,
      DateTime b,
      bool rising)
    {
      double ea =
        ObserveMoon(groundStation, a)?
          .Elevation.Radians
        ?? -Math.PI / 2;
      double eb =
        ObserveMoon(groundStation, b)?
          .Elevation.Radians
        ?? -Math.PI / 2;

      for (int i = 0; i < 28; i++)
      {
        DateTime m =
          a + TimeSpan.FromTicks((b - a).Ticks / 2);
        double em =
          ObserveMoon(groundStation, m)?
            .Elevation.Radians
          ?? -Math.PI / 2;

        if (rising)
        {
          if (em >= 0) b = m;
          else a = m;
        }
        else
        {
          if (em >= 0) a = m;
          else b = m;
        }
      }

      return rising ? b : a;
    }

    internal bool IsGeoStationary()
    {
      if (!Enabled || IsMoon) return false;
      return Math.Abs(Tle!.MeanMotionRevPerDay - 1) < 0.1f;
    }

    internal List<SatelliteVisibilityPeriod> ComputeGeostationaryPasses(
      GroundStation groundStation)
    {
      var observation =
        Observe(groundStation, DateTime.UtcNow);
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
