using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwNarrowbandCarrierEvidenceTests
  {
    private const int Rate=48000;
    private static readonly DateTime T0=
      new(2026,10,29,4,0,0,DateTimeKind.Utc);

    private static CwSignalTrack Track(double frequency)=>
      new(1,frequency,12,0,T0.AddSeconds(-2.4),T0,true,true);

    private static CwAudioSnapshot Snapshot(float[] samples)=>
      new(Rate,T0,samples.Length,samples);

    [Theory]
    [InlineData(800.0)]
    [InlineData(270.0)]
    public void GenuineKeyedCarrierPassesIndependentPcmGate(double hz)
    {
      int count=(int)(Rate*2.4);
      var samples=new float[count];
      var rng=new Random(1000);
      double phase=0;
      for(int i=0;i<count;i++)
      {
        double t=i/(double)Rate;
        bool keyed=(t%0.19)<0.105;
        phase+=2*Math.PI*hz/Rate;
        samples[i]=(float)(
          (keyed?0.33:0)*Math.Sin(phase)+
          (rng.NextDouble()-0.5)*0.01);
      }
      CwNarrowbandCarrierEvidence.Result evidence=
        CwNarrowbandCarrierEvidence.Assess(
          Snapshot(samples),Track(hz));
      evidence.IsKeyed.Should().BeTrue(
        $"keyed CW at {hz:F0} Hz should survive, evidence {evidence}");
      evidence.HighContrastFrames.Should().BeGreaterThan(10);
    }

    [Fact]
    public void NoiseOnlyDoesNotBecomeCwDespiteOccasionalLargeFftPeaks()
    {
      const double seconds=2.4;
      int count=(int)(Rate*seconds);
      var samples=new float[count];
      var rng=new Random(42);
      for(int i=0;i<count;i++)
        samples[i]=(float)((rng.NextDouble()-0.5)*0.035);
      foreach(double hz in new[]{245.0,271.0,289.0,308.0})
        CwNarrowbandCarrierEvidence.Assess(Snapshot(samples),Track(hz))
          .IsKeyed.Should().BeFalse(
            $"raw AF noise must not confirm {hz:F0} Hz as a CW station");
    }

    [Fact]
    public void BroadbandAfNoiseAmplitudeKeyingIsNotACarrier()
    {
      const double seconds=2.4;
      int count=(int)(Rate*seconds);
      var samples=new float[count];
      var rng=new Random(732);
      // Burst-modulated broadband noise mimics Morse-like on/off timing
      // and can fool a sparse STFT candidate sequence. There is no
      // independent narrowband oscillator at 245/271/289/308 Hz.
      double lowPass=0;
      for(int i=0;i<count;i++)
      {
        double t=i/(double)Rate;
        double burst=t%0.22<0.12 ? 1.0 : 0.06;
        lowPass=0.985*lowPass+0.015*(rng.NextDouble()-0.5);
        samples[i]=(float)(burst*lowPass);
      }
      foreach(double hz in new[]{245.0,271.0,289.0,308.0})
        CwNarrowbandCarrierEvidence.Assess(Snapshot(samples),Track(hz))
          .IsKeyed.Should().BeFalse(
            $"AM broadband noise is not station {hz:F0} Hz");
    }

    [Fact]
    public void ContinuousCarrierIsStillRejectedWithoutMorseEnvelope()
    {
      int count=(int)(Rate*2.4);
      var samples=new float[count];
      for(int i=0;i<count;i++)
        samples[i]=(float)(0.4*Math.Sin(2*Math.PI*800*i/Rate));
      CwNarrowbandCarrierEvidence.Assess(Snapshot(samples),Track(800))
        .IsKeyed.Should().BeFalse();
    }

    [Fact]
    public void TwoIndependentCloseKeyedStationsRemainEligible()
    {
      int count=(int)(Rate*2.4);
      var samples=new float[count];
      for(int i=0;i<count;i++)
      {
        double t=i/(double)Rate;
        if(t%0.19<0.11)
          samples[i]+=(float)(0.34*Math.Sin(2*Math.PI*800*t));
        if(t%0.27<0.14)
          samples[i]+=(float)(0.24*Math.Sin(2*Math.PI*815*t));
      }
      var snapshot=Snapshot(samples);
      CwNarrowbandCarrierEvidence.Assess(snapshot,Track(800))
        .IsKeyed.Should().BeTrue();
      CwNarrowbandCarrierEvidence.Assess(snapshot,Track(815))
        .IsKeyed.Should().BeTrue();
    }
  }
}
