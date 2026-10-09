using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwFixedLagAssociatorTests
  {
    private const int SampleRate = 48000;
    private const long Hop = 5760; // 120 ms @ 48 kHz

    [Fact]
    public void CrossingRidges_UseFutureFramesToPreserveHintIdentity()
    {
      CwFixedLagAssociator associator = NewAssociator();
      var committed = new List<CwAssociatedCandidateBatch>();

      (double A, double B, double SnrA, double SnrB)[] frames =
      [
        (760, 840, 8, 18),
        (772, 828, 10, 16),
        (784, 816, 14, 12),
        (796, 804, 20, 7),
        (808, 792, 22, 6),
        (820, 780, 18, 8),
        (832, 768, 16, 10),
        (844, 756, 14, 12),
        (856, 744, 12, 14)
      ];

      for (int i = 0; i < frames.Length; i++)
      {
        var f = frames[i];
        committed.AddRange(
          associator.Push(
            Batch(
              i,
              (f.A, f.SnrA, 11),
              (f.B, f.SnrB, 22))));
      }

      CwAssociatedCandidateBatch first =
        committed.Single(x =>
          x.CenterSampleIndex == 0);
      int risingHint = first.Candidates
        .Single(x => x.FrequencyHz < 800)
        .AssociationHintId;
      int fallingHint = first.Candidates
        .Single(x => x.FrequencyHz > 800)
        .AssociationHintId;

      risingHint.Should().BeGreaterThan(0);
      fallingHint.Should().BeGreaterThan(0);
      risingHint.Should().NotBe(fallingHint);

      CwAssociatedCandidateBatch afterCrossing =
        committed.Single(x =>
          x.CenterSampleIndex == 4 * Hop);

      afterCrossing.Candidates
        .Single(x =>
          Math.Abs(x.FrequencyHz - 808) < 1)
        .AssociationHintId.Should().Be(risingHint);
      afterCrossing.Candidates
        .Single(x =>
          Math.Abs(x.FrequencyHz - 792) < 1)
        .AssociationHintId.Should().Be(fallingHint);
    }

    [Fact]
    public void OneMergedPrecisionFrame_DoesNotSwapHintsAfterSplit()
    {
      CwFixedLagAssociator associator = NewAssociator();
      var committed = new List<CwAssociatedCandidateBatch>();

      CwRidgeObservationBatch[] frames =
      [
        Batch(0, (760, 12, 1), (840, 12, 2)),
        Batch(1, (775, 10, 1), (825, 14, 2)),
        Batch(2, (790, 9, 1), (810, 16, 2)),
        Batch(3, (800, 15, 99)),
        Batch(4, (820, 18, 1), (780, 8, 2)),
        Batch(5, (835, 17, 1), (765, 9, 2)),
        Batch(6, (850, 16, 1), (750, 10, 2)),
        Batch(7, (865, 15, 1), (735, 11, 2))
      ];

      foreach (CwRidgeObservationBatch frame in frames)
        committed.AddRange(associator.Push(frame));

      CwAssociatedCandidateBatch first =
        committed.Single(x =>
          x.CenterSampleIndex == 0);
      int risingHint = first.Candidates
        .Single(x => x.FrequencyHz < 800)
        .AssociationHintId;
      int fallingHint = first.Candidates
        .Single(x => x.FrequencyHz > 800)
        .AssociationHintId;

      CwAssociatedCandidateBatch split =
        committed.Single(x =>
          x.CenterSampleIndex == 4 * Hop);

      split.Candidates
        .Single(x => x.FrequencyHz > 800)
        .AssociationHintId.Should().Be(risingHint);
      split.Candidates
        .Single(x => x.FrequencyHz < 800)
        .AssociationHintId.Should().Be(fallingHint);
    }

    [Fact]
    public void SingleFrameClutter_IsNotCommittedAsNewIdentity()
    {
      CwFixedLagAssociator associator = NewAssociator();

      associator.Push(
        Batch(0, (800, 8, 1)))
        .Should().BeEmpty();
      associator.Push(EmptyBatch(1))
        .Should().BeEmpty();
      associator.Push(EmptyBatch(2))
        .Should().BeEmpty();

      IReadOnlyList<CwAssociatedCandidateBatch> output =
        associator.Push(EmptyBatch(3));

      output.Should().ContainSingle();
      output[0].CenterSampleIndex.Should().Be(0);
      output[0].Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Flush_CommitsPendingTimelineInOrder()
    {
      CwFixedLagAssociator associator = NewAssociator();

      associator.Push(
        Batch(0, (800, 12, 1)));
      associator.Push(
        Batch(1, (803, 12, 1)));

      IReadOnlyList<CwAssociatedCandidateBatch> flushed =
        associator.Flush();

      flushed.Select(x => x.CenterSampleIndex)
        .Should().Equal(0, Hop);
      flushed[0].Candidates.Should().ContainSingle();
      flushed[0].Candidates[0].AssociationHintId
        .Should().BeGreaterThan(0);
    }

    private static CwFixedLagAssociator NewAssociator() =>
      new(new CwFixedLagAssociatorOptions
      {
        SampleRate = SampleRate,
        LagBatches = 3,
        MaxHypotheses = 32,
        AssignmentBeamWidth = 64,
        MaxPaths = 8,
        MaxMisses = 3,
        MaxLinkHz = 45,
        MaxSlopeHzPerSecond = 150,
        VelocityChangeSigmaHzPerSecond = 35,
        ProcessFrequencySigmaHz = 2.5,
        MissCost = 3.0,
        BirthCost = 2.2,
        MinimumNewPathObservations = 2
      });

    private static CwRidgeObservationBatch Batch(
      int frame,
      params (double Hz, double Snr, int Portion)[] peaks)
    {
      long sample = frame * Hop;
      CwRidgeObservation[] observations =
        peaks.Select(x =>
          new CwRidgeObservation(
            sample,
            x.Hz,
            x.Snr,
            MeasurementSigmaHz: 2.0,
            ResolutionHz: 4.1666667,
            ActivityProbability: 0.90,
            CwRidgeObservationScale.Precision,
            x.Portion,
            KalmanEligible: true))
        .ToArray();

      return new(sample, observations);
    }

    private static CwRidgeObservationBatch EmptyBatch(
      int frame) =>
      new(
        frame * Hop,
        Array.Empty<CwRidgeObservation>());
  }
}
