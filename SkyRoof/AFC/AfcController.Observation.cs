using System;

namespace SkyRoof.AFC
{
  public sealed partial class AfcController
  {
    // Called only while Observe owns sync, preserving the existing state
    // transitions and time-aligned CAT/IF evidence ordering.
    private bool BeginObservation(AfcObservation obs)
    {
      if (state.Control == AfcControlMode.Off)
      {
        state = state with { State = AfcLockState.Off, CorrectionHz = 0,
          Detail = "AFC disabled" };
        return false;
      }
      if (obs.Mode != state.Mode || obs.SampleUtc <= lastProcess)
        return false;
      if (generation != obs.Generation)
      {
        generation = obs.Generation;
        anchored = false;
        confirmations = 0;
        havePolaritySample = false;
        inferredSign = 0;
        polarityCandidate = polarityVotes = 0;
        acceptedCorrection = 0;
        previousConfirmedError = previousConfirmedCatHz = double.NaN;
        divergenceVotes = 0;
        previousConfirmedIfHz = double.NaN;
        predictor.Reset();
        weakFmGate.Reset();
        recoveryUntilUtc = default;
        waitForManualSign = false;
        state = state with { CorrectionHz = 0, State = AfcLockState.Acquiring };
      }
      lastProcess = obs.SampleUtc;
      return true;
    }

    private bool TryAcceptMeasurement(AfcObservation obs, out bool corroboratedFm)
    {
      corroboratedFm = weakFmGate.Observe(obs);
      double verifiedQuality = corroboratedFm
        ? Math.Max(.62, obs.Confidence) : obs.Confidence;
      bool reliable = obs.HasAbsoluteReference && !obs.Ambiguous &&
        (obs.Confidence >= .6 || corroboratedFm) &&
        double.IsFinite(obs.IfOffsetHz) && double.IsFinite(obs.SigmaHz) &&
        obs.SigmaHz <= (obs.Mode == AfcSignalMode.FM ? 150 : 20);
      if (!reliable || !predictor.Update(obs.IfOffsetHz,
          obs.SigmaHz, verifiedQuality, obs.SampleUtc))
      {
        bool coasting = predictor.TryPredict(obs.SampleUtc, out double forecast);
        state = state with
        {
          State = coasting ? AfcLockState.Coasting : AfcLockState.Hold,
          ObservedIfOffsetHz = coasting ? forecast : obs.IfOffsetHz,
          ErrorHz = coasting && anchored ? forecast - anchorIfHz : state.ErrorHz,
          Confidence = obs.Confidence,
          LastObservationUtc = obs.SampleUtc,
          Detail = coasting ? "Short signal gap: bounded residual prediction" :
            !obs.HasAbsoluteReference && obs.Mode == AfcSignalMode.SSB
              ? "SSB relative drift assistance only; no absolute carrier" :
              "Signal ambiguous, weak, or reference absent; holding"
        };
        // No new CAT correction is based only on a predicted signal or
        // on an uncorroborated 500-Hz jump. Satellite Doppler still runs.
        return false;
      }
      return true;
    }

    private void UpdatePolarityEvidence(AfcObservation obs, int configuredSign,
      bool catReadbackIsTimeAligned, double catReadbackHz)
    {
      if (havePolaritySample && configuredSign == 0 && inferredSign == 0 &&
          catReadbackIsTimeAligned && double.IsFinite(catReadbackHz))
      {
        double catDelta = catReadbackHz - lastCatHz;
        double ifDelta = obs.IfOffsetHz - lastIfHz;
        if (Math.Abs(catDelta) >= 30 &&
            Math.Abs(ifDelta) >= 15 &&
            Math.Abs(Math.Abs(catDelta) - Math.Abs(ifDelta)) <
              Math.Max(25, Math.Abs(catDelta) * 0.4))
        {
          int candidate = -Math.Sign(ifDelta * catDelta);
          if (candidate != polarityCandidate)
          {
            polarityCandidate = candidate;
            polarityVotes = 1;
          }
          else if (++polarityVotes >= 3)
            inferredSign = candidate;
        }
      }
      lastIfHz = obs.IfOffsetHz;
      lastCatHz = catReadbackHz;
      havePolaritySample = catReadbackIsTimeAligned && double.IsFinite(catReadbackHz);
    }

    private bool TryHandleDivergence(AfcObservation obs,
      AfcObservationContext context, int sign, double error)
    {
      double catReadbackHz = context.CatReadbackHz;
      int configuredSign = context.ConfiguredSign;
      bool catReadbackIsTimeAligned = context.CatReadbackIsTimeAligned;
      bool confirmedOwnAfcReadback = context.ConfirmedOwnAfcReadback;
      if (confirmedOwnAfcReadback && catReadbackIsTimeAligned &&
          double.IsFinite(catReadbackHz) &&
          (!double.IsFinite(previousConfirmedCatHz) ||
           Math.Abs(catReadbackHz - previousConfirmedCatHz) >= 8))
      {
        double absError = Math.Abs(error);
        if (double.IsFinite(previousConfirmedError))
        {
          bool worsening = absError > previousConfirmedError +
            Math.Max(10, previousConfirmedError * .2);
          divergenceVotes = worsening ? divergenceVotes + 1 :
            Math.Max(0, divergenceVotes - 1);
        }
        if (configuredSign == 0 &&
            double.IsFinite(previousConfirmedCatHz) &&
            double.IsFinite(previousConfirmedIfHz))
        {
          double catDelta = catReadbackHz - previousConfirmedCatHz;
          double ifDelta = obs.IfOffsetHz - previousConfirmedIfHz;
          if (Math.Abs(catDelta) >= 20 && Math.Abs(ifDelta) >= 12 &&
              Math.Abs(Math.Abs(catDelta) - Math.Abs(ifDelta)) <
                Math.Max(30, Math.Abs(catDelta) * .6))
          {
            int observedSign = -Math.Sign(catDelta * ifDelta);
            if (observedSign == polarityCandidate) polarityVotes++;
            else { polarityCandidate = observedSign; polarityVotes = 1; }
            if (polarityVotes >= 2) inferredSign = observedSign;
          }
        }
        previousConfirmedCatHz = catReadbackHz;
        previousConfirmedIfHz = obs.IfOffsetHz;
        previousConfirmedError = absError;
        if (divergenceVotes >= 3)
        {
          acceptedCorrection *= .5;
          divergenceVotes = 0;
          previousConfirmedError = previousConfirmedCatHz =
            previousConfirmedIfHz = double.NaN;
          recoveryUntilUtc = obs.SampleUtc.AddMilliseconds(450);
          waitForManualSign = configuredSign is 1 or -1;
          state = state with
          {
            State = AfcLockState.Coasting,
            MappingSign = sign,
            CorrectionHz = acceptedCorrection,
            ObservedIfOffsetHz = obs.IfOffsetHz,
            ErrorHz = error,
            Confidence = obs.Confidence,
            LastObservationUtc = obs.SampleUtc,
            Detail = waitForManualSign
              ? "Manual RF/IF sign conflicts with AFC readback; backtracking"
              : inferredSign != 0 && inferredSign != sign
                ? "AFC divergent: verified sign reversed, backtracking"
                : "AFC divergent: bounded rollback and reacquiring"
          };
          return true;
        }
      }
      return false;
    }

  }
}
