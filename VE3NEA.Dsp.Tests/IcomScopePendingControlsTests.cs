using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IcomScopePendingControlsTests
  {
    private static readonly DateTime Epoch =
      new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static IcomScopeFrame Center(byte scope, long hz) => new()
    {
      Scope = scope,
      Mode = (byte)IcomScopeMode.Center,
      FrequencyAHz = scope == 0 ? 435_600_000 : 145_990_000,
      FrequencyBHz = hz,
      TimestampUtc = Epoch
    };

    [Fact]
    public void Span50K_PersistsWhileOld25KFramesArriveUntilRadioConfirms()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);

      for (int i = 0; i < 25; i++)
      {
        pending.ObserveFrame(Center(0, 25_000));
        pending.TryGet(0, IcomScopeControlKind.Span, out var request)
          .Should().BeTrue();
        request.SpanHz.Should().Be(50_000);
      }

      pending.ObserveFrame(Center(0, 50_000));
      pending.TryGet(0, IcomScopeControlKind.Span, out _)
        .Should().BeFalse();
      pending.Count.Should().Be(0);
    }

    [Fact]
    public void MainAndSubPendingSpansAreIndependent()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);
      pending.Track(IcomScopeControlRequest.ForSpan(1, 100_000), Epoch);
      pending.ObserveFrame(Center(0, 50_000));

      pending.TryGet(0, IcomScopeControlKind.Span, out _)
        .Should().BeFalse();
      pending.TryGet(1, IcomScopeControlKind.Span, out var sub)
        .Should().BeTrue();
      sub.SpanHz.Should().Be(100_000);
      pending.Count.Should().Be(1);
    }

    [Fact]
    public void ScopeModeOnlyCompletesWhenActualFrameMatches()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForMode(0, IcomScopeMode.Fixed),
        Epoch);
      pending.ObserveFrame(Center(0, 25_000));
      pending.TryGet(0, IcomScopeControlKind.Mode, out _)
        .Should().BeTrue();

      pending.ObserveFrame(new IcomScopeFrame
      {
        Scope = 0,
        Mode = (byte)IcomScopeMode.Fixed,
        FrequencyAHz = 435_580_000,
        FrequencyBHz = 435_620_000
      });
      pending.TryGet(0, IcomScopeControlKind.Mode, out _)
        .Should().BeFalse();
    }

    [Fact]
    public void OldReadbackDoesNotConfirmRefSpeedOrVideoBandwidth()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForReferenceLevel(0, 8.5), Epoch);
      pending.Track(IcomScopeControlRequest.ForSweepSpeed(
        0, IcomScopeSweepSpeed.Slow), Epoch);
      pending.Track(IcomScopeControlRequest.ForVbw(
        0, IcomScopeVbw.Wide), Epoch);

      pending.ObserveReadback(IcomScopeReadbackState.ParsePartial(
        "SELECT=MAIN;MAIN.REF=0.0;MAIN.SPEED=FAST;MAIN.VBW=NARROW"));
      pending.Count.Should().Be(3);

      pending.ObserveReadback(IcomScopeReadbackState.ParsePartial(
        "SELECT=MAIN;MAIN.REF=8.5;MAIN.SPEED=SLOW;MAIN.VBW=WIDE"));
      pending.Count.Should().Be(0);
    }

    [Fact]
    public void IncompleteReadbackNeverAcknowledgesAnUnqueriedField()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);
      pending.ObserveReadback(IcomScopeReadbackState.ParsePartial(
        "SELECT=MAIN;MAIN.REF=4.0"));
      pending.Count.Should().Be(1);
    }

    [Fact]
    public void OldRequestsExpireButNeverBecomeAcknowledged()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);
      pending.Expire(Epoch.AddSeconds(19)).Should().BeEmpty();
      pending.Count.Should().Be(1);

      IcomScopeControlRequest[] expired =
        pending.Expire(Epoch.AddSeconds(20));
      expired.Should().ContainSingle();
      expired[0].SpanHz.Should().Be(50_000);
      pending.Count.Should().Be(0);
    }

    [Fact]
    public void UserChangesChoiceAgain_CoalescesPendingTarget()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);
      pending.Track(IcomScopeControlRequest.ForSpan(0, 100_000),
        Epoch.AddSeconds(1));

      pending.ObserveFrame(Center(0, 50_000));
      pending.TryGet(0, IcomScopeControlKind.Span, out var requested)
        .Should().BeTrue();
      requested.SpanHz.Should().Be(100_000);
      pending.ObserveFrame(Center(0, 100_000));
      pending.Count.Should().Be(0);
    }

    [Fact]
    public void FixedEdgeWrite_WaitsForExactPresetReadback()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForFixedEdge(
        2, 3, 435_600_000, 435_680_000), Epoch);
      pending.ObserveFixedEdgeReadback(new IcomFixedEdgeReadbackState
      {
        FrequencyRange = 2, EdgeNumber = 3,
        LowerHz = 435_610_000, UpperHz = 435_680_000
      });
      pending.Count.Should().Be(1);
      pending.ObserveFixedEdgeReadback(new IcomFixedEdgeReadbackState
      {
        FrequencyRange = 2, EdgeNumber = 3,
        LowerHz = 435_600_000, UpperHz = 435_680_000
      });
      pending.Count.Should().Be(0);
    }

    [Fact]
    public void SwitchingTransportDiscardsPendingRadioWrites()
    {
      var pending = new IcomScopePendingControls();
      pending.Track(IcomScopeControlRequest.ForMode(
        0, IcomScopeMode.ScrollCenter), Epoch);
      pending.Track(IcomScopeControlRequest.ForSpan(0, 50_000), Epoch);
      pending.Clear();
      pending.Count.Should().Be(0);
    }
  }
}
