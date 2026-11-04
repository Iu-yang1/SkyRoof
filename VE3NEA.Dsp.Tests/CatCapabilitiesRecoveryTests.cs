using System;
using System.IO;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CatCapabilitiesRecoveryTests
  {
    [Fact]
    public void InvalidUserCapabilitiesFileFallsBackInsteadOfThrowing()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "skyroof-cat-" + Guid.NewGuid().ToString("N") + ".json");
      try
      {
        File.WriteAllText(path, "{not-valid-json");
        Assert.Null(RadioCapabilities.LoadFromFile(path));
      }
      finally
      {
        if (File.Exists(path)) File.Delete(path);
      }
    }

    [Fact]
    public void MissingCapabilitiesFileIsNotTreatedAsCatFailure()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "skyroof-absent-" + Guid.NewGuid().ToString("N") + ".json");
      Assert.Null(RadioCapabilities.LoadFromFile(path));
    }

    [Fact]
    public void UnknownNewerCatActionsAreIgnoredWithoutLosingKnownOnes()
    {
      const string json = """
        {"model":"test","split":{"when_receiving":["write_rx_frequency",
        "feature_from_future_version"],"when_transmitting":["write_tx_frequency"]}}
        """;
      RadioCapabilities? capabilities = RadioCapabilities.LoadFromJson(json);
      Assert.NotNull(capabilities);
      Assert.True(capabilities.CanSplitTune(false));
    }
  }
}
