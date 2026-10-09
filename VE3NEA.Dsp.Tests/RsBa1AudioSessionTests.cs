using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RsBa1AudioSessionTests
{
  [Theory]
  [InlineData("RemoteUtility", "Remote Utility", "ICOM Incorporated", true)]
  [InlineData("RSBA1RemoteUtility", "", "", true)]
  [InlineData("RemoteControl", "RS-BA1 Remote Control", "ICOM", false)]
  [InlineData("spotify", "Spotify", "", false)]
  [InlineData("RemoteUtility", "Other", "", false)]
  public void AutomaticBindingIsSpecificToIcomRemoteUtility(
    string process, string title, string company, bool expected)
  {
    Assert.Equal(expected,
      RsBa1AudioSessionController.IsLikelyRemoteUtility(
        process, title, company));
  }

  [Fact]
  public void AmbiguousAutoCandidatesMustNeverBeModified()
  {
    var sessions = new[] {
      NewSession(11, "RemoteUtility", "render1", candidate:true),
      NewSession(12, "RemoteUtility", "render2", candidate:true)
    };
    Assert.Null(RsBa1AudioSessionController.Resolve(sessions, null, null));
    Assert.Same(sessions[0],
      RsBa1AudioSessionController.Resolve(
        sessions, "RemoteUtility", "render1"));
    Assert.Null(RsBa1AudioSessionController.Resolve(
      sessions, "notepad", "render1"));
  }

  [Fact]
  public void ExplicitSelectionCanChooseDifferentNamedAudioProcess()
  {
    var sessions = new[] {
      NewSession(44, "IcomAudioBridge", "headset", candidate:false),
      NewSession(55, "Browser", "headset", candidate:false)
    };
    Assert.Null(RsBa1AudioSessionController.Resolve(sessions, null, null));
    Assert.Same(sessions[0],
      RsBa1AudioSessionController.Resolve(
        sessions, "IcomAudioBridge", "headset"));
  }

  [Theory]
  [InlineData(0, 1f)]
  [InlineData(-20, 0.1f)]
  [InlineData(-40, 0.01f)]
  public void MixerSliderUsesDecibelToAmplitudeMapping(
    int db, float expected)
  {
    float value = RsBa1AudioSessionController.GainDbToScalar(db);
    Assert.InRange(value, expected - 0.00001f, expected + 0.00001f);
    Assert.Equal(db,
      RsBa1AudioSessionController.ScalarToGainDb(value));
  }

  private static RsBa1AudioSessionController.AudioSessionInfo NewSession(
    int pid, string name, string device, bool candidate) =>
    new(pid, name, "", device, device, 0.5f, candidate);
}
