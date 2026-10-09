using System;
using System.Linq;
using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class CwPileupTrackManagerTests
  {
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SimultaneousStations_CreateIndependentStableTracks()
    {
      var tracker = new CwPileupTrackManager();
      var initial = tracker.Update(T0,
      [
        new(600, 12),
        new(850, 6),
        new(1100, 18)
      ]);
      initial.Should().HaveCount(3);
      initial.Should().OnlyContain(t => !t.Confirmed);

      var confirmed = tracker.Update(T0.AddMilliseconds(500),
      [
        new(603, 11),
        new(852, 5),
        new(1097, 17)
      ]);
      confirmed.Should().HaveCount(3);
      confirmed.Should().OnlyContain(t => t.Confirmed && t.Active);
      confirmed.Select(t => t.Id).Should().BeEquivalentTo(initial.Select(t => t.Id));
    }

    [Fact]
    public void DopplerDriftAndChangedSignalStrength_DoNotSwapIds()
    {
      var tracker = new CwPileupTrackManager(matchToleranceHz: 55);
      var start = tracker.Update(T0, [new(650, 1), new(970, 20)]);
      int lowerId = start[0].Id;
      int upperId = start[1].Id;

      tracker.Update(T0.AddMilliseconds(250),
        [new(660, 25), new(955, 3)]);
      var result = tracker.Update(T0.AddMilliseconds(750),
        [new(683, 25), new(933, 2)]);

      result.Should().HaveCount(2);
      result[0].Id.Should().Be(lowerId);
      result[1].Id.Should().Be(upperId);
      result[0].DriftHzPerSecond.Should().BeGreaterThan(0);
      result[1].DriftHzPerSecond.Should().BeLessThan(0);
    }

    [Fact]
    public void ShortFade_PreservesTrackIdentity_ThenExpires()
    {
      var tracker = new CwPileupTrackManager();
      int id = tracker.Update(T0, [new(800, 12)])[0].Id;
      tracker.Update(T0.AddMilliseconds(500), [new(801, 11)]);
      var faded = tracker.Update(T0.AddSeconds(2), []);
      faded.Should().ContainSingle().Which.Id.Should().Be(id);
      faded[0].Active.Should().BeFalse();

      var reacquired = tracker.Update(T0.AddSeconds(3), [new(802, 9)]);
      reacquired.Should().ContainSingle().Which.Id.Should().Be(id);
      reacquired[0].Active.Should().BeTrue();

      tracker.Update(T0.AddSeconds(9), []).Should().BeEmpty();
    }

    [Fact]
    public void ResolvableFifteenHertzCandidates_CanCreateSeparateTracks()
    {
      var tracker = new CwPileupTrackManager(
        minimumSeparationHz: 8,
        candidateDeduplicationHz: 2);

      var result = tracker.Update(T0,
      [
        new CwSignalCandidate(
          700, 14,
          MeasurementSigmaHz: 2,
          ResolutionHz: 12.5),
        new CwSignalCandidate(
          715, 11,
          MeasurementSigmaHz: 2,
          ResolutionHz: 12.5)
      ]);

      result.Should().HaveCount(2);
      result.Select(x => x.FrequencyHz)
        .Should().BeEquivalentTo([700, 715]);
    }

    [Fact]
    public void NearbyDuplicatePeaks_AreSuppressedWithoutMergingSeparateSignals()
    {
      var tracker = new CwPileupTrackManager(minimumSeparationHz: 30);
      var result = tracker.Update(T0,
        [new(700, 20), new(710, 10), new(780, 5)]);
      result.Select(x => x.FrequencyHz).Should().BeEquivalentTo([700, 780]);
    }

    [Fact]
    public void CapacityLimit_KeepsStrongestEightLanes()
    {
      var tracker = new CwPileupTrackManager();
      var results = tracker.Update(T0,
        Enumerable.Range(0, 12).Select(i => new CwSignalCandidate(
          200 + i * 50, i)).ToArray());
      results.Should().HaveCount(8);
      results.Min(x => x.SnrDb).Should().Be(4);
    }

    [Fact]
    public void InvalidDetectorBins_AreIgnored()
    {
      var tracker = new CwPileupTrackManager();
      tracker.Update(T0,
      [
        new(double.NaN, 12),
        new(800, double.PositiveInfinity),
        new(-50, 12),
        new(690, 5)
      ]).Should().ContainSingle().Which.FrequencyHz.Should().Be(690);
    }

    [Fact]
    public void NonMonotonicTimestamps_AreRejected()
    {
      var tracker = new CwPileupTrackManager();
      tracker.Update(T0.AddSeconds(1), []);
      Action backwards = () => tracker.Update(T0, []);
      backwards.Should().Throw<ArgumentException>();
    }


    [Fact]
    public void CrossingRidges_WithMergedMeasurement_PreserveTrackIdentity()
    {
      var tracker = new CwPileupTrackManager(
        minimumSeparationHz: 10,
        matchToleranceHz: 70,
        mergeResolutionHz: 22);

      var start = tracker.Update(T0,
        [new(700, 12), new(900, 12)]);
      int risingId = start.Single(x => x.FrequencyHz < 800).Id;
      int fallingId = start.Single(x => x.FrequencyHz > 800).Id;

      tracker.Update(T0.AddMilliseconds(500),
        [new(730, 10), new(870, 14)]);
      tracker.Update(T0.AddSeconds(1),
        [new(760, 9), new(840, 15)]);
      var close = tracker.Update(T0.AddMilliseconds(1500),
        [new(792, 8), new(808, 16)]);

      close.Should().Contain(x => x.Id == risingId);
      close.Should().Contain(x => x.Id == fallingId);
      close.Should().OnlyContain(x => x.Ambiguous);

      // Finite spectral resolution can merge two ridges into one observation.
      // The unobserved target must coast instead of being deleted.
      var merged = tracker.Update(T0.AddSeconds(2),
        [new(800, 13)]);
      merged.Should().HaveCount(2);
      merged.Count(x => x.Active).Should().Be(1);

      // After the crossing, order in frequency has reversed. Identity must
      // follow predicted ridge velocity, not sorted frequency or signal power.
      var separated = tracker.Update(T0.AddMilliseconds(2500),
        [new(748, 18), new(852, 6)]);

      CwSignalTrack rising = separated.Single(x => x.Id == risingId);
      CwSignalTrack falling = separated.Single(x => x.Id == fallingId);
      rising.FrequencyHz.Should().BeGreaterThan(falling.FrequencyHz);
      rising.DriftHzPerSecond.Should().BeGreaterThan(0);
      falling.DriftHzPerSecond.Should().BeLessThan(0);
    }

    [Fact]
    public void GlobalAssociation_DoesNotLetStrongPeakStealWeakTrack()
    {
      var tracker = new CwPileupTrackManager(
        minimumSeparationHz: 8,
        matchToleranceHz: 45);

      var start = tracker.Update(T0,
        [new(700, 20), new(750, 4)]);
      int lowId = start.Single(x => x.FrequencyHz < 725).Id;
      int highId = start.Single(x => x.FrequencyHz > 725).Id;

      tracker.Update(T0.AddMilliseconds(500),
        [new(712, 3), new(738, 24)]);
      var result = tracker.Update(T0.AddSeconds(1),
        [new(720, 2), new(730, 26)]);

      result.Single(x => x.Id == lowId)
        .DriftHzPerSecond.Should().BeGreaterThan(0);
      result.Single(x => x.Id == highId)
        .DriftHzPerSecond.Should().BeLessThan(0);
    }

    [Fact]
    public void Reset_RemovesTracksAndReinitializesIds()
    {
      var tracker = new CwPileupTrackManager();
      tracker.Update(T0, [new(700, 4)]);
      tracker.Reset();
      tracker.Update(T0, [new(900, 6)])
        .Should().ContainSingle().Which.Id.Should().Be(1);
    }
  }
}
