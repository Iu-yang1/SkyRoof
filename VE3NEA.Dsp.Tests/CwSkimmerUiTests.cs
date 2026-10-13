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
    public void RootLayout_IsToolbarStatusSplitSelectedRxAndFixedTransmitBar()
    {
      using TableLayoutPanel root =
        CwConsolePanel.CreateSkimmerRootLayout();

      root.RowCount.Should().Be(5);
      root.RowStyles.Count.Should().Be(5);
      root.RowStyles[2].SizeType.Should().Be(SizeType.Percent);
      root.RowStyles[3].SizeType.Should().Be(SizeType.AutoSize);
      root.RowStyles[4].SizeType.Should().Be(SizeType.AutoSize);
    }

    [Fact]
    public void SelectedRxPanel_UsesTxStyleWithWrappedReceiveOnlyText()
    {
      using var view = new CwSelectedLaneView();

      view.Should().BeAssignableTo<GroupBox>();
      view.Text.Should().Contain("CW RX");
      view.TranscriptControl.ReadOnly.Should().BeTrue();
      view.TranscriptControl.Multiline.Should().BeTrue();
      view.TranscriptControl.WordWrap.Should().BeTrue();
      view.TranscriptControl.ScrollBars.Should()
        .Be(RichTextBoxScrollBars.Vertical);
      view.CopyButton.Enabled.Should().BeFalse();
      view.StatusText.Should().Contain("select a Pileup lane");
    }

    [Fact]
    public void SelectedRxPanel_FollowsStableLaneIdentityAndFullTranscript()
    {
      using var view = new CwSelectedLaneView();
      CwSignalTrack a = Track(1, 123, 847);
      CwSignalTrack b = Track(2, 456, 1452);
      var transcriptA = Transcript(1, 123,
        "CQ CQ DE BG5JSU BG5JSU TEST LONG MESSAGE ",
        "PLEASE COPY AGN");
      var transcriptB = Transcript(2, 456, "DE K1ABC", " 599");

      view.UpdateLane(
        new CwConsoleLaneSlot(2,
          CwConsolePresentation.Identity(a), a, true),
        transcriptA);

      view.StatusText.Should().Contain("Slot 3/8");
      view.StatusText.Should().Contain("H123");
      view.StatusText.Should().Contain("847 Hz");
      view.CopyText.Should().Be(transcriptA.Text);
      view.DisplayedText.Should().Be(transcriptA.Text);
      view.CopyButton.Enabled.Should().BeTrue();
      // No split or truncation despite the compact bottom RX panel.
      view.DisplayedText.Length.Should().BeGreaterThan(30);

      view.UpdateLane(
        new CwConsoleLaneSlot(5,
          CwConsolePresentation.Identity(b), b, false),
        transcriptB);
      view.StatusText.Should().Contain("Slot 6/8");
      view.StatusText.Should().Contain("H456");
      view.StatusText.Should().Contain("Grace");
      view.CopyText.Should().Be(transcriptB.Text);
      view.DisplayedText.Should().NotContain("BG5JSU");
      view.SelectedIdentity.Should().Be(
        CwConsolePresentation.Identity(b));
    }

    [Fact]
    public void SelectedRxPanel_RecolorsUnconfirmedSuffixWhenCommitted()
    {
      using var view = new CwSelectedLaneView();
      CwSignalTrack track = Track(1, 77, 707);
      var slot = new CwConsoleLaneSlot(0,
        CwConsolePresentation.Identity(track), track, true);

      view.UpdateLane(slot, Transcript(1, 77, "CQ ", "TEST"));
      view.TranscriptControl.Select(3, 1);
      view.TranscriptControl.SelectionColor.Should().Be(
        System.Drawing.Color.FromArgb(155, 93, 32));

      // Same combined characters; only their committed status changes.
      view.UpdateLane(slot, Transcript(1, 77, "CQ TEST", ""));
      view.TranscriptControl.Select(3, 1);
      // RichEdit normalizes named system colors to their concrete RGB
      // value (for example WindowText becomes Black on CI hosts).
      view.TranscriptControl.SelectionColor.ToArgb().Should().Be(
        System.Drawing.SystemColors.WindowText.ToArgb());
    }

    [Fact]
    public void SelectedRxPanel_ClearsStaleSelectionOnNewTimeline()
    {
      using var view = new CwSelectedLaneView();
      CwSignalTrack track = Track(1, 12, 900);
      view.UpdateLane(
        new CwConsoleLaneSlot(0,
          CwConsolePresentation.Identity(track), track, true),
        Transcript(1, 12, "CQ", " DE"));

      view.UpdateLane(null, null);
      view.SelectedIdentity.Should().BeNull();
      view.CopyText.Should().BeEmpty();
      view.CopyButton.Enabled.Should().BeFalse();
      view.DisplayedText.Should().NotContain("CQ");
    }

    private static CwTranscriptSnapshot Transcript(
      int trackId, int hint, string committed, string provisional)
    {
      return new CwTranscriptSnapshot(
        trackId, hint, committed, provisional,
        Array.Empty<CwTranscriptSymbol>(), DateTime.UtcNow);
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
      // A 2048-point Hann FFT has a multi-bin main lobe when interpolated
      // onto the 512-bin display grid. A one-bin impulse can disappear when
      // a 512-pixel texture is minified to a 250-pixel WinForms view.
      for (int bin = 185; bin <= 191; bin++)
        power[bin] = -10f; // ~799 Hz, away from the 750-Hz grid
      var frame = new CwAudioSpectrumFrame(
        DateTime.UtcNow, 0, 100, 2000, power);
      view.SetSpectrum(frame);

      // Less than the 512-column history: old time must remain dark on
      // the left while these 40 keyed frames accumulate on the right.
      for (int i = 0; i < 40; i++)
        view.Append(frame);

      using var rendered = new System.Drawing.Bitmap(
        view.Width, view.Height);
      using (var g = System.Drawing.Graphics.FromImage(rendered))
        view.PaintContents(g);

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
    public void PileupCompactOverview_EllipsizesWithoutDiscardingFullTranscript()
    {
      using var card = new CwPileupLaneCard(0);
      var layout = card.Controls.OfType<TableLayoutPanel>().Single();
      var labels = layout.Controls.OfType<Label>().ToArray();
      labels.Should().HaveCount(2);
      labels.Should().OnlyContain(x => x.AutoEllipsis);
      layout.RowCount.Should().Be(2);

      CwSignalTrack track = Track(1, 77, 720);
      var slot = new CwConsoleLaneSlot(0,
        CwConsolePresentation.Identity(track), track, true);
      const string message =
        "CQ CQ DE BG5JSU BG5JSU 5NN TU 73 LONG LONG MESSAGE";
      var text = Transcript(1, 77, message, " AGN");
      card.UpdateLane(slot, text, CwConsolePresentation.Identity(track));

      card.CopyText.Should().Be(text.Text);
      card.PreviewText.Should().Contain("BG5JSU");
      card.PreviewText.Should().Contain("AGN");
      card.Controls.OfType<TableLayoutPanel>().Single()
        .Controls.OfType<Button>().Single()
        .Enabled.Should().BeTrue();

      // The existing Selected RX pane is the single source for
      // full multiline, scrollable transcript presentation.
      using var detail = new CwSelectedLaneView();
      detail.UpdateLane(slot, text);
      detail.DisplayedText.Should().Be(text.Text);
      detail.TranscriptControl.WordWrap.Should().BeTrue();
      detail.TranscriptControl.ScrollBars.Should()
        .Be(RichTextBoxScrollBars.Vertical);
    }

    [Fact]
    public void MacroRightClickEditor_UsesValidatedPersistentPresets()
    {
      var macros = new CwMacroSettings();
      CwMacroBank.Set(macros, 0, "CQ CQ BG5JSU");
      CwMacroBank.Get(macros, 0).Should().Be("CQ CQ BG5JSU");
      CwMacroBank.Prepare(macros, 0).Should().Be("CQ CQ BG5JSU");

      CwMacroBank.Set(macros, 0, "");
      CwMacroBank.Get(macros, 0).Should().BeEmpty();

      Action invalid = () => CwMacroBank.Set(macros, 1, "CW#BAD");
      invalid.Should().Throw<ArgumentException>();
      CwMacroBank.Get(macros, 1).Should().BeEmpty();
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
