using System;
using System.Collections.Generic;
using System.Linq;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwTrackIdentityResolverTests
  {
    private const int SampleRate = 48000;
    private const int Hop = 720;
    private static readonly DateTime Now =
      new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

    private static CwSignalTrack Track(int id, double frequency,
      int hint = 0, double drift = 0) =>
      new(id,frequency,16,drift,Now.AddSeconds(-3),
        Now,true,true,AssociationHintId:hint,
        KeyingStatus:CwKeyingStatus.Verified);

    private static CwRidgeObservation Observation(long at, double hz,
      CwRidgeObservationScale scale = CwRidgeObservationScale.Fast) =>
      new(at,hz,18,2.0,
        scale == CwRidgeObservationScale.Fast ? 12.5 : 4.17,
        0.95,scale,1,scale == CwRidgeObservationScale.Precision);

    private static CwFrameRidgeScanResult Keyed(double frequency,
      IReadOnlyList<CwRidgeObservationBatch>? precision = null)
    {
      var fast = new List<CwRidgeObservation>();
      // Include off frames as gaps; the resolver must reconstruct the
      // original 15 ms sample axis rather than correlate only ON samples.
      for (int i=0; i<96; i++)
        if (i%12<6)
          fast.Add(Observation(i*Hop,frequency));
      return new CwFrameRidgeScanResult(
        Array.Empty<CwRidgePortion>(),fast,
        precision??Array.Empty<CwRidgeObservationBatch>());
    }

    [Fact]
    public void KeyedCarrierRebirth_ReusesOneIdentityAndRetiresOtherTrack()
    {
      var first=Track(40,990,100);
      var duplicate=Track(41,993,101);
      CwFrameRidgeScanResult scan=Keyed(991.5);
      CwIdentityResolution result=CwTrackIdentityResolver.Resolve(
        [first,duplicate],scan,SampleRate,95*Hop,Hop);
      Assert.Single(result.Tracks);
      Assert.Equal(first.Id,result.Tracks[0].Id);
      Assert.Equal(new[] {duplicate.Id},result.RetiredTrackIds);

      var tracker=new CwPileupTrackManager();
      var initial=tracker.Update(Now,
        [new CwSignalCandidate(990,14),
         new CwSignalCandidate(1010,14)]);
      tracker.RetireAliases([initial[1].Id]);
      var retained=tracker.Update(Now.AddMilliseconds(200),
        [new CwSignalCandidate(990,14)]);
      Assert.Single(retained);
      Assert.Equal(initial[0].Id,retained[0].Id);
    }

    [Fact]
    public void SimultaneousResolvedFiveHertzDoublet_RemainsTwoStations()
    {
      var first=Track(40,990,100);
      var second=Track(41,995,101);
      var precision=new[]
      {
        new CwRidgeObservationBatch(30*Hop,
          [Observation(30*Hop,990,CwRidgeObservationScale.Precision),
           Observation(30*Hop,995,CwRidgeObservationScale.Precision)]),
        new CwRidgeObservationBatch(40*Hop,
          [Observation(40*Hop,990,CwRidgeObservationScale.Precision),
           Observation(40*Hop,995,CwRidgeObservationScale.Precision)])
      };
      var result=CwTrackIdentityResolver.Resolve(
        [first,second],Keyed(992.5,precision),
        SampleRate,95*Hop,Hop);
      Assert.Equal(2,result.Tracks.Count);
      Assert.Empty(result.RetiredTrackIds);
    }

    [Fact]
    public void FifteenHertzDoublet_OrDivergentDoppler_NeverAutoMerges()
    {
      CwFrameRidgeScanResult scan=Keyed(991.5);
      var separated=CwTrackIdentityResolver.Resolve(
        [Track(1,990),Track(2,1005)],
        scan,SampleRate,95*Hop,Hop);
      Assert.Equal(2,separated.Tracks.Count);

      var drifting=CwTrackIdentityResolver.Resolve(
        [Track(1,990,drift:-20),Track(2,992,drift:20)],
        scan,SampleRate,95*Hop,Hop);
      Assert.Equal(2,drifting.Tracks.Count);
    }

    [Fact]
    public void SameFrequencyWithoutIndependentKeyingEvidence_IsNotEnough()
    {
      var empty=new CwFrameRidgeScanResult(
        Array.Empty<CwRidgePortion>(),
        Array.Empty<CwRidgeObservation>(),
        Array.Empty<CwRidgeObservationBatch>());
      var resolution=CwTrackIdentityResolver.Resolve(
        [Track(1,990,11),Track(2,992,12)],
        empty,SampleRate,95*Hop,Hop);
      Assert.Equal(2,resolution.Tracks.Count);
    }

    [Fact]
    public void InferenceMasksCollapseOnlyCoherentNonFlatAliases()
    {
      var a=Track(1,990);
      var b=Track(2,993);
      var independent=Track(3,1005);
      float[] keyed=Enumerable.Range(0,120)
        .Select(i=>i%12<6?0.95f:0.05f).ToArray();
      float[] alternate=Enumerable.Range(0,120)
        .Select(i=>i%12<6?0.05f:0.95f).ToArray();
      Assert.True(CwTrackIdentityResolver.SameActivityAlias(
        a,b,keyed,keyed));
      Assert.False(CwTrackIdentityResolver.SameActivityAlias(
        a,b,keyed,alternate));
      Assert.False(CwTrackIdentityResolver.SameActivityAlias(
        a,independent,keyed,keyed));
      Assert.False(CwTrackIdentityResolver.SameActivityAlias(
        a,b,Enumerable.Repeat(0.9f,120).ToArray(),
        Enumerable.Repeat(0.9f,120).ToArray()));

      var selected=CwTrackIdentityResolver.DeduplicateInferenceLanes(
        [a,b,independent],
        new Dictionary<int,float[]> {
          [a.Id]=keyed,[b.Id]=keyed,[independent.Id]=keyed
        });
      Assert.Equal(new[]{1,3},selected.Select(x=>x.Id));
    }
  }
}
