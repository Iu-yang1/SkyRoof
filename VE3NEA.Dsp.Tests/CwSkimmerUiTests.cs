using FluentAssertions;
using SkyRoof;
using SkyRoof.CW;
using System.Windows.Forms;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwSkimmerUiTests
  {
    [Fact]
    public void RootLayout_IsToolbarStatusSplitAndFixedTransmitBar()
    {
      using TableLayoutPanel root =
        CwConsolePanel.CreateSkimmerRootLayout();

      root.RowCount.Should().Be(4);
      root.RowStyles.Count.Should().Be(4);
      root.RowStyles[2].SizeType.Should().Be(SizeType.Percent);
      root.RowStyles[3].SizeType.Should().Be(SizeType.Absolute);
      root.RowStyles[3].Height.Should().BeGreaterThan(125);
    }

    [Fact]
    public void WaterfallPanAndZoom_ChangesOnlyViewport()
    {
      using var view = new CwAudioWaterfallView(512);
      view.SetViewport(0, 2);
      view.VisibleMinimumHz.Should().BeApproximately(100, 0.001);
      view.VisibleMaximumHz.Should().BeApproximately(1050, 0.001);

      view.SetViewport(1, 2);
      view.VisibleMinimumHz.Should().BeApproximately(1050, 0.001);
      view.VisibleMaximumHz.Should().BeApproximately(2000, 0.001);

      view.SetViewport(0.5, 8);
      (view.VisibleMaximumHz - view.VisibleMinimumHz)
        .Should().BeApproximately(237.5, 0.001);

      view.SetViewport(0.9, 1);
      view.ViewportStartFraction.Should().Be(0);
      view.VisibleMinimumHz.Should().Be(100);
      view.VisibleMaximumHz.Should().Be(2000);
    }

    [Fact]
    public void StableMessageCards_KeepTheirSlotDuringFrequencyCrossing()
    {
      var map = new CwConsoleLaneSlotMap(
        maxSlots: 8, releaseDelay: TimeSpan.FromSeconds(4));
      DateTime now = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

      CwSignalTrack a = Track(1, 101, 700);
      CwSignalTrack b = Track(2, 202, 900);
      var first = map.Update(new[] { a, b }, now);
      int slotA = first.Single(x => x.Identity.HasValue &&
        x.Identity.Value.Id == 101).Index;

      var after = map.Update(new[]
      {
        a with { FrequencyHz = 1200 },
        b with { FrequencyHz = 550 }
      }, now.AddSeconds(1));

      after.Single(x => x.Identity.HasValue &&
        x.Identity.Value.Id == 101).Index.Should().Be(slotA);
    }

    private static CwSignalTrack Track(
      int id, int hint, double hz)
    {
      DateTime now = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
      return new CwSignalTrack(
        Id: id, FrequencyHz: hz, SnrDb: 10,
        DriftHzPerSecond: 0,
        FirstSeenUtc: now, LastSeenUtc: now,
        Confirmed: true, Active: true, Ambiguous: false,
        FrequencySigmaHz: 2, MergeGroupId: 0,
        IdentityConfidence: 0.9,
        AssociationHintId: hint);
    }
  }
}
