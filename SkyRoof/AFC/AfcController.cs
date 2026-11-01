using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// A deterministic Doppler-residual servo. Only its composed RX correction
  /// is ever used; it never owns CAT TX, RIT, or saved transponder offsets.
  /// Tune commands are applied by FrequencyWidget's single CAT writer.
  /// </summary>
  public sealed class AfcController
  {
    private readonly object sync = new();
    private AfcSnapshot state = new(
      AfcLockState.Off, AfcSignalMode.FM, AfcControlMode.Off,
      0, 0, 0, 0, 0, default, "AFC disabled");
    private double anchorIfHz;
    private bool anchored;
    private bool centerNext;
    private double centerIfOffsetHz;
    private int confirmations;
    private DateTime lastProcess;
    private long generation = -1;
    private double lastIfHz, lastCatHz;
    private bool havePolaritySample;
    private int inferredSign;
    private int polarityCandidate, polarityVotes;
    private double acceptedCorrection;
    private double previousConfirmedError = double.NaN;
    private double previousConfirmedCatHz = double.NaN;
    private int divergenceVotes;
    private double previousConfirmedIfHz = double.NaN;
    private DateTime lastDecayTickUtc;
    private DateTime recoveryUntilUtc;
    private bool waitForManualSign;
    private AfcBurstPredictor predictor = new();
    private readonly AfcFmEvidenceGate weakFmGate = new();
    private AfcSignalLossPolicy lossPolicy = AfcSignalLossPolicy.HoldLast;

    public AfcSnapshot Snapshot { get { lock (sync) return state; } }

    public void ConfigureSignalLoss(AfcSignalLossPolicy policy)
    {
      if (!Enum.IsDefined(typeof(AfcSignalLossPolicy), policy))
        throw new ArgumentOutOfRangeException(nameof(policy));
      lock (sync) lossPolicy = policy; // Do not reset active AFC correction.
    }

    public void Reset(string reason = "AFC target reset")
    {
      lock (sync)
      {
        state = state with
        {
          State = AfcLockState.Off, Control = AfcControlMode.Off, CorrectionHz = 0,
          ErrorHz = 0, Confidence = 0, Detail = reason
        };
        anchored = false;
        centerNext = false;
        confirmations = 0;
        generation = -1;
        havePolaritySample = false;
        inferredSign = 0;
        polarityCandidate = polarityVotes = 0;
        acceptedCorrection = 0;
        previousConfirmedError = previousConfirmedCatHz = double.NaN;
        divergenceVotes = 0;
        previousConfirmedIfHz = double.NaN;
        predictor.Reset();
        weakFmGate.Reset();
        recoveryUntilUtc = lastDecayTickUtc = default;
        waitForManualSign = false;
        lastProcess = default;
      }
    }

    /// <summary>
    /// The last correction remains on shutdown by default only while the
    /// service is active; stopping Auto freezes writes and resets the volatile
    /// AFC layer without touching user satellite settings.
    /// </summary>
    public void Configure(AfcSignalMode mode, AfcControlMode control)
    {
      lock (sync)
      {
        if (state.Mode == mode && state.State != AfcLockState.Off &&
            state.Control != AfcControlMode.Off && control != AfcControlMode.Off)
        {
          if (state.Control == control) return;
          // Assist->Auto retains the already observed IF/CAT polarity
          // from the same input timeline and radio mode, but discards
          // all prior correction and reacquires the chosen pitch.
          state = state with
          {
            Control = control, State = AfcLockState.Acquiring,
            CorrectionHz = 0, ErrorHz = 0,
            Detail = "Control level changed; reacquiring target"
          };
          anchored = false;
          centerNext = mode != AfcSignalMode.CW;
          centerIfOffsetHz = 0;
          acceptedCorrection = 0;
          previousConfirmedError = previousConfirmedCatHz = double.NaN;
          divergenceVotes = 0;
          previousConfirmedIfHz = double.NaN;
          predictor.Reset();
          weakFmGate.Reset();
          recoveryUntilUtc = default;
          waitForManualSign = false;
          confirmations = 0;
          return;
        }
        if (state.Mode == mode && state.Control == control &&
            state.State != AfcLockState.Off) return;
        state = state with
        {
          Mode = mode, Control = control, State = AfcLockState.Acquiring,
          CorrectionHz = 0, ErrorHz = 0, Confidence = 0,
          LastObservationUtc = default,
          Detail = "Awaiting fresh IF signal"
        };
        anchored = false;
        centerNext = mode != AfcSignalMode.CW;
        centerIfOffsetHz = 0;
        confirmations = 0;
        generation = -1;
        havePolaritySample = false;
        inferredSign = 0;
        acceptedCorrection = 0;
        previousConfirmedError = previousConfirmedCatHz = double.NaN;
        divergenceVotes = 0;
        previousConfirmedIfHz = double.NaN;
        weakFmGate.Reset();
        predictor = new AfcBurstPredictor(mode == AfcSignalMode.FM ? 1.5 :
          mode == AfcSignalMode.CW ? 2.5 : 2.0,
          mode == AfcSignalMode.FM ? 90 : mode == AfcSignalMode.CW ? 35 : 30);
        recoveryUntilUtc = lastDecayTickUtc = default;
        waitForManualSign = false;
        lastProcess = default;
      }
    }

    /// <summary>Follow the target's current pitch, rather than forcing it to 0 Hz.</summary>
    public void LockHere()
    {
      lock (sync)
      {
        anchored = false;
        centerNext = false;
        previousConfirmedError = previousConfirmedCatHz = double.NaN;
        divergenceVotes = 0;
        confirmations = 0;
        predictor.Reset();
        weakFmGate.Reset();
        recoveryUntilUtc = default;
        waitForManualSign = false;
        if (state.State == AfcLockState.Fault)
        {
          inferredSign = 0;
          havePolaritySample = false;
          acceptedCorrection = 0;
        }
        state = state with { State = AfcLockState.Acquiring,
          CorrectionHz = acceptedCorrection,
          Detail = "Locking currently observed IF frequency" };
      }
    }

    public void CenterOn(double desiredIfOffsetHz)
    {
      if (!double.IsFinite(desiredIfOffsetHz)) return;
      lock (sync)
      {
        centerNext = true;
        centerIfOffsetHz = desiredIfOffsetHz;
        anchored = false;
        previousConfirmedError = previousConfirmedCatHz = double.NaN;
        divergenceVotes = 0;
        confirmations = 0;
        predictor.Reset();
        recoveryUntilUtc = default;
        waitForManualSign = false;
        if (state.State == AfcLockState.Fault)
        {
          inferredSign = 0;
          havePolaritySample = false;
          acceptedCorrection = 0;
        }
        state = state with { State = AfcLockState.Acquiring,
          CorrectionHz = acceptedCorrection,
          Detail = "Centering selected IF signal" };
      }
    }

    /// <summary>
    /// Accept a direction only after an independent, closed-loop CAT/IF
    /// calibration has verified both probe and restore readbacks.
    /// </summary>
    public void ConfirmCalibratedSign(int sign)
    {
      if (sign != 1 && sign != -1) return;
      lock (sync)
      {
        if (state.State == AfcLockState.Fault) return;
        inferredSign = sign;
        polarityCandidate = sign;
        polarityVotes = 3;
        state = state with { MappingSign = sign };
      }
    }

    public void Observe(AfcObservation obs, double catReadbackHz,
      int configuredSign, double maxCorrectionHz,
      double fmDeadbandHz, double toneDeadbandHz, bool transmitting,
      bool catReadbackIsTimeAligned = true,
      bool confirmedOwnAfcReadback = false,
      bool calibrationInProgress = false,
      int nominalSign = 0)
    {
      lock (sync)
      {
        // Divergence is recoverable. Never latch Fault while leaving an
        // old nonzero AFC correction attached to the Doppler model.
        if (state.Control == AfcControlMode.Off)
        {
          state = state with { State = AfcLockState.Off, CorrectionHz = 0,
            Detail = "AFC disabled" };
          return;
        }
        if (obs.Mode != state.Mode || obs.SampleUtc <= lastProcess)
          return;
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
        double deltaTime = state.LastObservationUtc == default ? 1 :
          Math.Clamp((obs.SampleUtc - state.LastObservationUtc).TotalSeconds, 0.01, 5);
        // Weak FM may be recognized from successive consistent narrow-
        // sigma observations, without globally weakening the CW/SSB gate.
        // Each new epoch must contain new PCM (>=80 ms); overlapping
        // windows are NOT claimed to be independent measurements.
        bool corroboratedFm = weakFmGate.Observe(obs);
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
          return;
        }

        // Learn sign from an actual CAT move and a persistent IF target. The
        // radio documentation specifies 12-kHz IF but not every mode's
        // frequency-axis sign. Never guess a CAT direction.
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

        if (!anchored)
        {
          // FM is centered on the nominal 12-kHz IF. CW "Lock Here"
          // instead preserves the operator's chosen audible pitch.
          anchorIfHz = centerNext ? centerIfOffsetHz : obs.IfOffsetHz;
          anchored = true;
          confirmations = 1;
          state = state with { State = AfcLockState.Acquiring,
            ObservedIfOffsetHz = obs.IfOffsetHz,
            ErrorHz = obs.IfOffsetHz - anchorIfHz, Confidence = obs.Confidence,
            LastObservationUtc = obs.SampleUtc,
            Detail = "Acquiring selected carrier" };
          return;
        }
        confirmations++;
        int sign = configuredSign is 1 or -1 ? configuredSign :
          inferredSign is 1 or -1 ? inferredSign : nominalSign;
        double error = predictor.OffsetHz - anchorIfHz;
        // Compare only verified AFC-authored CAT readbacks with independent
        // IF samples. Satellite-model-only movement is NOT a polarity test.
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
            return;
          }
        }
        bool mayWrite = state.Control == AfcControlMode.Auto &&
          confirmations >= 2 && sign != 0 && !transmitting &&
          !calibrationInProgress && !waitForManualSign &&
          obs.SampleUtc >= recoveryUntilUtc;
        if (obs.Mode == AfcSignalMode.SSB && !obs.HasAbsoluteReference)
          mayWrite = false;
        if (mayWrite)
        {
          double deadband = obs.Mode == AfcSignalMode.FM
            ? fmDeadbandHz : toneDeadbandHz;
          if (Math.Abs(error) >= deadband)
          {
            double step = Math.Clamp(sign * error * 0.55,
              -Math.Min(120, 250 * deltaTime),
              Math.Min(120, 250 * deltaTime));
            acceptedCorrection = Math.Clamp(acceptedCorrection + step,
              -maxCorrectionHz, maxCorrectionHz);
          }
        }
        state = state with
        {
          State = transmitting ? AfcLockState.Hold :
            waitForManualSign || obs.SampleUtc < recoveryUntilUtc ?
              AfcLockState.Coasting :
            confirmations >= 2 ? AfcLockState.Locked : AfcLockState.Acquiring,
          MappingSign = sign,
          ObservedIfOffsetHz = obs.IfOffsetHz,
          ErrorHz = error, CorrectionHz = acceptedCorrection,
          Confidence = obs.Confidence, LastObservationUtc = obs.SampleUtc,
          Detail = transmitting ? "PTT active: AFC frozen" :
            waitForManualSign ? "Manual RF/IF sign requires review" :
            obs.SampleUtc < recoveryUntilUtc ? "Divergence recovery: verifying new IF" :
            sign == 0 && state.Control == AfcControlMode.Auto
              ? "Awaiting nominal RF/IF mapping"
              : "Tracking reference"
        };
      }
    }

    public double GetRxCorrection(bool transmitting)
    {
      lock (sync)
        return state.Control == AfcControlMode.Auto ? acceptedCorrection : 0;
    }

    public void Tick(DateTime nowUtc, bool transmitting = false)
    {
      lock (sync)
      {
        if (state.Control == AfcControlMode.Off || !predictor.HasEstimate)
          return;
        double dt = lastDecayTickUtc == default ? 0 :
          Math.Clamp((nowUtc - lastDecayTickUtc).TotalSeconds, 0, 1);
        lastDecayTickUtc = nowUtc;
        double age = (nowUtc - predictor.LastValidUtc).TotalSeconds;
        if (age < 0) return;
        if (waitForManualSign)
        {
          // A persistently wrong manual RF/IF sign must not leave the old
          // offset attached while continuous FM prevents the stale timeout.
          if (!transmitting && dt > 0)
          {
            double release = 160 * dt;
            acceptedCorrection = Math.Abs(acceptedCorrection) <= release ? 0 :
              acceptedCorrection - Math.CopySign(release, acceptedCorrection);
          }
          state = state with
          {
            State = AfcLockState.Coasting, CorrectionHz = acceptedCorrection,
            Detail = "Manual RF/IF polarity conflict: releasing correction"
          };
          return;
        }
        double horizon = state.Mode == AfcSignalMode.FM ? 1.5 :
          state.Mode == AfcSignalMode.CW ? 2.5 : 2.0;
        if (age >= .6 && age <= horizon &&
            predictor.TryPredict(nowUtc, out double predicted))
        {
          state = state with
          {
            State = AfcLockState.Coasting,
            ObservedIfOffsetHz = predicted,
            ErrorHz = anchored ? predicted - anchorIfHz : state.ErrorHz,
            Detail = "Short signal gap: bounded residual prediction"
          };
        }
        else if (age > horizon)
        {
          // Missing FM speech, CW key-up and QSB are not evidence that the
          // last measured residual was wrong. The orbital Doppler model
          // continues to move the radio, while the bounded AFC RX bias
          // remains unchanged by default. No CAT command is generated
          // by prediction or by a noise-only window.
          bool optionalDecay = lossPolicy == AfcSignalLossPolicy.GentleRelease &&
            age > 30 && !transmitting && dt > 0;
          if (optionalDecay)
          {
            double release = 10 * dt; // explicitly selected, gentle policy
            acceptedCorrection = Math.Abs(acceptedCorrection) <= release ? 0 :
              acceptedCorrection - Math.CopySign(release, acceptedCorrection);
          }
          state = state with
          {
            State = age >= horizon + 2 ? AfcLockState.Lost : AfcLockState.Hold,
            CorrectionHz = acceptedCorrection,
            Detail = optionalDecay
              ? "No carrier: optional gentle residual release after 30s"
              : "No carrier: holding last verified AFC bias; Doppler still running"
          };
        }
      }
    }

    public void OnUserTune()
    {
      // A user knob command changes the reference target; do not fight it.
      lock (sync)
      {
        anchored = false;
        centerNext = false;
        acceptedCorrection = 0;
        predictor.Reset();
        weakFmGate.Reset();
        waitForManualSign = false;
        recoveryUntilUtc = default;
        // Preserve polarity evidence through routine hand tuning: those
        // known CAT/IF motions are precisely the observations that let
        // automatic sign inference work without a separate calibration UI.
        // Switching PCM generation or mode still clears this evidence.
        state = state with { State = AfcLockState.Acquiring, CorrectionHz = 0,
          Detail = "Manual tune: AFC target rebased" };
        confirmations = 0;
      }
    }
  }
}
