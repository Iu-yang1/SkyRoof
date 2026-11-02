using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FluentAssertions;
using SkyRoof;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcSpectrumReferenceTests
  {
    private static readonly DateTime T0 =
      new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ModeSpecificTargetsResetWithoutOverwritingEachOther()
    {
      var cfg = new AfcSettings { TargetOffsetHz = 455 };
      AfcSpectrumReference.AnalysisOffset(cfg, AfcSignalMode.FM)
        .Should().Be(double.NaN);
      AfcSpectrumReference.Select(cfg, AfcSignalMode.FM, 820).Should().BeTrue();
      AfcSpectrumReference.AnalysisOffset(cfg, AfcSignalMode.FM).Should().Be(820);
      AfcSpectrumReference.AnalysisOffset(cfg, AfcSignalMode.CW).Should().Be(455);
      AfcSpectrumReference.Select(cfg, AfcSignalMode.FM, 0).Should().BeTrue();
      cfg.FmReferenceCandidateHz.Should().Be(0);
      AfcSpectrumReference.Reset(cfg, AfcSignalMode.FM);
      cfg.FmReferenceCandidateHz.Should().BeNull();
      cfg.TargetOffsetHz.Should().Be(455);
      AfcSpectrumReference.Reset(cfg, AfcSignalMode.SSB);
      cfg.TargetOffsetHz.Should().Be(0);
      AfcSpectrumReference.Select(cfg, AfcSignalMode.FM, double.NaN)
        .Should().BeFalse();
    }

    [Fact]
    public void SelectingAndResettingFmHintInvalidatesMeasurementGeneration()
    {
      using var input = new AfcIfInput();
      input.UpdateTarget(AfcSignalMode.FM, false, double.NaN);
      long first = input.Generation;
      input.UpdateTarget(AfcSignalMode.FM, false, 900);
      input.Generation.Should().BeGreaterThan(first);
      long selected = input.Generation;
      input.UpdateTarget(AfcSignalMode.FM, false, 900);
      input.Generation.Should().Be(selected);
      input.UpdateTarget(AfcSignalMode.FM, false, double.NaN);
      input.Generation.Should().BeGreaterThan(selected);
      long reset = input.Generation;
      input.UpdateTarget(AfcSignalMode.FM, false, double.NaN);
      input.Generation.Should().Be(reset);
    }

    private static float[] FmPcm(double offset)
    {
      const int fs = 48000;
      var pcm = new float[fs];
      for (int i = 0; i < fs; i++)
      {
        double t = i / (double)fs;
        double phase = 2 * Math.PI * (12000 + offset) * t +
          1.6 * Math.Sin(2 * Math.PI * 300 * t);
        pcm[i] = (float)(.35 * Math.Cos(phase));
      }
      return pcm;
    }

    [Fact]
    public void FmManualHintFiltersFirstAcquisitionButNotLaterConvergence()
    {
      var p = new AfcSignalProcessing();
      var rejected = p.Analyze(FmPcm(820), AfcSignalMode.FM,
        T0, 3, expectedOffsetHz: -2900);
      rejected.HasAbsoluteReference.Should().BeFalse();
      rejected.Ambiguous.Should().BeTrue();
      var matched = p.Analyze(FmPcm(820), AfcSignalMode.FM,
        T0.AddMilliseconds(150), 4, expectedOffsetHz: 900);
      matched.HasAbsoluteReference.Should().BeTrue();
      matched.IfOffsetHz.Should().BeApproximately(820, 40);
      var corrected = p.Analyze(FmPcm(-1100), AfcSignalMode.FM,
        T0.AddMilliseconds(300), 4, expectedOffsetHz: 900);
      corrected.HasAbsoluteReference.Should().BeTrue();
    }

    [Fact]
    public void RightClickResetsSpanAndSignalsReferenceClearWithoutFft()
    {
      Exception? failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          using var view = new AfcSpectrumView { Size = new Size(685, 230) };
          view.SetSpan(2000);
          view.SetTargetInteraction(true);
          int resets = 0, selections = 0;
          view.ReferenceResetRequested += () => resets++;
          view.TargetSelected += _ => selections++;
          MethodInfo mouse = typeof(AfcSpectrumView).GetMethod("OnMouseDown",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
          mouse.Invoke(view, new object[] {
            new MouseEventArgs(MouseButtons.Right, 1, 365, 70, 0) });
          view.SpanHz.Should().Be(12000);
          resets.Should().Be(1);
          selections.Should().Be(0);
          view.SetSpectrum(new float[512], -6000, 12000.0 / 511, null, 0);
          mouse.Invoke(view, new object[] {
            new MouseEventArgs(MouseButtons.Left, 1, 365, 70, 0) });
          selections.Should().Be(1);
        }
        catch (Exception ex) { failure = ex; }
      });
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
      if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
  }
}
