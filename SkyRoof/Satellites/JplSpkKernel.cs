using System.Buffers.Binary;
using System.Text;
using SGPdotNET.Observation;
using SGPdotNET.Util;

namespace SkyRoof
{
  internal enum JplBody
  {
    Sun = 10,
    Venus = 2,
    Moon = 301,
    Earth = 399
  }

  internal readonly record struct JplVector3(
    double X,
    double Y,
    double Z)
  {
    public static JplVector3 operator +(
      JplVector3 a,
      JplVector3 b) =>
      new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static JplVector3 operator -(
      JplVector3 a,
      JplVector3 b) =>
      new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    internal double Length =>
      Math.Sqrt(X * X + Y * Y + Z * Z);
  }

  /// <summary>
  /// Minimal managed NAIF DAF/SPK reader for JPL planetary kernels.
  ///
  /// Supported SPK segment types:
  ///   2 - Chebyshev position coefficients
  ///   3 - Chebyshev position/velocity coefficients
  ///
  /// DE421, DE440 and DE440s use these representations for the planetary
  /// ephemerides required by SkyRoof. The implementation follows the NAIF
  /// DAF/SPK file specifications and deliberately avoids a native CSPICE
  /// runtime dependency.
  /// </summary>
  internal sealed class JplSpkKernel
  {
    private const int DafRecordBytes = 1024;
    private const double SecondsPerDay = 86400.0;
    private const double J2000 = 2451545.0;

    private readonly byte[] Data;
    private readonly bool LittleEndian;
    private readonly List<Segment> Segments = new();

    internal string FileName { get; }
    internal string InternalName { get; }

    private readonly record struct Segment(
      double StartSeconds,
      double EndSeconds,
      int Target,
      int Center,
      int Frame,
      int Type,
      int FirstAddress,
      int LastAddress);

    internal JplSpkKernel(string path)
    {
      FileName = path;
      Data = File.ReadAllBytes(path);

      if (Data.Length < DafRecordBytes)
        throw new InvalidDataException(
          "The selected JPL kernel is too small to be a DAF/SPK file.");

      string id = ReadAscii(0, 8);
      if (!id.StartsWith("DAF/SPK", StringComparison.Ordinal))
        throw new InvalidDataException(
          $"Unsupported ephemeris file identifier: '{id.Trim()}'. Expected DAF/SPK.");

      string format = ReadAscii(88, 8).Trim();
      LittleEndian = format switch
      {
        "LTL-IEEE" => true,
        "BIG-IEEE" => false,
        _ => throw new InvalidDataException(
          $"Unsupported DAF binary format '{format}'.")
      };

      int nd = ReadInt32(8);
      int ni = ReadInt32(12);
      int firstSummaryRecord = ReadInt32(76);

      if (nd != 2 || ni != 6)
        throw new InvalidDataException(
          $"Unsupported SPK descriptor shape ND={nd}, NI={ni}.");

      InternalName = ReadAscii(16, 60).TrimEnd();
      ParseSummaries(
        nd,
        ni,
        firstSummaryRecord);

      if (Segments.Count == 0)
        throw new InvalidDataException(
          "The selected SPK kernel contains no ephemeris segments.");
    }

    internal bool Supports(
      JplBody body,
      DateTime utc)
    {
      try
      {
        _ = ComputePosition(
          (int)body,
          (int)JplBody.Earth,
          ToJulianDateTdb(utc));
        return true;
      }
      catch
      {
        return false;
      }
    }

    internal TopocentricObservation Observe(
      JplBody body,
      GroundStation groundStation,
      DateTime utc)
    {
      utc = EnsureUtc(utc);

      TopocentricVector current =
        ComputeTopocentric(
          body,
          groundStation,
          utc);

      // Finite-difference the complete observer-to-target distance. This
      // includes Earth rotation and is adequate for radio Doppler/status use.
      DateTime before = utc.AddSeconds(-0.5);
      DateTime after = utc.AddSeconds(0.5);

      double rangeBefore =
        ComputeTopocentric(
          body,
          groundStation,
          before).RangeKm;
      double rangeAfter =
        ComputeTopocentric(
          body,
          groundStation,
          after).RangeKm;

      double rangeRateKmPerSecond =
        rangeAfter - rangeBefore;

      return new TopocentricObservation(
        Angle.FromRadians(current.Azimuth),
        Angle.FromRadians(current.Elevation),
        current.RangeKm,
        rangeRateKmPerSecond,
        groundStation.Location);
    }

