using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class JplEphemerisTests
  {
    [Fact]
    public void JplSpkKernel_ComposesMoonRelativeToEarthThroughEmb()
    {
      string path =
        Path.Combine(
          Path.GetTempPath(),
          $"skyroof-jpl-{Guid.NewGuid():N}.bsp");

      try
      {
        File.WriteAllBytes(
          path,
          BuildSyntheticKernel());

        var kernel =
          new JplSpkKernel(path);

        JplVector3 moonFromEarth =
          kernel.ComputePosition(
            (int)JplBody.Moon,
            (int)JplBody.Earth,
            2451545.0);

        moonFromEarth.X.Should().BeApproximately(399_990, 1e-6);
        moonFromEarth.Y.Should().BeApproximately(480, 1e-6);
        moonFromEarth.Z.Should().BeApproximately(570, 1e-6);

        kernel.Supports(
          JplBody.Moon,
          new DateTime(
            2000,
            1,
            1,
            12,
            0,
            0,
            DateTimeKind.Utc))
          .Should().BeTrue();
      }
      finally
      {
        try { File.Delete(path); } catch { }
      }
    }

    [Fact]
    public void JplSpkKernel_RejectsNonSpkFiles()
    {
      string path =
        Path.Combine(
          Path.GetTempPath(),
          $"skyroof-not-spk-{Guid.NewGuid():N}.bsp");

      try
      {
        File.WriteAllBytes(
          path,
          new byte[1024]);

        Action action =
          () => _ = new JplSpkKernel(path);

        action.Should()
          .Throw<InvalidDataException>();
      }
      finally
      {
        try { File.Delete(path); } catch { }
      }
    }

    [Fact]
    public void CustomTleParser_AcceptsTwoLineAndThreeLineRecords()
    {
      const string line1 =
        "1 25544U 98067A   24001.50000000  .00000000  00000-0  00000-0 0  9999";
      const string line2 =
        "2 25544  51.6400 100.0000 0005000 200.0000 160.0000 15.50000000123456";

      var twoLine =
        SatnogsDb.TlesFromText(
          $"{line1}\n{line2}",
          "Test");

      var threeLine =
        SatnogsDb.TlesFromText(
          $"ISS\n{line1}\n{line2}",
          "Test");

      twoLine.Should().HaveCount(1);
      threeLine.Should().HaveCount(1);
      twoLine[0].norad_cat_id.Should().Be(25544);
      threeLine[0].tle0.Should().Be("ISS");
      threeLine[0].tle_source.Should().Be("Test");
    }

    [Fact]
    public void CustomTleSources_AcceptSemicolonAndNewlineLists()
    {
      string[] sources =
        SatnogsDb.SplitCustomTleSources(
          "https://example.invalid/a.txt;C:\\tle\\local.txt\nhttps://example.invalid/a.txt");

      sources.Should().Equal(
        "https://example.invalid/a.txt",
        "C:\\tle\\local.txt");
    }

    [Fact]
    public void CustomTleSources_FirstEntryHasHighestPriority()
    {
      SatnogsDb.GetCustomTleApplyOrder(
          "first.txt;second.txt;third.txt")
        .Should().Equal(
          "third.txt",
          "second.txt",
          "first.txt");
    }

    [Fact]
    public void JplDownloadProgress_ComputesPercentageWhenLengthIsKnown()
    {
      new JplDownloadProgress(
          1,
          2,
          "https://example.invalid/de440s.bsp",
          25,
          100)
        .Percent.Should().Be(25);

      new JplDownloadProgress(
          1,
          2,
          "https://example.invalid/de440s.bsp",
          25,
          null)
        .Percent.Should().BeNull();
    }

    [Fact]
    public void SolarSystemGroup_ContainsMoonSunAndVenusAfterKernelActivation()
    {
      string path =
        Path.Combine(
          Path.GetTempPath(),
          $"skyroof-jpl-solar-{Guid.NewGuid():N}.bsp");

      try
      {
        File.WriteAllBytes(
          path,
          BuildSyntheticKernel());

        var settings =
          new OrbitSourceSettings
          {
            JplKernel = JplEphemerisKernel.CustomFile,
            JplKernelFile = path,
            ShowSolarSystemTargets = true
          };
        var db = new SatnogsDb();
        db.ConfigureSources(settings);

        db.ConfigureSolarSystem(settings)
          .Should().Be(3);

        var satelliteSettings =
          new SatelliteSettings();
        satelliteSettings.EnsureSolarSystemGroup(db);

        SatelliteGroup group =
          satelliteSettings.SatelliteGroups
            .Single(
              g =>
                g.Id == "solar-system-ephemeris");

        group.SatelliteIds.Should().Equal(
          SatnogsDb.MoonSatId,
          SatnogsDb.SunSatId,
          SatnogsDb.VenusSatId);
      }
      finally
      {
        try { File.Delete(path); } catch { }
      }
    }

    [Fact]
    public void OrbitSourceSettings_ExposePrimaryUrlsAndJplFallbacks()
    {
      var settings =
        new OrbitSourceSettings();

      settings.SatellitesUrl.Should()
        .Be(OrbitSourceSettings.DefaultSatellitesUrl);
      settings.TransmittersUrl.Should()
        .Be(OrbitSourceSettings.DefaultTransmittersUrl);
      settings.TleUrl.Should()
        .Be(OrbitSourceSettings.DefaultTleUrl);

      SatnogsDb.SplitSourceList(settings.De440sSources)
        .Should().HaveCount(2)
        .And.Contain(
          "https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/planets/de440s.bsp")
        .And.Contain(
          "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de440s.bsp");

      SatnogsDb.SplitSourceList(settings.De421Sources)
        .Should().HaveCount(2)
        .And.Contain(
          "https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/planets/a_old_versions/de421.bsp")
        .And.Contain(
          "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de421.bsp");
    }

    private static byte[] BuildSyntheticKernel()
    {
      byte[] data = new byte[3 * 1024];
      WriteAscii(data, 0, 8, "DAF/SPK ");
      WriteInt32(data, 8, 2);
      WriteInt32(data, 12, 6);
      WriteAscii(data, 16, 60, "SkyRoof synthetic JPL SPK");
      WriteInt32(data, 76, 2);
      WriteInt32(data, 80, 2);
      WriteAscii(data, 88, 8, "LTL-IEEE");

      int summaryRecord = 1024;
      WriteDouble(data, summaryRecord, 0);
      WriteDouble(data, summaryRecord + 8, 0);
      WriteDouble(data, summaryRecord + 16, 5);

      int firstAddress = 257;

      WriteSummary(
        data,
        summaryRecord + 24,
        target: 3,
        center: 0,
        firstAddress,
        firstAddress + 8);

      WriteSummary(
        data,
        summaryRecord + 24 + 40,
        target: 399,
        center: 3,
        firstAddress + 9,
        firstAddress + 17);

      WriteSummary(
        data,
        summaryRecord + 24 + 80,
        target: 301,
        center: 3,
        firstAddress + 18,
        firstAddress + 26);

      WriteSummary(
        data,
        summaryRecord + 24 + 120,
        target: 10,
        center: 0,
        firstAddress + 27,
        firstAddress + 35);

      WriteSummary(
        data,
        summaryRecord + 24 + 160,
        target: 2,
        center: 0,
        firstAddress + 36,
        firstAddress + 44);

      int offset = 2048;
      WriteType2ConstantRecord(
        data,
        offset,
        1000,
        2000,
        3000);

      WriteType2ConstantRecord(
        data,
        offset + 9 * 8,
        10,
        20,
        30);

      WriteType2ConstantRecord(
        data,
        offset + 18 * 8,
        400000,
        500,
        600);

      WriteType2ConstantRecord(
        data,
        offset + 27 * 8,
        149_600_000,
        0,
        0);

      WriteType2ConstantRecord(
        data,
        offset + 36 * 8,
        108_200_000,
        1_000,
        2_000);

      return data;
    }

    private static void WriteSummary(
      byte[] data,
      int offset,
      int target,
      int center,
      int firstAddress,
      int lastAddress)
    {
      WriteDouble(data, offset, -2_000_000_000);
      WriteDouble(data, offset + 8, 2_000_000_000);
      WriteInt32(data, offset + 16, target);
      WriteInt32(data, offset + 20, center);
      WriteInt32(data, offset + 24, 1);
      WriteInt32(data, offset + 28, 2);
      WriteInt32(data, offset + 32, firstAddress);
      WriteInt32(data, offset + 36, lastAddress);
    }

    private static void WriteType2ConstantRecord(
      byte[] data,
      int offset,
      double x,
      double y,
      double z)
    {
      // RSIZE=5 -> [MID,RADIUS,X,Y,Z], one coefficient per component.
      WriteDouble(data, offset, 0);
      WriteDouble(data, offset + 8, 2_000_000_000);
      WriteDouble(data, offset + 16, x);
      WriteDouble(data, offset + 24, y);
      WriteDouble(data, offset + 32, z);

      // Segment tail: INIT, INTLEN, RSIZE, N. Use a broad synthetic
      // interval so ConfigureSolarSystem can validate the current UTC date.
      WriteDouble(data, offset + 40, -2_000_000_000);
      WriteDouble(data, offset + 48, 4_000_000_000);
      WriteDouble(data, offset + 56, 5);
      WriteDouble(data, offset + 64, 1);
    }

    private static void WriteInt32(
      byte[] data,
      int offset,
      int value) =>
      BinaryPrimitives.WriteInt32LittleEndian(
        data.AsSpan(offset, 4),
        value);

    private static void WriteDouble(
      byte[] data,
      int offset,
      double value) =>
      BinaryPrimitives.WriteInt64LittleEndian(
        data.AsSpan(offset, 8),
        BitConverter.DoubleToInt64Bits(value));

    private static void WriteAscii(
      byte[] data,
      int offset,
      int count,
      string text)
    {
      data.AsSpan(offset, count).Fill((byte)' ');
      Encoding.ASCII
        .GetBytes(text)
        .AsSpan(0, Math.Min(text.Length, count))
        .CopyTo(data.AsSpan(offset, count));
    }
  }
}
