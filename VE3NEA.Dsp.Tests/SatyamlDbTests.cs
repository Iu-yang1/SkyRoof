using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class SatyamlDbTests
  {
    [Fact]
    public void Find_SelectsNearestBaudWithinTolerance()
    {
      string dir = CreateDb(
        """
        norad: 12345
        name: TESTSAT
        transmitters:
          fast:
            baudrate: 9600
            deviation: 5000
          slow:
            baudrate: 1200
            deviation: 600
        """);

      try
      {
        SatyamlDb db = SatyamlDb.Load(dir)!;

        GrSatsInfo? match = db.Find(12345, 9500);

        match.Should().NotBeNull();
        match!.baudrate.Should().Be(9600);
        match.deviation.Should().Be(5000);
      }
      finally
      {
        Directory.Delete(dir, true);
      }
    }

    [Fact]
    public void Find_RejectsClearlyDifferentBaud()
    {
      string dir = CreateDb(
        """
        norad: 12345
        transmitters:
          fast:
            baudrate: 9600
          slow:
            baudrate: 1200
        """);

      try
      {
        SatyamlDb.Load(dir)!.Find(12345, 9000).Should().BeNull();
      }
      finally
      {
        Directory.Delete(dir, true);
      }
    }

    [Fact]
    public void Find_UsesSoleTransmitterWhenSatnogsBaudIsMissing()
    {
      string dir = CreateDb(
        """
        norad: 12345
        transmitters:
          only:
            baudrate: 9600
            precoding: differential
        """);

      try
      {
        GrSatsInfo? match = SatyamlDb.Load(dir)!.Find(12345, null);
        match.Should().NotBeNull();
        match!.precoding.Should().Be("differential");
      }
      finally
      {
        Directory.Delete(dir, true);
      }
    }

    private static string CreateDb(string yaml)
    {
      string dir = Path.Combine(Path.GetTempPath(), $"skyroof-satyaml-{Guid.NewGuid():N}");
      Directory.CreateDirectory(dir);
      File.WriteAllText(Path.Combine(dir, "test.yml"), yaml);
      return dir;
    }
  }
}