    internal JplVector3 ComputePosition(
      int target,
      int center,
      double julianDateTdb)
    {
      if (target == center)
        return new JplVector3();

      Segment? direct =
        FindSegment(
          target,
          center,
          julianDateTdb);

      if (direct.HasValue)
        return EvaluateSegment(
          direct.Value,
          julianDateTdb);

      JplVector3 targetFromSsb =
        PositionFromSsb(
          target,
          julianDateTdb,
          new HashSet<int>());

      JplVector3 centerFromSsb =
        center == 0
          ? new JplVector3()
          : PositionFromSsb(
              center,
              julianDateTdb,
              new HashSet<int>());

      return targetFromSsb - centerFromSsb;
    }

    internal static double ToJulianDateTdb(
      DateTime utc)
    {
      utc = EnsureUtc(utc);

      double jdUtc =
        2440587.5 +
        (utc - DateTime.UnixEpoch).TotalDays;

      double ttMinusUtc =
        TaiMinusUtc(utc) + 32.184;
      double jdTt =
        jdUtc +
        ttMinusUtc / SecondsPerDay;

      // Fairhead/Bretagnon first-order approximation is more than adequate
      // for antenna pointing: TDB-TT is only ~1.7 ms peak.
      double g =
        DegreesToRadians(
          NormalizeDegrees(
            357.53 +
            0.9856003 * (jdTt - J2000)));

      double tdbMinusTtSeconds =
        0.001657 * Math.Sin(g) +
        0.000022 * Math.Sin(2 * g);

      return
        jdTt +
        tdbMinusTtSeconds / SecondsPerDay;
    }

    private readonly record struct TopocentricVector(
      double Azimuth,
      double Elevation,
      double RangeKm);

    private TopocentricVector ComputeTopocentric(
      JplBody body,
      GroundStation groundStation,
      DateTime utc)
    {
      double jdTdb = ToJulianDateTdb(utc);

      JplVector3 targetEci =
        ComputePosition(
          (int)body,
          (int)JplBody.Earth,
          jdTdb);

      double jdUtc =
        2440587.5 +
        (utc - DateTime.UnixEpoch).TotalDays;

      double theta =
        GreenwichMeanSiderealTime(jdUtc);

      double cosTheta = Math.Cos(theta);
      double sinTheta = Math.Sin(theta);

      // J2000/ICRF equatorial inertial -> Earth-fixed.
      double x =
        cosTheta * targetEci.X +
        sinTheta * targetEci.Y;
      double y =
        -sinTheta * targetEci.X +
        cosTheta * targetEci.Y;
      double z = targetEci.Z;

      double lat = groundStation.ObserverLatRad;
      double lon = groundStation.ObserverLonRad;
      double altitudeKm = groundStation.ObserverAltKm;

      const double equatorialRadiusKm = 6378.137;
      const double eccentricitySquared =
        6.69437999014e-3;

      double sinLat = Math.Sin(lat);
      double cosLat = Math.Cos(lat);
      double sinLon = Math.Sin(lon);
      double cosLon = Math.Cos(lon);

      double n =
        equatorialRadiusKm /
        Math.Sqrt(
          1 -
          eccentricitySquared *
          sinLat *
          sinLat);

      double observerX =
        (n + altitudeKm) *
        cosLat *
        cosLon;
      double observerY =
        (n + altitudeKm) *
        cosLat *
        sinLon;
      double observerZ =
        (n *
         (1 - eccentricitySquared) +
         altitudeKm) *
        sinLat;

      double dx = x - observerX;
      double dy = y - observerY;
      double dz = z - observerZ;

      double east =
        -sinLon * dx +
        cosLon * dy;
      double north =
        -sinLat * cosLon * dx -
        sinLat * sinLon * dy +
        cosLat * dz;
      double up =
        cosLat * cosLon * dx +
        cosLat * sinLon * dy +
        sinLat * dz;

      double horizontal =
        Math.Sqrt(
          east * east +
          north * north);

      double azimuth =
        Math.Atan2(
          east,
          north);
      if (azimuth < 0)
        azimuth +=
          2 * Math.PI;

      double elevation =
        Math.Atan2(
          up,
          horizontal);

      double range =
        Math.Sqrt(
          dx * dx +
          dy * dy +
          dz * dz);

      return new TopocentricVector(
        azimuth,
        elevation,
        range);
    }

