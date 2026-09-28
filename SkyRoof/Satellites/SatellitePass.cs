using System.Drawing.Printing;
using SGPdotNET.Observation;
using SGPdotNET.TLE;
using SGPdotNET.Util;
using VE3NEA;
using VE3NEA.Clock;
using static SkyRoof.SatellitePass;

namespace SkyRoof
{
  public class SatellitePass
  {
    public class TrackPoint
    {
      public DateTime Utc;
      public TopocentricObservation? Observation;
    }

    private const double TwoPi = 2 * Math.PI;
    private readonly TimeSpan TrackStep = TimeSpan.FromSeconds(10);

    private GroundStation GroundStation;
    public SatnogsDbSatellite Satellite;

    public DateTime StartTime, CulminationTime, EndTime;
    public double MaxElevation;
    public int OrbitNumber;
    public bool HasOrbitNumber => Satellite.Tracker.Tle != null;
    public bool Geostationary { get; private set; }
    private SatelliteVisibilityPeriod SatelliteVisibilityPeriod;

    public PointF[]? MiniPath;
    private PointF[] hump;
    private List<TrackPoint> track;
    public List<TrackPoint> Track { get => track ?? MakeTrack(); }



    public SatellitePass(GroundStation groundStation, SatnogsDbSatellite satellite, SatelliteVisibilityPeriod visibilityPeriod)
    {
      GroundStation = groundStation;
      Satellite = satellite;
      SatelliteVisibilityPeriod = visibilityPeriod;
      StartTime = visibilityPeriod.Start;
      CulminationTime = visibilityPeriod.MaxElevationTime;
      EndTime = visibilityPeriod.End;
      MaxElevation = visibilityPeriod.MaxElevation.Degrees;
      OrbitNumber = ComputeOrbitNumber();
      Geostationary = satellite.Tracker.IsGeoStationary();
    }






    //----------------------------------------------------------------------------------------------
    //                                         get
    //----------------------------------------------------------------------------------------------

    internal TopocentricObservation? GetObservationAt(DateTime utc)
    {
      return Satellite.Tracker.Observe(GroundStation, utc);
    }

    internal bool IsActive()
    {
      return StartTime < DateTime.UtcNow && EndTime > DateTime.UtcNow;
    }

    internal string[] GetTooltipText(bool showSeconds = true)
    {
      if (Geostationary) return [$"Geostationary   orbit #{OrbitNumber}", "", "", "", "", ""];

      var now = DateTime.UtcNow;
      var obs1 = GetObservationAt(now);
      var obs2 = GetObservationAt(now.AddMinutes(1));
      bool down = obs1 == null || obs2 == null || obs1.Elevation > obs2.Elevation;
      string upDpwn = down ? "↓" : "↑";
  
      var aos = GetObservationAt(StartTime);
      var los = GetObservationAt(EndTime);

      string[] tooltip = new string[6];

      if (EndTime < DateTime.UtcNow) tooltip[0] = "Ended.";
      else if (StartTime < DateTime.UtcNow) tooltip[0] = $"LOS ↓  in {Utils.TimespanToString(EndTime - DateTime.UtcNow, showSeconds)}";
      else tooltip[0] = $"AOS ↑  in {Utils.TimespanToString(StartTime - DateTime.UtcNow, showSeconds)}";

      tooltip[1] = $"AOS azimuth {aos?.Azimuth.Degrees:F0}º at {ClockWidget.Stamp(StartTime, "yyyy-MM-dd HH:mm:ss")}";
      tooltip[2] = $"LOS azimuth {los?.Azimuth.Degrees:F0}º at {ClockWidget.Stamp(EndTime, "yyyy-MM-dd HH:mm:ss")}";
      tooltip[3] = $"Duration: {Utils.TimespanToString(EndTime - StartTime, false)}";
      tooltip[4] = $"Elevation: {obs1?.Elevation.Degrees:F0}º {upDpwn}  (Max {MaxElevation:F0}º at {ClockWidget.Stamp(CulminationTime, "HH:mm")})";
      tooltip[5] = HasOrbitNumber
        ? $"Orbit: #{OrbitNumber}"
        : $"Ephemeris window: {ClockWidget.Stamp(StartTime, "yyyy-MM-dd")}";

      return tooltip;
    }




    //----------------------------------------------------------------------------------------------
    //                                        compute
    //----------------------------------------------------------------------------------------------
    private int ComputeOrbitNumber()
    {
      if (!Satellite.Tracker.Enabled) return 0;

      Tle? tle = Satellite.Tracker.Tle;
      if (tle == null)
      {
        // Solar-system targets do not have a TLE revolution number. Existing
        // scheduling/rotator code uses this integer as part of pass identity,
        // so use a stable date key derived from the recovered AOS. Moon, Sun
        // and Venus have at most one normal rise window per civil day.
        return checked(
          (int)Math.Floor(
            (StartTime.ToUniversalTime() - DateTime.UnixEpoch)
              .TotalMinutes));
      }

      uint revNum = tle.OrbitNumber;
      var timeSinceOrbit = (SatelliteVisibilityPeriod.MaxElevationTime - tle.Epoch).TotalDays;
      var revPerDay = tle.MeanMotionRevPerDay;
      var drag = tle.BStarDragTerm;
      var meanAnomaly = tle.MeanAnomaly.Radians;
      var argumentPerigee = tle.ArgumentPerigee.Radians;

      return (int)(
        revNum +
        Math.Floor((meanAnomaly + argumentPerigee) / TwoPi)
        + (revPerDay + drag * timeSinceOrbit) * timeSinceOrbit);

      // (int)((revPerDay + drag * timeSinceOrbit) * timeSinceOrbit + (meanAnomaly + argumentPerigee) / TwoPi)
      // - (int)((meanAnomaly + argumentPerigee) / TwoPi)
      // + (int)revNum;
    }

