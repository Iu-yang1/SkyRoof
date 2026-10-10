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
      root.RowStyles[3].SizeType.Should().Be(SizeType.AutoSize);
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

    [Theory]
    [InlineData(100.0, 400.0)]
    [InlineData(1050.0, 200.0)]
    [InlineData(2000.0, 0.0)]
    public void VerticalFrequencyRuler_HighAFAboveLowAF(
      double hz, double expectedPixel)
    {
      float y = CwAudioWaterfallView.FrequencyToVerticalPixel(
        hz, 100, 2000, 401);
      y.Should().BeApproximately((float)expectedPixel, 0.001f);
      CwAudioWaterfallView.VerticalPixelToFrequency(
        (int)y, 100, 2000, 401)
        .Should().BeApproximately(hz, 0.001);
    }

    [Fact]
    public void HorizontalWaterfall_NewFramesArriveOnRight()
    {
      using var view = new CwAudioWaterfallView(512)
      {
        Size = new System.Drawing.Size(740, 250)
      };
      var power = Enumerable.Repeat(-100f, 512).ToArray();
      power[188] = -10f; // ~799 Hz, away from 750-Hz grid
      var frame = new CwAudioSpectrumFrame(
        DateTime.UtcNow, 0, 100, 2000, power);
      view.SetSpectrum(frame);

      // Less than the 512-column history: old time must remain dark on
      // the left while these 40 keyed frames accumulate on the right.
      for (int i = 0; i < 40; i++)
        view.Append(frame);

      using var rendered = new System.Drawing.Bitmap(
        view.Width, view.Height);
      view.DrawToBitmap(rendered, new System.Drawing.Rectangle(
        0, 0, view.Width, view.Height));

      int signalY = (int)CwAudioWaterfallView.FrequencyToVerticalPixel(
        100 + 188 * 1900.0 / 511.0, 100, 2000, view.Height);
      // Check an area rather than one pixel: GDI nearest-neighbor pixel
      // centers and font/DPI scaling can shift the drawn bin by a few px.
      int PeakBrightness(int left, int right)
      {
        int peak = 0;
        for (int x = left; x < right; x++)
          for (int y = signalY - 4; y <= signalY + 4; y++)
          {
            var color = rendered.GetPixel(x, y);
            peak = Math.Max(peak, color.R + color.G + color.B);
          }
        return peak;
      }

      int brightRight = PeakBrightness(view.Width - 52, view.Width - 5);
      int darkLeft = PeakBrightness(180, 230);
      brightRight.Should().BeGreaterThan(darkLeft + 60,
        "the newest 40 time columns must appear at the right side");
    }

    [Fact]
    public void PileupCopyButton_AutoSizesForWindowsDpi()
    {
      using var card = new CwPileupLaneCard(0);
      var layout = card.Controls.OfType<TableLayoutPanel>().Single();
      layout.ColumnStyles[1].SizeType.Should().Be(
        SizeType.AutoSize);
      var button = layout.Controls.OfType<Button>().Single();
      button.Text.Should().Be("Copy");
      button.AutoSize.Should().BeTrue();
      button.MinimumSize.Width.Should().BeGreaterThan(65);
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
