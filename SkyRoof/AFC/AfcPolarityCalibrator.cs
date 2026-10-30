using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// A temporary receive-only CAT/IF calibration. No direction is guessed:
  /// a deliberate RX step and its independently acknowledged return must
  /// produce opposite IF movements. The caller owns the sole CAT writer.
  /// </summary>
  public sealed class AfcPolarityCalibrator
  {
    public enum Phase { Waiting, Probing, Restoring, Complete, Failed }

    private long generation = -1;
    private DateTime phaseStartedUtc, baselineSampleUtc, probedSampleUtc;
    private double baselineCatHz, baselineIfHz;
    private double probedCatHz, probedIfHz;
    private double previousWaitingIfHz = double.NaN;
    private int stableSamples, phaseVotes, candidateSign;
    private double probeHz;
    private string failureReason = "";

    public Phase State { get; private set; } = Phase.Waiting;
    public DateTime PhaseStartedUtc => phaseStartedUtc;
    public double OffsetHz { get; private set; }
    public int VerifiedSign { get; private set; }
    public bool Active => State is Phase.Probing or Phase.Restoring;
    public bool BlocksAutomaticControl => State != Phase.Complete;

    public string Detail => State switch
    {
      Phase.Waiting => "Waiting for three stable IF samples and aligned RX CAT readback",
      Phase.Probing => "RX probe: waiting for settled IF and verified CAT readback",
      Phase.Restoring => "RX restore: verifying opposite IF movement and CAT return",
      Phase.Complete => "RX CAT/IF polarity verified; original RX frequency restored",
      _ => $"RX polarity calibration failed: {failureReason}"
    };

    public void Reset()
    {
      generation = -1;
      phaseStartedUtc = baselineSampleUtc = probedSampleUtc = default;
      baselineCatHz = baselineIfHz = probedCatHz = probedIfHz = 0;
      previousWaitingIfHz = double.NaN;
      stableSamples = phaseVotes = candidateSign = 0;
      probeHz = OffsetHz = 0;
      failureReason = "";
      VerifiedSign = 0;
      State = Phase.Waiting;
    }

    /// <summary>
    /// IF SampleUtc represents the CENTER of the measured PCM window.
    /// A CAT command is posted on the UI thread later than that sample.
    /// Therefore evidence is only accepted once the entire IF window
    /// starts after the actual command time, and its CAT readback is
    /// independently confirmed at the IF sample timestamp.
    /// </summary>
    public void Observe(AfcObservation observation, double catReadbackHz,
      bool confirmedProbeReadback, bool eligible, int tuningStepHz,
      DateTime nowUtc, double predictedCatRateHzPerSecond = 0)
    {
      if (nowUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("AFC calibration clock must be UTC");
      if (generation != observation.Generation)
      {
        Reset();
        generation = observation.Generation;
      }
      if (State is Phase.Complete or Phase.Failed) return;
      // Use the independent satellite model's RX tuning rate to remove the
      // expected CAT movement between samples. Never infer direction from
      // normal satellite Doppler tracking itself.
      double catRate = double.IsFinite(predictedCatRateHzPerSecond)
        ? Math.Clamp(predictedCatRateHzPerSecond, -500, 500) : 0;

      if (!eligible)
      {
        if (Active) Fail("RX tracking/PTT changed during calibration");
        else stableSamples = 0;
        return;
      }

      if (Active && (nowUtc - phaseStartedUtc).TotalSeconds > 15)
      {
        Fail(State == Phase.Probing
          ? "CAT probe readback or settled IF response timed out"
          : "CAT restoration readback or settled IF response timed out");
        return;
      }

      bool valid = observation.HasAbsoluteReference &&
        !observation.Ambiguous && observation.Confidence >= .7 &&
        double.IsFinite(observation.IfOffsetHz) &&
        double.IsFinite(catReadbackHz) &&
        observation.SigmaHz <= (observation.Mode == AfcSignalMode.FM ? 150 : 20);
      if (!valid)
      {
        if (!Active) stableSamples = 0;
        return;
      }

      if (State == Phase.Waiting)
      {
        double maxDrift = observation.Mode == AfcSignalMode.FM ? 60 : 15;
        if (double.IsFinite(previousWaitingIfHz) &&
            Math.Abs(previousWaitingIfHz - observation.IfOffsetHz) <= maxDrift)
          stableSamples++;
        else
          stableSamples = 1;
        previousWaitingIfHz = observation.IfOffsetHz;
        if (stableSamples < 3) return;

        // A stronger FM test step makes the response identifiable even when
        // the finite FM discriminator window has a modest modulation bias.
        int amplitude = observation.Mode == AfcSignalMode.FM ? 150 : 40;
        if (tuningStepHz <= 0 || tuningStepHz > amplitude)
        {
          Fail("CAT tuning step is too coarse for a safe IF probe");
          return;
        }
        probeHz = Math.Ceiling(amplitude / (double)tuningStepHz) * tuningStepHz;
        baselineCatHz = catReadbackHz;
        baselineIfHz = observation.IfOffsetHz;
        baselineSampleUtc = observation.SampleUtc;
        OffsetHz = probeHz;
        phaseStartedUtc = nowUtc;
        phaseVotes = candidateSign = 0;
        State = Phase.Probing;
        return;
      }

      // For FM this is a 2-second PCM window, centered at SampleUtc.
      // Requiring the center to be >= 1.2 s after the command means
      // the entire effective averaging window follows the CAT move.
      double settlingSeconds = observation.Mode == AfcSignalMode.FM ? 1.2 : .3;
      if (observation.SampleUtc < phaseStartedUtc.AddSeconds(settlingSeconds) ||
          !confirmedProbeReadback)
        return;

      double expectedStep = State == Phase.Probing ? probeHz : -probeHz;
      double fromCat = State == Phase.Probing ? baselineCatHz : probedCatHz;
      double fromIf = State == Phase.Probing ? baselineIfHz : probedIfHz;
      DateTime fromUtc = State == Phase.Probing
        ? baselineSampleUtc : probedSampleUtc;
      double expectedModelDelta =
        catRate * (observation.SampleUtc - fromUtc).TotalSeconds;
      double catDelta = catReadbackHz - fromCat - expectedModelDelta;
      double ifDelta = observation.IfOffsetHz - fromIf;
      double catTolerance = Math.Max(25, probeHz * .35);
      double ifTolerance = observation.Mode == AfcSignalMode.FM
        ? Math.Max(85, probeHz * .65)
        : Math.Max(20, probeHz * .5);

      // A readback must show the actual commanded CAT step, not merely a
      // request. Doppler model changes are tolerated within a bounded range.
      if (Math.Abs(catDelta - expectedStep) > catTolerance ||
          Math.Abs(ifDelta) < Math.Max(15, probeHz * .25) ||
          Math.Abs(Math.Abs(ifDelta) - Math.Abs(catDelta)) > ifTolerance)
      {
        phaseVotes = 0;
        return;
      }

      int sign = -Math.Sign(catDelta * ifDelta);
      if (sign == 0 || candidateSign != 0 && sign != candidateSign)
      {
        Fail("CAT/IF direction reversed or inconsistent");
        return;
      }

      candidateSign = sign;
      // Two independent settled IF samples for each of the two CAT edges.
      if (++phaseVotes < 2) return;
      phaseVotes = 0;

      if (State == Phase.Probing)
      {
        probedCatHz = catReadbackHz;
        probedIfHz = observation.IfOffsetHz;
        probedSampleUtc = observation.SampleUtc;
        OffsetHz = 0;
        State = Phase.Restoring;
        phaseStartedUtc = nowUtc;
        return;
      }

      // Never publish the sign until the original RX was actually restored.
      double returnTolerance = observation.Mode == AfcSignalMode.FM ? 85 : 20;
      double restoredCatDelta = catReadbackHz - baselineCatHz -
        catRate * (observation.SampleUtc - baselineSampleUtc).TotalSeconds;
      if (Math.Abs(restoredCatDelta) > catTolerance ||
          Math.Abs(observation.IfOffsetHz - baselineIfHz) > returnTolerance)
      {
        phaseVotes = 0;
        return;
      }
      OffsetHz = 0;
      VerifiedSign = candidateSign;
      State = Phase.Complete;
    }

    /// <summary>
    /// The UI timer also enforces the deadline if there are no IF callbacks.
    /// Return the temporary RX layer to zero on any failure.
    /// </summary>
    public void Watchdog(DateTime nowUtc, bool eligible)
    {
      if (!Active) return;
      if (!eligible)
      {
        Fail("PTT, target change, or lost signal lock");
        return;
      }
      if ((nowUtc - phaseStartedUtc).TotalSeconds > 15)
        Fail(State == Phase.Probing
          ? "CAT probe not acknowledged by a settled IF measurement"
          : "CAT restore not acknowledged by a settled IF measurement");
    }

    private void Fail(string reason)
    {
      failureReason = reason;
      OffsetHz = 0;
      VerifiedSign = 0;
      State = Phase.Failed;
    }
  }
}
