using System;
using System.Linq;
using SkyRoof.AFC;
using FluentAssertions;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcBurstTrackingTests
  {
    private static readonly DateTime Epoch = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int Fs = 48000;

    [Theory]
    [InlineData(AfcSignalMode.FM, false, 19200, 125)]
    [InlineData(AfcSignalMode.CW, false, 18000, 125)]
    [InlineData(AfcSignalMode.SSB, true, 18000, 150)]
    [InlineData(AfcSignalMode.SSB, false, 36000, 250)]
    public void ModesHaveIndependentShortCaptureProfiles(AfcSignalMode mode,
      bool pilot, int minSamples, int cadence)
    {
      var p = AfcTrackingProfile.For(mode, pilot);
      p.MinimumSamples.Should().Be(minSamples);
      p.CadenceMilliseconds.Should().Be(cadence);
      p.WindowSamples.Should().BeGreaterThanOrEqualTo(p.MinimumSamples);
    }

    [Fact]
    public void FmCarrierIsMeasuredInSingleFourHundredMillisecondBurst()
    {
      var pcm = new float[19200];
      double phase = 0;
      for (int i = 0; i < pcm.Length; i++)
      {
        double t = i / (double)Fs;
        phase += 2 * Math.PI / Fs *
          (12000 + 175 + 1100 * Math.Sin(2 * Math.PI * 330 * t));
        pcm[i] = (float)(.35 * Math.Cos(phase));
      }
      var obs = new AfcSignalProcessing().Analyze(pcm, AfcSignalMode.FM, Epoch, 1);
      obs.HasAbsoluteReference.Should().BeTrue();
      obs.IfOffsetHz.Should().BeApproximately(175, 55);
      obs.Confidence.Should().BeGreaterThan(.6);
    }

    [Theory]
    [InlineData(AfcSignalMode.CW)]
    [InlineData(AfcSignalMode.SSB)]
    public void ShortCarrierIsTrackedWithNarrowFrequencyGate(AfcSignalMode mode)
    {
      var pcm = new float[18000];
      var rnd = new Random(821);
      for (int i = 0; i < pcm.Length; i++)
      {
        double t = i / (double)Fs;
        double wanted = i >= 7000
          ? .25 * Math.Cos(2 * Math.PI * 12110 * t) : 0;
        double outside = .4 * Math.Cos(2 * Math.PI * 12490 * t);
        pcm[i] = (float)(wanted + outside + .008 * (rnd.NextDouble() - .5));
      }
      var obs = new AfcSignalProcessing().Analyze(pcm, mode, Epoch, 4,
        expectedOffsetHz: 110, ssbPilot: mode == AfcSignalMode.SSB);
      obs.HasAbsoluteReference.Should().BeTrue();
      obs.IfOffsetHz.Should().BeApproximately(110, 8);
    }

    [Fact]
    public void NoSsbPilotNeverAcquiresAnAbsoluteAfcReference()
    {
      float[] pcm = Enumerable.Range(0, 48000)
        .Select(i => (float)(.3 * Math.Cos(2 * Math.PI * 12280 * i / Fs)))
        .ToArray();
      var obs = new AfcSignalProcessing().Analyze(pcm,
        AfcSignalMode.SSB, Epoch, 17, ssbPilot: false);
      obs.HasAbsoluteReference.Should().BeFalse();
    }

    [Fact]
    public void BurstPredictorSurvivesCwKeyGapsButRejectsNoiseJumps()
    {
      var filter = new AfcBurstPredictor(2.5, 35);
      filter.Update(105, 2, .92, Epoch).Should().BeTrue();
      filter.Update(113, 2, .92, Epoch.AddMilliseconds(300))
        .Should().BeTrue();
      filter.TryPredict(Epoch.AddSeconds(1.5), out double expected)
        .Should().BeTrue();
      expected.Should().BeInRange(100, 150);
      filter.Update(900, 3, .99, Epoch.AddSeconds(1.6))
        .Should().BeFalse("a single distant interferer must not steal CW lock");
      filter.TryPredict(Epoch.AddSeconds(3.1), out _).Should().BeFalse();
    }

    [Fact]
    public void ShortFmBreakCoastsThenReleasesResidualAndCanReacquire()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 3; i++)
        controller.Observe(new(AfcSignalMode.FM,
          Epoch.AddMilliseconds(130*i), 12, 170, 4, .96, true),
          436795000, 1, 2000, 20, 5, false);
      double locked = controller.Snapshot.CorrectionHz;
      locked.Should().BeGreaterThan(0);
      controller.Observe(new(AfcSignalMode.FM,
        Epoch.AddMilliseconds(600), 12, 0, 999, 0, false),
        436795000, 1, 2000, 20, 5, false);
      controller.Snapshot.State.Should().Be(AfcLockState.Coasting);
      controller.Snapshot.CorrectionHz.Should().Be(locked,
        "noise between FM voice bursts must not drive a CAT command");
      controller.Tick(Epoch.AddMilliseconds(1000));
      controller.Snapshot.State.Should().Be(AfcLockState.Coasting);
      controller.Tick(Epoch.AddMilliseconds(2000));
      controller.Tick(Epoch.AddMilliseconds(2800));
      controller.Snapshot.CorrectionHz.Should().BeLessThan(locked,
        "the last residual must be released if no valid FM arrives");
      controller.Observe(new(AfcSignalMode.FM,
        Epoch.AddMilliseconds(3000), 12, 40, 4, .99, true),
        436795000, 1, 2000, 20, 5, false);
      controller.Snapshot.State.Should().NotBe(AfcLockState.Fault);
    }

    [Fact]
    public void ManualPolarityAndPTTNeverAuthorizeAutomaticDirectionReversal()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 6; i++)
        controller.Observe(new(AfcSignalMode.FM,
          Epoch.AddMilliseconds(i * 150), 31, 80 + i * 70, 4, .96, true),
          436795000 + 45*i, 1, 2000, 20, 5, true,
          confirmedOwnAfcReadback: true);
      controller.Snapshot.MappingSign.Should().Be(1);
      controller.Snapshot.CorrectionHz.Should().Be(0);
      controller.Tick(Epoch.AddSeconds(10), transmitting: true);
      controller.Snapshot.CorrectionHz.Should().Be(0);
    }
  }
}
