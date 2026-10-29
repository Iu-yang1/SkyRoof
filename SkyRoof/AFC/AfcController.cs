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

    public AfcSnapshot Snapshot { get { lock (sync) return state; } }

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
        if (state.Mode == mode && state.Control == control && state.State != AfcLockState.Off) return;
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
        confirmations = 0;
        state = state with { State = AfcLockState.Acquiring,
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
        confirmations = 0;
        state = state with { State = AfcLockState.Acquiring,
          Detail = "Centering selected IF signal" };
      }
    }

    public void Observe(AfcObservation obs, double catReadbackHz,
      int configuredSign, double maxCorrectionHz,
      double fmDeadbandHz, double toneDeadbandHz, bool transmitting)
    {
      lock (sync)
      {
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
          state = state with { CorrectionHz = 0, State = AfcLockState.Acquiring };
        }
        lastProcess = obs.SampleUtc;
        double deltaTime = state.LastObservationUtc == default ? 1 :
          Math.Clamp((obs.SampleUtc - state.LastObservationUtc).TotalSeconds, 0.01, 5);
        if (!obs.HasAbsoluteReference || obs.Ambiguous ||
            obs.Confidence < 0.6 || !double.IsFinite(obs.IfOffsetHz) ||
            obs.SigmaHz > (obs.Mode == AfcSignalMode.FM ? 150 : 20))
        {
          state = state with { State = AfcLockState.Hold,
            ObservedIfOffsetHz = obs.IfOffsetHz,
            Confidence = obs.Confidence, LastObservationUtc = obs.SampleUtc,
            Detail = !obs.HasAbsoluteReference && obs.Mode == AfcSignalMode.SSB
              ? "SSB relative drift assistance only; no absolute carrier"
              : "Signal ambiguous, weak, or reference absent; holding" };
          return;
        }

        // Learn sign from an actual CAT move and a persistent IF target. The
        // radio documentation specifies 12-kHz IF but not every mode's
        // frequency-axis sign. Never guess a CAT direction.
        if (havePolaritySample && configuredSign == 0 && inferredSign == 0 &&
            double.IsFinite(catReadbackHz))
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
        havePolaritySample = double.IsFinite(catReadbackHz);

        if (!anchored)
        {
          // FM is centered on the nominal 12-kHz IF. CW "Lock Here"
          // instead preserves the operator's chosen audible pitch.
          anchorIfHz = centerNext ? centerIfOffsetHz : obs.IfOffsetHz;
          anchored = true;
          confirmations = 1;
          state = state with { State = AfcLockState.Acquiring,
            ObservedIfOffsetHz = obs.IfOffsetHz,
            ErrorHz = 0, Confidence = obs.Confidence,
            LastObservationUtc = obs.SampleUtc,
            Detail = "Acquiring selected carrier" };
          return;
        }
        confirmations++;
        int sign = configuredSign == 1 || configuredSign == -1 ?
          configuredSign : inferredSign;
        double error = obs.IfOffsetHz - anchorIfHz;
        bool mayWrite = state.Control == AfcControlMode.Auto &&
          confirmations >= 3 && sign != 0 && !transmitting;
        if (obs.Mode == AfcSignalMode.SSB && !obs.HasAbsoluteReference)
          mayWrite = false;
        if (mayWrite)
        {
          double deadband = obs.Mode == AfcSignalMode.FM
            ? fmDeadbandHz : toneDeadbandHz;
          if (Math.Abs(error) >= deadband)
          {
            double step = Math.Clamp(sign * error * 0.45,
              -Math.Min(50, 50 * deltaTime),
              Math.Min(50, 50 * deltaTime));
            acceptedCorrection = Math.Clamp(acceptedCorrection + step,
              -maxCorrectionHz, maxCorrectionHz);
          }
        }
        state = state with
        {
          State = transmitting ? AfcLockState.Hold :
            confirmations >= 3 ? AfcLockState.Locked : AfcLockState.Acquiring,
          MappingSign = sign,
          ObservedIfOffsetHz = obs.IfOffsetHz,
          ErrorHz = error, CorrectionHz = acceptedCorrection,
          Confidence = obs.Confidence, LastObservationUtc = obs.SampleUtc,
          Detail = transmitting ? "PTT active: AFC frozen" :
            sign == 0 && state.Control == AfcControlMode.Auto
              ? "Learning IF/CAT polarity from observed tuning"
              : "Tracking reference"
        };
      }
    }

    public double GetRxCorrection(bool transmitting)
    {
      lock (sync)
      {
        if (transmitting || state.Control != AfcControlMode.Auto ||
            state.State != AfcLockState.Locked)
          return state.Control == AfcControlMode.Auto ? acceptedCorrection : 0;
        return state.CorrectionHz;
      }
    }

    public void Tick(DateTime nowUtc)
    {
      lock (sync)
      {
        if (state.Control == AfcControlMode.Off || state.LastObservationUtc == default)
          return;
        double age = (nowUtc - state.LastObservationUtc).TotalSeconds;
        if (age > 8)
          state = state with { State = AfcLockState.Lost,
            Detail = "IF input stale; automatic tuning stopped" };
        else if (age > 3 && state.State == AfcLockState.Locked)
          state = state with { State = AfcLockState.Hold,
            Detail = "No fresh IF measurement; holding correction" };
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
        havePolaritySample = false;
        inferredSign = 0;
        polarityCandidate = polarityVotes = 0;
        state = state with { State = AfcLockState.Acquiring, CorrectionHz = 0,
          Detail = "Manual tune: AFC target rebased" };
        confirmations = 0;
      }
    }
  }
}