    private JplVector3 PositionFromSsb(
      int target,
      double julianDateTdb,
      HashSet<int> visited)
    {
      if (target == 0)
        return new JplVector3();

      if (!visited.Add(target))
        throw new InvalidDataException(
          $"SPK center chain contains a cycle at NAIF body {target}.");

      Segment? direct =
        FindSegment(
          target,
          0,
          julianDateTdb);

      if (direct.HasValue)
        return EvaluateSegment(
          direct.Value,
          julianDateTdb);

      double seconds =
        (julianDateTdb - J2000) *
        SecondsPerDay;

      foreach (Segment segment in Segments)
      {
        if (segment.Target != target ||
            seconds < segment.StartSeconds ||
            seconds > segment.EndSeconds)
          continue;

        JplVector3 relative =
          EvaluateSegment(
            segment,
            julianDateTdb);
        JplVector3 center =
          PositionFromSsb(
            segment.Center,
            julianDateTdb,
            visited);

        return center + relative;
      }

      throw new ArgumentOutOfRangeException(
        nameof(julianDateTdb),
        $"No SPK segment chain is available for NAIF body {target} at JD {julianDateTdb:F6}.");
    }

    private Segment? FindSegment(
      int target,
      int center,
      double julianDateTdb)
    {
      double seconds =
        (julianDateTdb - J2000) *
        SecondsPerDay;

      // SPICE precedence is effectively last-loaded/last-segment wins. Within
      // one DE kernel, iterate backwards for the same deterministic behavior.
      for (int i = Segments.Count - 1; i >= 0; i--)
      {
        Segment segment = Segments[i];

        if (segment.Target == target &&
            segment.Center == center &&
            seconds >= segment.StartSeconds &&
            seconds <= segment.EndSeconds)
          return segment;
      }

      return null;
    }

    private JplVector3 EvaluateSegment(
      Segment segment,
      double julianDateTdb)
    {
      if (segment.Type != 2 &&
          segment.Type != 3)
        throw new NotSupportedException(
          $"SPK type {segment.Type} is not supported. DE421/DE440 planetary position segments are expected to use type 2 or 3.");

      int dataStart =
        checked(
          (segment.FirstAddress - 1) * 8);
      int dataEnd =
        checked(
          segment.LastAddress * 8);

      if (dataStart < 0 ||
          dataEnd > Data.Length ||
          dataEnd - dataStart < 32)
        throw new InvalidDataException(
          "SPK segment addresses are outside the file.");

      int metaOffset = dataEnd - 32;
      double init =
        ReadDouble(metaOffset);
      double interval =
        ReadDouble(metaOffset + 8);
      int recordSize =
        checked(
          (int)Math.Round(
            ReadDouble(metaOffset + 16)));
      int recordCount =
        checked(
          (int)Math.Round(
            ReadDouble(metaOffset + 24)));

      int components =
        segment.Type == 2 ? 3 : 6;

      if (recordSize < 2 + components ||
          recordCount <= 0 ||
          (recordSize - 2) % components != 0)
        throw new InvalidDataException(
          "Invalid Chebyshev SPK segment metadata.");

      double seconds =
        (julianDateTdb - J2000) *
        SecondsPerDay;

      int index =
        (int)Math.Floor(
          (seconds - init) / interval);

      index =
        Math.Clamp(
          index,
          0,
          recordCount - 1);

      int offset =
        checked(
          dataStart +
          index *
          recordSize *
          8);

      double mid =
        ReadDouble(offset);
      double radius =
        ReadDouble(offset + 8);

      if (radius == 0)
        throw new InvalidDataException(
          "SPK Chebyshev record has zero radius.");

      double normalized =
        (seconds - mid) /
        radius;

      int coefficientCount =
        (recordSize - 2) /
        components;

      double x =
        EvaluateChebyshev(
          offset + 16,
          coefficientCount,
          normalized);
      double y =
        EvaluateChebyshev(
          offset + 16 +
          coefficientCount * 8,
          coefficientCount,
          normalized);
      double z =
        EvaluateChebyshev(
          offset + 16 +
          coefficientCount * 16,
          coefficientCount,
          normalized);

      return new JplVector3(
        x,
        y,
        z);
    }

