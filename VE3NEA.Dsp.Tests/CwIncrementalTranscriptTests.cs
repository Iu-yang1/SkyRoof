using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwIncrementalTranscriptTests
  {
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OverlappingWindows_DeduplicateAndCommitStablePrefix()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      CwTranscriptSnapshot first = coordinator.Push(
        Window(
          trackId: 1,
          hintId: 101,
          endSeconds: 4.0,
          ('C', 2.00, 0.90),
          ('Q', 3.00, 0.88)));

      first.CommittedText.Should().BeEmpty();
      first.ProvisionalText.Should().Be("CQ");

      CwTranscriptSnapshot second = coordinator.Push(
        Window(
          trackId: 1,
          hintId: 101,
          endSeconds: 5.0,
          ('C', 2.01, 0.94),
          ('Q', 3.01, 0.91)));

      second.CommittedText.Should().Be("CQ");
      second.ProvisionalText.Should().BeEmpty();
      second.Text.Should().Be("CQ");
    }

    [Fact]
    public void LaterHighConfidenceWindows_CorrectProvisionalCharacter()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          2, 202, 4.0,
          ('A', 2.00, 0.55)));

      coordinator.Push(
        Window(
          2, 202, 4.7,
          ('N', 2.01, 0.95)));

      CwTranscriptSnapshot result = coordinator.Push(
        Window(
          2, 202, 5.4,
          ('N', 2.00, 0.93)));

      result.CommittedText.Should().Be("N");
      result.ProvisionalText.Should().BeEmpty();
    }

    [Fact]
    public void RepeatedCharactersAtDifferentTimes_RemainDistinct()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          3, 303, 4.0,
          ('E', 2.00, 0.92),
          ('E', 2.18, 0.91)));

      CwTranscriptSnapshot result = coordinator.Push(
        Window(
          3, 303, 5.0,
          ('E', 2.01, 0.94),
          ('E', 2.19, 0.93)));

      result.CommittedText.Should().Be("EE");
      result.Text.Should().Be("EE");
    }

    [Fact]
    public void AssociationHint_PreservesTranscriptAcrossTrackIdRebuild()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          4, 404, 4.0,
          ('K', 2.0, 0.93)));

      CwTranscriptSnapshot rebuilt = coordinator.Push(
        Window(
          99, 404, 5.0,
          ('K', 2.01, 0.95)));

      rebuilt.TrackId.Should().Be(99);
      rebuilt.AssociationHintId.Should().Be(404);
      rebuilt.CommittedText.Should().Be("K");
      coordinator.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void DifferentAssociationHints_KeepIndependentTexts()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          10, 1001, 4.0,
          ('A', 2.0, 0.95)));
      coordinator.Push(
        Window(
          20, 2002, 4.0,
          ('B', 2.0, 0.95)));

      coordinator.Push(
        Window(
          10, 1001, 5.0,
          ('A', 2.01, 0.96)));
      coordinator.Push(
        Window(
          20, 2002, 5.0,
          ('B', 2.01, 0.96)));

      coordinator.Get(
          10, 1001)!
        .CommittedText.Should().Be("A");
      coordinator.Get(
          20, 2002)!
        .CommittedText.Should().Be("B");
      coordinator.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void OneWindowHallucination_IsDroppedInsteadOfBlockingPrefixForever()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          5, 505, 4.0,
          ('X', 1.0, 0.55)));
      coordinator.Push(
        Window(
          5, 505, 5.0,
          ('C', 3.0, 0.94)));
      coordinator.Push(
        Window(
          5, 505, 5.8,
          ('C', 3.01, 0.95)));

      CwTranscriptSnapshot later = coordinator.Push(
        Window(
          5, 505, 7.0));

      later.CommittedText.Should().Be("C");
      later.ProvisionalText.Should().BeEmpty();
    }

    [Fact]
    public void MarginalLettersAcrossTwoWindows_CommitWithoutTruncatingAReport()
    {
      var coordinator = NewCoordinator();
      var first = Window(8, 808, 5.0,
        ('C', 2.00, 0.55), ('Q', 2.25, 0.55),
        (' ', 2.50, 0.55), ('5', 2.75, 0.55),
        ('N', 3.00, 0.55), ('N', 3.25, 0.55));
      var second = Window(8, 808, 6.0,
        ('C', 2.01, 0.56), ('Q', 2.26, 0.56),
        (' ', 2.51, 0.56), ('5', 2.76, 0.56),
        ('N', 3.01, 0.56), ('N', 3.26, 0.56));

      coordinator.Push(first).CommittedText.Should().BeEmpty();
      var result = coordinator.Push(second);
      result.CommittedText.Should().Be("CQ 5NN");
      result.ProvisionalText.Should().BeEmpty();
    }

    [Fact]
    public void Reset_RemovesAllLaneTranscriptState()
    {
      CwIncrementalTranscriptCoordinator coordinator =
        NewCoordinator();

      coordinator.Push(
        Window(
          6, 606, 4.0,
          ('C', 2.0, 0.9)));

      coordinator.Reset();

      coordinator.GetAll().Should().BeEmpty();
      coordinator.Get(6, 606).Should().BeNull();
    }

    private static CwIncrementalTranscriptCoordinator
      NewCoordinator() =>
      new(new CwTranscriptOptions
      {
        SymbolMatchToleranceSeconds = 0.22,
        CommitLagSeconds = 0.90,
        AbandonAfterSeconds = 2.5,
        MinimumConfirmations = 2,
        MinimumConsensus = 0.60,
        MinimumAverageConfidence = 0.52,
        CommittedMatchRetentionSeconds = 8
      });

    private static DeepCwLaneResult Window(
      int trackId,
      int hintId,
      double endSeconds,
      params (
        char Character,
        double AbsoluteSeconds,
        double Confidence)[] symbols)
    {
      const double duration = 4.0;
      const int outputFrames = 100;

      DateTime end =
        T0.AddSeconds(endSeconds);
      double startSeconds =
        endSeconds - duration;

      DeepCwDecodedSymbol[] decoded =
        symbols.Select(x =>
        {
          double fraction =
            (x.AbsoluteSeconds -
             startSeconds) /
            duration;
          int frame = (int)Math.Round(
            fraction *
            outputFrames -
            0.5);
          frame = Math.Clamp(
            frame,
            0,
            outputFrames - 1);

          return new DeepCwDecodedSymbol(
            x.Character,
            frame,
            x.Confidence);
        }).ToArray();

      return new DeepCwLaneResult(
        trackId,
        InputFrequencyHz: 800,
        Text: new string(
          decoded
            .Select(x => x.Character)
            .ToArray()),
        Active: true,
        Ambiguous: false,
        WindowEndUtc: end,
        AssociationHintId: hintId,
        Symbols: decoded,
        OutputFrameCount: outputFrames,
        WindowDurationSeconds: duration);
    }
  }
}
