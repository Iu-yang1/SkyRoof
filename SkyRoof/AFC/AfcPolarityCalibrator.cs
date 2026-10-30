using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Verifies the sign of an actual RX CAT/12-kHz IF response without using
  /// a guessed direction. The caller owns the single CAT writer and applies
  /// OffsetHz as a temporary, RX-only layer. This object never writes TX.
  /// </summary>
  public sealed class AfcPolarityCalibrator
  {
    public enum Phase { Waiting, Probing, Restoring, Complete, Failed }

    private long generation = -1;
    private DateTime commandSampleUtc;
    private double lastCatHz, lastIfHz;
    private int stableSamples, transitions, candidateSign;
    private double probeHz;

    public Phase State { get; private set; } = Phase.Waiting;
    public double OffsetHz { get; private set; }
    public int VerifiedSign { get; private set; }
    public bool Active => State is Phase.Probing or Phase.Restoring;
    public bool BlocksAutomaticControl => Active || State == Phase.Failed;

    public string Detail => State switch
    {
      Phase.Waiting => "Awaiting stable IF and time-aligned CAT readback",
      Phase.Probing => "Verifying RX CAT/IF direction (probe)",
      Phase.Restoring => "Verifying RX CAT/IF direction (restore)",
      Phase.Complete => "RX CAT/IF direction verified; probe restored",
      _ => "RX CAT/IF calibration failed; no automatic correction"
    };

    public void Reset()
    {
      generation = -1;
      commandSampleUtc = default;
      lastCatHz = lastIfHz = 0;
      stableSamples = transitions = candidateSign = 0;
      probeHz = OffsetHz = 0;
      VerifiedSign = 0;
      State = Phase.Waiting;
    }

    /// <summary>
    /// One probe/restore pair is not sufficient: require four independent
    /// time-aligned CAT/IF transitions (two complete probe/restore pairs).
    /// Each transition must match the commanded CAT movement, and all four
    /// must imply the same sign. Any timeout aborts with zero RX probe offset.
    /// </summary>
    public void Observe(AfcObservation observation, double catReadbackHz,
      bool confirmedProbeReadback, bool eligible, int tuningStepHz)
    {
      if (generation != observation.Generation)
      {
        Reset();
        generation = observation.Generation;
      }

      if (!eligible)
      {
        if (Active) Fail();
        return;
      }

      bool valid = observation.HasAbsoluteReference &&
        !observation.Ambiguous && observation.Confidence >= .75 &&
        double.IsFinite(observation.IfOffsetHz) &&
        double.IsFinite(catReadbackHz) &&
        observation.SigmaHz <= (observation.Mode == AfcSignalMode.FM ? 150 : 20);

      if (State == Phase.Complete || State == Phase.Failed) return;

      if (Active && (observation.SampleUtc - commandSampleUtc).TotalSeconds > 8)
      {
        Fail();
        return;
      }

      if (!valid)
      {
        if (!Active) stableSamples = 0;
        return;
      }

      if (State == Phase.Waiting)
      {
        // Do not step outside the CW/SSB target gate with coarse CAT tuning.
        int amplitude = observation.Mode == AfcSignalMode.FM ? 80 : 40;
        if (tuningStepHz <= 0 || tuningStepHz > amplitude)
        {
          Fail();
          return;
        }
        probeHz = Math.Ceiling(amplitude / (double)tuningStepHz) * tuningStepHz;
        if (++stableSamples < 3) return;
        lastCatHz = catReadbackHz;
        lastIfHz = observation.IfOffsetHz;
        OffsetHz = probeHz;
        State = Phase.Probing;
        commandSampleUtc = observation.SampleUtc;
        return;
      }

      if (observation.SampleUtc <= commandSampleUtc ||
          !confirmedProbeReadback)
        return;

      double catDelta = catReadbackHz - lastCatHz;
      double ifDelta = observation.IfOffsetHz - lastIfHz;
      double expected = State == Phase.Probing ? probeHz : -probeHz;
      double tolerance = Math.Max(20, probeHz * .4);
      if (Math.Abs(catDelta - expected) > tolerance ||
          Math.Abs(ifDelta) < 15 ||
          Math.Abs(Math.Abs(catDelta) - Math.Abs(ifDelta)) > tolerance)
        return;

      int observedSign = -Math.Sign(catDelta * ifDelta);
      if (observedSign == 0) return;
      if (candidateSign != 0 && observedSign != candidateSign)
      {
        Fail();
        return;
      }

      candidateSign = observedSign;
      transitions++;
      lastCatHz = catReadbackHz;
      lastIfHz = observation.IfOffsetHz;
      commandSampleUtc = observation.SampleUtc;

      if (State == Phase.Probing)
      {
        OffsetHz = 0;
        State = Phase.Restoring;
      }
      else if (transitions >= 4)
      {
        OffsetHz = 0;
        VerifiedSign = candidateSign;
        State = Phase.Complete;
      }
      else
      {
        OffsetHz = probeHz;
        State = Phase.Probing;
      }
    }

    private void Fail()
    {
      OffsetHz = 0; // caller must restore the normal RX model through CAT
      VerifiedSign = 0;
      State = Phase.Failed;
    }
  }
}