    private double EvaluateChebyshev(
      int offset,
      int count,
      double x)
    {
      if (count <= 0)
        return 0;

      if (count == 1)
        return ReadDouble(offset);

      double b1 = 0;
      double b2 = 0;

      for (int i = count - 1; i >= 1; i--)
      {
        double coefficient =
          ReadDouble(
            offset +
            i * 8);

        double b =
          coefficient +
          2 * x * b1 -
          b2;

        b2 = b1;
        b1 = b;
      }

      return
        ReadDouble(offset) +
        x * b1 -
        b2;
    }

    private void ParseSummaries(
      int nd,
      int ni,
      int firstRecord)
    {
      int summaryDoubleCount =
        nd +
        (ni + 1) / 2;
      int summaryBytes =
        summaryDoubleCount * 8;

      int record = firstRecord;
      var visited = new HashSet<int>();

      while (record > 0)
      {
        if (!visited.Add(record))
          throw new InvalidDataException(
            "DAF summary-record chain contains a cycle.");

        int offset =
          checked(
            (record - 1) *
            DafRecordBytes);

        if (offset < 0 ||
            offset + DafRecordBytes > Data.Length)
          throw new InvalidDataException(
            "DAF summary record lies outside the file.");

        int next =
          checked(
            (int)Math.Round(
              ReadDouble(offset)));
        int count =
          checked(
            (int)Math.Round(
              ReadDouble(offset + 16)));

        int maxSummaries =
          (DafRecordBytes - 24) /
          summaryBytes;
        if (count < 0 ||
            count > maxSummaries)
          throw new InvalidDataException(
            "DAF summary count is invalid.");

        for (int i = 0; i < count; i++)
        {
          int summaryOffset =
            offset +
            24 +
            i *
            summaryBytes;

          double start =
            ReadDouble(summaryOffset);
          double end =
            ReadDouble(summaryOffset + 8);

          int integerOffset =
            summaryOffset +
            nd * 8;

          int target =
            ReadInt32(integerOffset);
          int center =
            ReadInt32(integerOffset + 4);
          int frame =
            ReadInt32(integerOffset + 8);
          int type =
            ReadInt32(integerOffset + 12);
          int firstAddress =
            ReadInt32(integerOffset + 16);
          int lastAddress =
            ReadInt32(integerOffset + 20);

          Segments.Add(
            new Segment(
              start,
              end,
              target,
              center,
              frame,
              type,
              firstAddress,
              lastAddress));
        }

        record = next;
      }
    }

    private int ReadInt32(int offset)
    {
      ReadOnlySpan<byte> span =
        Data.AsSpan(
          offset,
          4);

      return LittleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(span)
        : BinaryPrimitives.ReadInt32BigEndian(span);
    }

    private double ReadDouble(int offset)
    {
      long bits =
        LittleEndian
          ? BinaryPrimitives.ReadInt64LittleEndian(
              Data.AsSpan(offset, 8))
          : BinaryPrimitives.ReadInt64BigEndian(
              Data.AsSpan(offset, 8));

      return BitConverter.Int64BitsToDouble(bits);
    }

