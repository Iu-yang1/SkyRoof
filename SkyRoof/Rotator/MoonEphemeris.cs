using System.Globalization;
using VE3NEA;

namespace SkyRoof
{
  internal sealed class MoonEphemeris
  {
    private sealed record Point(DateTime Utc, Bearing Bearing);

    private readonly List<Point> Points;

    private MoonEphemeris(List<Point> points)
    {
      Points = points
        .OrderBy(p => p.Utc)
        .GroupBy(p => p.Utc)
        .Select(g => g.Last())
        .ToList();
    }

    internal static MoonEphemeris? TryLoad(string? path)
    {
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return null;

      try
      {
        var points = ParseCsv(File.ReadAllLines(path));
        return points.Count >= 2 ? new MoonEphemeris(points) : null;
      }
      catch (Exception ex)
      {
        Serilog.Log.Warning(ex, $"Unable to load Moon ephemeris: {path}");
        return null;
      }
    }

    internal Bearing? GetBearing(DateTime utc)
    {
      if (Points.Count < 2 ||
          utc < Points[0].Utc ||
          utc > Points[^1].Utc)
        return null;

      int hi = Points.BinarySearch(
        new Point(utc, new Bearing(0, 0)),
        Comparer<Point>.Create((a, b) => a.Utc.CompareTo(b.Utc)));

      if (hi >= 0) return Points[hi].Bearing;

      hi = ~hi;
      if (hi <= 0 || hi >= Points.Count) return null;

      Point p0 = Points[hi - 1];
      Point p1 = Points[hi];

      // Do not bridge a large hole in an imported file.
      if (p1.Utc - p0.Utc > TimeSpan.FromMinutes(30))
        return null;

      double f = (utc - p0.Utc).TotalSeconds /
        (p1.Utc - p0.Utc).TotalSeconds;

      double az0 = p0.Bearing.Az;
      double az1 = p1.Bearing.Az;
      double da = NormalizePi(az1 - az0);

      return new Bearing(
        NormalizeTwoPi(az0 + da * f),
        p0.Bearing.El + (p1.Bearing.El - p0.Bearing.El) * f);
    }