    public PointF[] ComputeHump(float x0, float y0, float scaleX, float scaleY)
    {
      for (int i = 0; i < Track.Count; i++)
        hump[i] = new(
          x0 + (float)(Track[i].Utc - StartTime).TotalMinutes * scaleX, 
          y0 - (float)Track[i].Observation.Elevation.Degrees * scaleY);

      return hump;
    }

    private List<TrackPoint> MakeTrack()
    {
      int stepCount = 63;
      if (Geostationary) stepCount = 3;
      TimeSpan step = (EndTime - StartTime) / stepCount;
      if (step < TimeSpan.FromSeconds(10)) step = TimeSpan.FromSeconds(10);
      stepCount = (int)Math.Round((EndTime - StartTime) / step);

      track = new();
      
      for (int i=0; i <= stepCount; i++)
        {
          var trackPoint = new TrackPoint();
          trackPoint.Utc = StartTime + i * step;
          trackPoint.Observation = GetObservationAt(trackPoint.Utc);
          track.Add(trackPoint);
        }

      hump = new PointF[track.Count];

      return track.Where(p => p != null).ToList();
    }


    private const int StepCount = 10;
    public void MakeMiniPath()
    {
      if (MiniPath != null) return;

      var duration = EndTime - StartTime;
      var step = duration / StepCount;
      MiniPath = new PointF[StepCount + 1];

      for (int i = 0; i <= StepCount; i++)
      {
        var utc = StartTime + step * i;
        var observation = Satellite.Tracker.Observe(GroundStation, utc);
        if (observation == null) continue;

        double ro = 1 - observation.Elevation.Radians / Utils.HalfPi;
        double phi = Utils.HalfPi - observation.Azimuth.Radians;

        MiniPath[i] = new PointF((float)(ro * Math.Cos(phi)), (float)(ro * Math.Sin(phi)));
      }
    }

    // boundary polygon of the area covered by the satellite during this pass, as absolute geo points
    public List<GeoPoint> GetCoveragePolygon()
    {
      // only for LEO
      if (EndTime - StartTime > TimeSpan.FromHours(2)) return new();

      // sub-satellite points and footprint radii along the pass.
      // propagate our own geodetic samples; we don't need the topocentric Track here
      const int steps = 32;
      var stepTime = (EndTime - StartTime) / steps;
      var centers = new List<GeoPoint>(steps + 1);
      var radii = new List<double>(steps + 1); // km
      for (int i = 0; i <= steps; i++)
      {
        var p = Satellite.Tracker.Predict(StartTime + i * stepTime);
        if (p == null) continue;
        centers.Add(new GeoPoint(p.Latitude.Degrees, p.Longitude.Degrees));
        radii.Add(p.GetFootprint());
      }
      if (centers.Count < 3) return new();

      var leftPoints = new List<GeoPoint>();
      var rightPoints = new List<GeoPoint>();

      for (int i = 0; i < centers.Count; i++)
      {
        var G = centers[i];
        var radius = radii[i];

        // local track bearing at G, centered difference
        var prevG = centers[Math.Max(0, i - 1)];
        var nextG = centers[Math.Min(centers.Count - 1, i + 1)];
        var azim = (nextG - prevG).Azimuth;

        // start cap: back semicircle around AOS
        if (i == 0)
          for (double a = 4; a < 180; a += 4)
            leftPoints.Add(G + new GeoPath(azim + 90 + a, radius));

        leftPoints.Add(G + new GeoPath(azim - 90, radius));
        rightPoints.Add(G + new GeoPath(azim + 90, radius));

        // end cap: front semicircle around LOS
        if (i == centers.Count - 1)
          for (double a = 4; a < 180; a += 4)
            leftPoints.Add(G + new GeoPath(azim - 90 + a, radius));
      }

      rightPoints.Reverse();
      leftPoints.AddRange(rightPoints);

      // remove inner-offset self-intersections: a vertex that lies strictly inside
      // any footprint disk is not on the true outer boundary. dropping these clips
      // the kinks/loops the offset rails produce where the track curves sharply.
      var boundary = new List<GeoPoint>(leftPoints.Count);
      foreach (var pt in leftPoints)
      {
        bool inside = false;
        for (int j = 0; j < centers.Count; j++)
          if ((pt - centers[j]).Distance < radii[j] - 1.0) { inside = true; break; } // 1 km margin keeps tangent points
        if (!inside) boundary.Add(pt);
      }

      if (boundary.Count < 3) return new();

      // close the loop
      boundary.Add(boundary[0]);

      return boundary;
    }

    internal bool IsAboveHorizon()
    {
      return StartTime < DateTime.UtcNow && EndTime > DateTime.UtcNow;
    }
  }
}