    private string ReadAscii(
      int offset,
      int count) =>
      Encoding.ASCII.GetString(
        Data,
        offset,
        count);

    private static DateTime EnsureUtc(
      DateTime value) =>
      value.Kind == DateTimeKind.Utc
        ? value
        : value.ToUniversalTime();

    private static double GreenwichMeanSiderealTime(
      double jdUtc)
    {
      double t =
        (jdUtc - J2000) /
        36525.0;

      double degrees =
        280.46061837 +
        360.98564736629 *
        (jdUtc - J2000) +
        0.000387933 *
        t *
        t -
        t *
        t *
        t /
        38710000.0;

      return
        DegreesToRadians(
          NormalizeDegrees(degrees));
    }

    private static double NormalizeDegrees(
      double degrees)
    {
      degrees %= 360.0;
      return degrees < 0
        ? degrees + 360.0
        : degrees;
    }

    private static double DegreesToRadians(
      double degrees) =>
      degrees *
      Math.PI /
      180.0;

    private static int TaiMinusUtc(DateTime utc)
    {
      // UTC leap-second history. Pre-1972 civil-time reconstruction is not
      // required for the modern radio-tracking use case; use the first TAI-UTC
      // value there rather than pretending historical UTC was fully defined.
      (DateTime Effective, int Value)[] entries =
      {
        (new DateTime(1972, 1, 1, 0, 0, 0, DateTimeKind.Utc), 10),
        (new DateTime(1972, 7, 1, 0, 0, 0, DateTimeKind.Utc), 11),
        (new DateTime(1973, 1, 1, 0, 0, 0, DateTimeKind.Utc), 12),
        (new DateTime(1974, 1, 1, 0, 0, 0, DateTimeKind.Utc), 13),
        (new DateTime(1975, 1, 1, 0, 0, 0, DateTimeKind.Utc), 14),
        (new DateTime(1976, 1, 1, 0, 0, 0, DateTimeKind.Utc), 15),
        (new DateTime(1977, 1, 1, 0, 0, 0, DateTimeKind.Utc), 16),
        (new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc), 17),
        (new DateTime(1979, 1, 1, 0, 0, 0, DateTimeKind.Utc), 18),
        (new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc), 19),
        (new DateTime(1981, 7, 1, 0, 0, 0, DateTimeKind.Utc), 20),
        (new DateTime(1982, 7, 1, 0, 0, 0, DateTimeKind.Utc), 21),
        (new DateTime(1983, 7, 1, 0, 0, 0, DateTimeKind.Utc), 22),
        (new DateTime(1985, 7, 1, 0, 0, 0, DateTimeKind.Utc), 23),
        (new DateTime(1988, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24),
        (new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc), 25),
        (new DateTime(1991, 1, 1, 0, 0, 0, DateTimeKind.Utc), 26),
        (new DateTime(1992, 7, 1, 0, 0, 0, DateTimeKind.Utc), 27),
        (new DateTime(1993, 7, 1, 0, 0, 0, DateTimeKind.Utc), 28),
        (new DateTime(1994, 7, 1, 0, 0, 0, DateTimeKind.Utc), 29),
        (new DateTime(1996, 1, 1, 0, 0, 0, DateTimeKind.Utc), 30),
        (new DateTime(1997, 7, 1, 0, 0, 0, DateTimeKind.Utc), 31),
        (new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), 32),
        (new DateTime(2006, 1, 1, 0, 0, 0, DateTimeKind.Utc), 33),
        (new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc), 34),
        (new DateTime(2012, 7, 1, 0, 0, 0, DateTimeKind.Utc), 35),
        (new DateTime(2015, 7, 1, 0, 0, 0, DateTimeKind.Utc), 36),
        (new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), 37)
      };

      int value = 10;
      foreach (var entry in entries)
      {
        if (utc < entry.Effective)
          break;
        value = entry.Value;
      }

      return value;
    }
  }
}