    internal static Bearing GetBuiltInMoonBearing(
      DateTime utc,
      GeoPoint observer,
      double altitudeMeters)
    {
      utc = utc.Kind == DateTimeKind.Utc
        ? utc
        : utc.ToUniversalTime();

      double jd = 2440587.5 +
        (utc - DateTime.UnixEpoch).TotalDays;
      double d = jd - 2451543.5;

      double n = ToRad(NormalizeDeg(125.1228 - 0.0529538083 * d));
      double i = ToRad(5.1454);
      double w = ToRad(NormalizeDeg(318.0634 + 0.1643573223 * d));
      double a = 60.2666;
      double e = 0.054900;
      double mm = ToRad(NormalizeDeg(115.3654 + 13.0649929509 * d));

      double ms = ToRad(NormalizeDeg(356.0470 + 0.9856002585 * d));
      double ws = ToRad(NormalizeDeg(282.9404 + 4.70935e-5 * d));
      double ls = NormalizeTwoPi(ms + ws);

      double eccentricAnomaly = mm;
      for (int iteration = 0; iteration < 8; iteration++)
        eccentricAnomaly -=
          (eccentricAnomaly - e * Math.Sin(eccentricAnomaly) - mm) /
          (1 - e * Math.Cos(eccentricAnomaly));

      double xOrb = a * (Math.Cos(eccentricAnomaly) - e);
      double yOrb = a * Math.Sqrt(1 - e * e) * Math.Sin(eccentricAnomaly);
      double r = Math.Sqrt(xOrb * xOrb + yOrb * yOrb);
      double trueAnomaly = Math.Atan2(yOrb, xOrb);
      double lonArg = trueAnomaly + w;

      double xEcl =
        r * (Math.Cos(n) * Math.Cos(lonArg) -
             Math.Sin(n) * Math.Sin(lonArg) * Math.Cos(i));
      double yEcl =
        r * (Math.Sin(n) * Math.Cos(lonArg) +
             Math.Cos(n) * Math.Sin(lonArg) * Math.Cos(i));
      double zEcl = r * Math.Sin(lonArg) * Math.Sin(i);

      double lon = Math.Atan2(yEcl, xEcl);
      double lat = Math.Atan2(
        zEcl,
        Math.Sqrt(xEcl * xEcl + yEcl * yEcl));

      double lm = NormalizeTwoPi(n + w + mm);
      double elongation = NormalizePi(lm - ls);
      double argumentLatitude = NormalizePi(lm - n);

      lon += ToRad(
        -1.274 * Math.Sin(mm - 2 * elongation) +
         0.658 * Math.Sin(2 * elongation) -
         0.186 * Math.Sin(ms) -
         0.059 * Math.Sin(2 * mm - 2 * elongation) -
         0.057 * Math.Sin(mm - 2 * elongation + ms) +
         0.053 * Math.Sin(mm + 2 * elongation) +
         0.046 * Math.Sin(2 * elongation - ms) +
         0.041 * Math.Sin(mm - ms) -
         0.035 * Math.Sin(elongation) -
         0.031 * Math.Sin(mm + ms) -
         0.015 * Math.Sin(2 * argumentLatitude - 2 * elongation) +
         0.011 * Math.Sin(mm - 4 * elongation));

      lat += ToRad(
        -0.173 * Math.Sin(argumentLatitude - 2 * elongation) -
         0.055 * Math.Sin(mm - argumentLatitude - 2 * elongation) -
         0.046 * Math.Sin(mm + argumentLatitude - 2 * elongation) +
         0.033 * Math.Sin(argumentLatitude + 2 * elongation) +
         0.017 * Math.Sin(2 * mm + argumentLatitude));

      r +=
        -0.58 * Math.Cos(mm - 2 * elongation) -
         0.46 * Math.Cos(2 * elongation);

      double cosLat = Math.Cos(lat);
      xEcl = r * Math.Cos(lon) * cosLat;
      yEcl = r * Math.Sin(lon) * cosLat;
      zEcl = r * Math.Sin(lat);

      double obliquity =
        ToRad(23.4393 - 3.563e-7 * d);

      double xEq = xEcl;
      double yEq =
        yEcl * Math.Cos(obliquity) -
        zEcl * Math.Sin(obliquity);
      double zEq =
        yEcl * Math.Sin(obliquity) +
        zEcl * Math.Cos(obliquity);

      double ra = Math.Atan2(yEq, xEq);
      double dec = Math.Atan2(
        zEq,
        Math.Sqrt(xEq * xEq + yEq * yEq));

      // Topocentric correction. The Moon's horizontal parallax is close to one
      // degree, so geocentric RA/Dec is not accurate enough for EME pointing.
      double phi = observer.LatitudeRad;
      double longitude = observer.LongitudeRad;
      double pi = Math.Asin(Math.Clamp(1.0 / r, -1.0, 1.0));

      double u = Math.Atan(0.99664719 * Math.Tan(phi));
      double h = altitudeMeters / 6378140.0;
      double rhoSinPhi =
        0.99664719 * Math.Sin(u) +
        h * Math.Sin(phi);
      double rhoCosPhi =
        Math.Cos(u) +
        h * Math.Cos(phi);

      double t = (jd - 2451545.0) / 36525.0;
      double gmstDeg =
        280.46061837 +
        360.98564736629 * (jd - 2451545.0) +
        0.000387933 * t * t -
        t * t * t / 38710000.0;
      double lst = NormalizeTwoPi(ToRad(gmstDeg) + longitude);

      double hourAngle = NormalizePi(lst - ra);
      double deltaRa = Math.Atan2(
        -rhoCosPhi * Math.Sin(pi) * Math.Sin(hourAngle),
        Math.Cos(dec) -
        rhoCosPhi * Math.Sin(pi) * Math.Cos(hourAngle));

      double raTop = ra + deltaRa;
      double decTop = Math.Atan2(
        (Math.Sin(dec) - rhoSinPhi * Math.Sin(pi)) *
          Math.Cos(deltaRa),
        Math.Cos(dec) -
          rhoCosPhi * Math.Sin(pi) * Math.Cos(hourAngle));

      double hTop = NormalizePi(lst - raTop);
      double sinAlt =
        Math.Sin(phi) * Math.Sin(decTop) +
        Math.Cos(phi) * Math.Cos(decTop) * Math.Cos(hTop);
      double alt = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

      double az = Math.Atan2(
        -Math.Sin(hTop),
        Math.Tan(decTop) * Math.Cos(phi) -
        Math.Sin(phi) * Math.Cos(hTop));

      return new Bearing(
        NormalizeTwoPi(az),
        alt);
    }

