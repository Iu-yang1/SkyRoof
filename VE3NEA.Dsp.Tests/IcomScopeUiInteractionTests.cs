using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IcomScopeUiInteractionTests
  {
    private static readonly DateTime Epoch =
      new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private static IcomScopeFrame Frame(int index) => new()
    {
      Scope = 0,
      Mode = (byte)IcomScopeMode.Center,
      FrequencyAHz = 435_600_000,
      FrequencyBHz = 25_000,
      TimestampUtc = Epoch.AddMilliseconds(index * 10)
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Span100K_HoveringOrFocusedCannotBeReplacedBy25KReadback(
      bool dropped, bool focused)
    {
      // 100 kHz user selection (index 5) vs stale 25 kHz (index 3).
      IcomScopeUiSelectionPolicy.ShouldWriteRemoteSelection(
        dropped, focused, 5, 3).Should().BeFalse();
    }

    [Fact]
    public void ClosedUnfocusedSelectorFollowsActualRadioChanges()
    {
      IcomScopeUiSelectionPolicy.ShouldWriteRemoteSelection(
        false, false, 5, 3).Should().BeTrue();
      IcomScopeUiSelectionPolicy.ShouldWriteRemoteSelection(
        false, false, 3, 3).Should().BeFalse();
    }

    [Fact]
    public void BurstOfScopeFramesProducesOnePendingUiCallback()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(1)).Should().BeTrue();
      for (int i = 2; i < 100; i++)
        mailbox.Offer(Frame(i)).Should().BeFalse();

      mailbox.ReplacedFrames.Should().Be(98);
      mailbox.TakeAll().Should().ContainSingle().Which.TimestampUtc.Should().Be(Frame(99).TimestampUtc);
      mailbox.TakeAll().Should().BeEmpty();

      mailbox.Offer(Frame(100)).Should().BeTrue();
      mailbox.TakeAll().Should().ContainSingle().Which.TimestampUtc.Should().Be(Frame(100).TimestampUtc);
    }

    [Fact]
    public void OutOfOrderScopeFrameNeverReplacesNewerPendingFrame()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(40)).Should().BeTrue();
      mailbox.Offer(Frame(12)).Should().BeFalse();
      mailbox.TakeAll().Should().ContainSingle().Which.TimestampUtc.Should().Be(Frame(40).TimestampUtc);
      mailbox.ReplacedFrames.Should().Be(0);
    }

    [Fact]
    public void InterleavedMainAndSubKeepNewestFromBothReceivers()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(5)).Should().BeTrue();
      IcomScopeFrame sub = new()
      {
        Scope = 1,
        Mode = (byte)IcomScopeMode.Center,
        FrequencyAHz = 145_990_000,
        FrequencyBHz = 50_000,
        TimestampUtc = Epoch.AddMilliseconds(60)
      };
      mailbox.Offer(sub).Should().BeFalse();
      IcomScopeFrame[] two = mailbox.TakeAll();
      two.Should().HaveCount(2);
      two[0].Scope.Should().Be(0);
      two[1].Scope.Should().Be(1);
    }

    [Fact]
    public void NewerPartialDoesNotEraseCompletedSweepUsedByWaterfall()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      IcomScopeFrame complete = Frame(10);
      IcomScopeFrame partial = new()
      {
        Scope = 0,
        Mode = (byte)IcomScopeMode.Center,
        TimestampUtc = Frame(11).TimestampUtc,
        FrequencyAHz = complete.FrequencyAHz,
        FrequencyBHz = complete.FrequencyBHz,
        SweepComplete = false,
        DivisionCurrent = 2,
        DivisionMaximum = 11
      };
      mailbox.Offer(complete).Should().BeTrue();
      mailbox.Offer(partial).Should().BeFalse();
      IcomScopeFrame[] frames = mailbox.TakeAll();
      frames.Should().HaveCount(2);
      frames[0].SweepComplete.Should().BeTrue();
      frames[1].SweepComplete.Should().BeFalse();
      mailbox.ReplacedCompleteSweeps.Should().Be(0);
    }

    [Fact]
    public void NewCompletedSweepSupersedesOlderPartial()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      IcomScopeFrame partial = new()
      {
        Scope = 0, TimestampUtc = Frame(5).TimestampUtc,
        SweepComplete = false, DivisionMaximum = 11
      };
      mailbox.Offer(partial).Should().BeTrue();
      mailbox.Offer(Frame(10)).Should().BeFalse();
      mailbox.TakeAll().Should().ContainSingle()
        .Which.SweepComplete.Should().BeTrue();
    }

    [Fact]
    public void FullAndPartialFromBothReceiversArePreservedAndOrdered()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(1)).Should().BeTrue();
      mailbox.Offer(new IcomScopeFrame {
        Scope = 1, TimestampUtc = Frame(2).TimestampUtc,
        SweepComplete = true
      }).Should().BeFalse();
      mailbox.Offer(new IcomScopeFrame {
        Scope = 0, TimestampUtc = Frame(3).TimestampUtc,
        SweepComplete = false
      }).Should().BeFalse();
      mailbox.Offer(new IcomScopeFrame {
        Scope = 1, TimestampUtc = Frame(4).TimestampUtc,
        SweepComplete = false
      }).Should().BeFalse();
      var frames = mailbox.TakeAll();
      frames.Should().HaveCount(4);
      frames.Select(x => x.Scope).Should().Equal(0, 1, 0, 1);
      frames.Select(x => x.SweepComplete).Should().Equal(
        true, true, false, false);
    }

    [Fact]
    public void StatusPollFallbackCannotDiscardPendingCompletedSweep()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(10)).Should().BeTrue();
      var currentPartial = new IcomScopeFrame
      {
        Scope = 0,
        TimestampUtc = Frame(11).TimestampUtc,
        SweepComplete = false,
        DivisionCurrent = 2,
        DivisionMaximum = 11
      };
      mailbox.Offer(currentPartial).Should().BeFalse();

      // Status refresh observes the same latest partial while the
      // original event callback is still queued. The fallback must not
      // call RenderScopeFrame directly (which would advance its timestamp
      // beyond the completed sweep); it offers it to this same mailbox.
      mailbox.Offer(currentPartial).Should().BeFalse();

      var delivered = mailbox.TakeAll();
      delivered.Should().HaveCount(2);
      delivered[0].SweepComplete.Should().BeTrue();
      delivered[1].SweepComplete.Should().BeFalse();
    }

    [Fact]
    public void ReplacementOfCompleteSweepsIsMeasuredSeparately()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(1)).Should().BeTrue();
      mailbox.Offer(Frame(2)).Should().BeFalse();
      mailbox.ReplacedCompleteSweeps.Should().Be(1);
      mailbox.TakeAll().Should().ContainSingle()
        .Which.TimestampUtc.Should().Be(Frame(2).TimestampUtc);
      mailbox.Clear();
      mailbox.ReplacedCompleteSweeps.Should().Be(0);
    }

    [Fact]
    public void CaptureSessionRestartInvalidatesPendingWaveform()
    {
      var mailbox = new IcomScopeUiFrameMailbox();
      mailbox.Offer(Frame(5)).Should().BeTrue();
      mailbox.Clear();
      mailbox.TakeAll().Should().BeEmpty();
      mailbox.Offer(Frame(1)).Should().BeTrue();
    }
  }
}
