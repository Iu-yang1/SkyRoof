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