    private static List<Point> ParseCsv(IEnumerable<string> lines)
    {
      var result = new List<Point>();
      int timeColumn = 0;
      int azColumn = 1;
      int elColumn = 2;
      bool headerResolved = false;

      foreach (string raw in lines)
      {
        string line = raw.Trim();
        if (line.Length == 0 ||
            line.StartsWith("#") ||
            line.StartsWith("$$SOE") ||
            line.StartsWith("$$EOE"))
          continue;

        char delimiter =
          line.Count(c => c == ',') >= 2 ? ',' :
          line.Count(c => c == ';') >= 2 ? ';' :
          '\t';

        string[] fields = line
          .Split(delimiter)
          .Select(f => f.Trim().Trim('"'))
          .ToArray();

        if (fields.Length < 3) continue;

        if (!headerResolved)
        {
          int date = FindColumn(fields, "date", "utc", "time", "calendar");
          int az = FindColumn(fields, "azi", "azimuth");
          int el = FindColumn(fields, "elev", "elevation", "alt");

          if (date >= 0 && az >= 0 && el >= 0)
          {
            timeColumn = date;
            azColumn = az;
            elColumn = el;
            headerResolved = true;
            continue;
          }

          headerResolved = true;
        }

        int maxColumn = Math.Max(timeColumn, Math.Max(azColumn, elColumn));
        if (fields.Length <= maxColumn) continue;

        if (!TryParseUtc(fields[timeColumn], out DateTime utc) ||
            !double.TryParse(
              fields[azColumn],
              NumberStyles.Float,
              CultureInfo.InvariantCulture,
              out double azimuth) ||
            !double.TryParse(
              fields[elColumn],
              NumberStyles.Float,
              CultureInfo.InvariantCulture,
              out double elevation))
          continue;

        if (elevation < -90 || elevation > 90) continue;

        result.Add(
          new Point(
            utc,
            new Bearing(
              ToRad(NormalizeDeg(azimuth)),
              ToRad(elevation))));
      }

      return result;
    }

    private static int FindColumn(string[] fields, params string[] names)
    {
      for (int i = 0; i < fields.Length; i++)
      {
        string normalized = fields[i]
          .Replace("_", string.Empty)
          .Replace("-", string.Empty)
          .Replace("(", string.Empty)
          .Replace(")", string.Empty)
          .ToLowerInvariant();

        if (names.Any(name => normalized.Contains(name)))
          return i;
      }

      return -1;
    }

    private static bool TryParseUtc(string value, out DateTime utc)
    {
      value = value.Trim();

      string[] exactFormats =
      {
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
        "yyyy-MMM-dd HH:mm:ss",
        "yyyy-MMM-dd HH:mm"
      };

      if (DateTime.TryParseExact(
            value,
            exactFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out utc))
        return true;

      if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out utc))
        return true;

      utc = default;
      return false;
    }

    private static double ToRad(double deg) =>
      deg * Math.PI / 180.0;

    private static double NormalizeDeg(double deg)
    {
      deg %= 360.0;
      return deg < 0 ? deg + 360.0 : deg;
    }

    private static double NormalizeTwoPi(double value)
    {
      value %= 2 * Math.PI;
      return value < 0 ? value + 2 * Math.PI : value;
    }

    private static double NormalizePi(double value)
    {
      value = NormalizeTwoPi(value);
      return value > Math.PI ? value - 2 * Math.PI : value;
    }
  }
}
