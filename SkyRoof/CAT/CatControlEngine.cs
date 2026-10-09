using System.Globalization;
using Serilog;
using SkyRoof.Properties;
using VE3NEA;

namespace SkyRoof
{
  public enum OperatingMode { RxOnly, TxOnly, Simplex, Split, Duplex }

  public class CatControlEngine : ControlEngine
  {
    const long NOT_ASSIGNED = 0;

    private bool rx, tx, crossband;
    private OperatingMode CatMode;
    private RadioCapabilities RadioCapabilities;
    private AvailableCommands Caps;
    private readonly int TuningStep;
    private readonly bool IgnoreDialKnob;

    public bool Ptt { get; private set; } = false;
    private bool PttChanged = false;

    // Ownership is deliberately separate from the observed PTT state. A second
    // client (for example WSJT-X) may key the same radio; shutting down SkyRoof
    // must only release PTT that SkyRoof itself successfully asserted.
    private volatile bool PttOwnedByApplication;
    // A PTT-ON write can time out after the radio has already acted. Keep the
    // attempt separate from confirmed ownership so shutdown/key release can
    // still fail safe until hardware readback resolves the ambiguity.
    private volatile bool PttOnAttempted;
    internal bool PttMayBeOwnedByApplication =>
      PttOwnedByApplication || PttOnAttempted;

    private readonly ManualResetEventSlim PttReleased = new(true);
    private bool DialKnobSpinning = false;
    public long RequestedRxFrequency, LastWrittenRxFrequency, LastReadRxFrequency;
    public long RequestedTxFrequency, LastWrittenTxFrequency, LastReadTxFrequency;
    private Slicer.Mode? RequestedRxMode, LastWrittenRxMode;      
    private Slicer.Mode? RequestedTxMode, LastWrittenTxMode;
    private bool? RequestedPtt;

    // CTCSS encode state requested by the UI. CtcssEnabled is null until the app sets it,
    // CtcssPending is true while the state still has to be written to the radio
    private double CtcssTone = CtcssTones.DEFAULT_TONE;
    private bool? CtcssEnabled;
    private bool CtcssPending;
    // One-shot guard for transmitter changes. IC-9700/SkyCAT may swap Main/Sub while
    // applying a new cross-band frequency pair; reassert CTCSS only after those tune
    // writes have settled so the encoder ends up on the final TX/Sub side.
    private bool CtcssReassertAfterTune;
    private double? RequestedArmingTone;

    // One-shot request used by the native Icom LAN Spectrum panel. The actual CI-V
    // command is sent by SkyCAT over the already-open CAT/virtual-serial path, so this
    // does not create a second Icom LAN session.
    private volatile bool IcomScopeOutputPending;
    private readonly IcomScopeCommandQueue IcomScopeControlCommands =
      new(capacity: 32);
    private volatile bool IcomScopeReadbackPending;
    private static readonly string[] IcomScopeReadbackFields =
    {
      "SELECT",
      "MAIN.MODE", "MAIN.SPAN", "MAIN.EDGE", "MAIN.REF",
      "MAIN.SPEED", "MAIN.VBW",
      "SUB.MODE", "SUB.SPAN", "SUB.EDGE", "SUB.REF",
      "SUB.SPEED", "SUB.VBW",
      "TX", "CENTER", "MARKER"
    };
    private readonly Dictionary<string, string> IcomScopeReadbackValues = new(
      StringComparer.OrdinalIgnoreCase);
    private int IcomScopeReadbackIndex;
    private readonly object IcomScopeReadbackSync = new();
    private int IcomScopeReadbackGeneration;
    private volatile bool IcomRfGainReadbackPending;
    private int RequestedIcomRfGain = -1;
    internal bool SupportsIcomRfGain =>
      SupportsIcomScopeOutput &&
      string.Equals(RadioCapabilities?.model, "IC-9700",
        StringComparison.OrdinalIgnoreCase);
    internal event Action<int>? IcomRfGainReadbackReceived;
    private IcomFixedEdgeReadbackRequest? IcomFixedEdgeReadbackPending;

    private sealed class IcomFixedEdgeReadbackRequest
    {
      internal int FrequencyRange { get; init; }
      internal int EdgeNumber { get; init; }
    }

    internal int PendingIcomScopeControlCount =>
      IcomScopeControlCommands.Count;

    internal long DroppedIcomScopeControlCount =>
      IcomScopeControlCommands.DroppedCount;

    private long RejectedIcomScopeControlCountValue;

    internal long RejectedIcomScopeControlCount =>
      Interlocked.Read(
        ref RejectedIcomScopeControlCountValue);

    public event EventHandler? RxTuned;
    public event EventHandler? TxTuned;
    internal event Action<IcomScopeReadbackState>?
      IcomScopeReadbackReceived;
    internal event Action<IcomFixedEdgeReadbackState>?
      IcomFixedEdgeReadbackReceived;

    public CatControlEngine(CatRadioSettings radioSettings, CatSettings catSettings) : base(radioSettings.Host, radioSettings.Port, catSettings)
    {
      TuningStep = catSettings.TuningStep;
      IgnoreDialKnob = catSettings.IgnoreDialKnob;
    }

    private void LogInfo(string msg)
    {
      if (log) Log.Information(msg);
    }

    private void LogFreqs(string msg)
    {
      LogInfo($"{msg}  (RxReq={RequestedRxFrequency}  RxWr={LastWrittenRxFrequency}  RxRd={LastReadRxFrequency})");
    }

    // some radios use 10 Hz steps and some don't, round frequency to the tuning step
    private long RoundToStep(double freq)
    {
      int step = TuningStep;
      return step * (long)Math.Truncate(freq / step);
    }




    //----------------------------------------------------------------------------------------------
    //                                      public methods
    //----------------------------------------------------------------------------------------------
    public void Start(bool rx, bool tx, bool crossband)
    {
      this.rx = rx;
      this.tx = tx;
      this.crossband = crossband;

      StartThread();
    }

    public void SetRxFrequency(double frequency)
    {
      frequency = RoundToStep(frequency);
      LogFreqs($"SetRxFrequency {frequency}");

      if (DialKnobSpinning)
        LogInfo("Ignoring RX frequency change while dial knob is spinning");
      else
        RequestedRxFrequency = (long)frequency;
    }

    public void SetTxFrequency(double frequency)
    {
      frequency = RoundToStep(frequency);
      LogFreqs($"SetTxFrequency {frequency}");

      if (DialKnobSpinning)
        LogInfo("Ignoring TX frequency change while dial knob is spinning");
      else
        RequestedTxFrequency = (long)frequency;
    }

    public void SetRxMode(Slicer.Mode mode)
    {
      RequestedRxMode = mode;
    }

    public void SetTxMode(Slicer.Mode mode)
    {
      RequestedTxMode = mode;
    }

    public void SetPtt(bool ptt)
    {
      LogInfo($"SetPtt {ptt}");
      RequestedPtt = ptt;
    }

    internal void ReleaseApplicationPtt()
    {
      // A momentary control must never unkey a transmission that it did not
      // create. If its ON request is still only queued, cancel it. Once an ON
      // write was attempted (including a timeout) or confirmed, request OFF
      // and let the normal readback/retry path verify release.
      if (PttMayBeOwnedByApplication)
      {
        LogInfo("ReleaseApplicationPtt: requesting PTT OFF");
        RequestedPtt = false;
      }
      else if (RequestedPtt == true)
      {
        LogInfo("ReleaseApplicationPtt: canceling unsent PTT ON");
        RequestedPtt = null;
      }
    }

    // called whenever the transmitter settings are applied, including on every Doppler update,
    // so the radio is written to only when the tone or the on/off state actually changes
    public void SetCtcssTone(double toneHz, bool enabled)
    {
      if (toneHz == CtcssTone && enabled == CtcssEnabled) return;

      LogInfo($"SetCtcssTone {toneHz} {(enabled ? "on" : "off")}");
      CtcssTone = toneHz;
      CtcssEnabled = enabled;
      CtcssPending = true;
    }

    // Request a one-shot CTCSS write after all currently pending RX/TX frequency and
    // mode changes are complete. This is intentionally separate from CtcssPending:
    // the desired tone may be unchanged while an IC-9700 Main/Sub swap moves the
    // previously configured encoder state to the wrong side.
    public void RequestCtcssReassertAfterTune()
    {
      LogInfo("CTCSS reassert requested after tune");
      CtcssReassertAfterTune = true;
    }

    // one-shot keyed carrier with the given tone, used to arm the SO-50 timer
    public void SendArmingTone(double toneHz)
    {
      LogInfo($"SendArmingTone {toneHz}");
      RequestedArmingTone = toneHz;
    }

    internal bool SupportsIcomScopeOutput =>
      ReferenceEquals(commands, RigCtldCommands.SkyCat);

    internal bool RequestIcomRfGainReadback()
    {
      if (!SupportsIcomRfGain)
        return false;
      IcomRfGainReadbackPending = true;
      return true;
    }

    internal bool RequestIcomRfGainWrite(int value)
    {
      if (!SupportsIcomRfGain || value is < 0 or > 255)
        return false;
      Interlocked.Exchange(ref RequestedIcomRfGain, value);
      return true;
    }

    public void RequestIcomScopeOutput()
    {
      LogInfo("IC-9700 scope output reassert requested");
      IcomScopeOutputPending = true;
    }

    internal void CancelIcomScopeRequests()
    {
      IcomScopeOutputPending = false;
      lock (IcomScopeReadbackSync)
      {
        IcomScopeReadbackPending = false;
        IcomScopeReadbackIndex = 0;
        IcomScopeReadbackValues.Clear();
        IcomScopeReadbackGeneration++;
      }
      Volatile.Write(
        ref IcomFixedEdgeReadbackPending,
        null);
      IcomScopeControlCommands.Clear();
    }

    internal bool RequestIcomScopeReadback()
    {
      if (!SupportsIcomScopeOutput)
        return false;

      lock (IcomScopeReadbackSync)
      {
        if (!IcomScopeReadbackPending)
        {
          IcomScopeReadbackIndex = 0;
          IcomScopeReadbackValues.Clear();
          IcomScopeReadbackGeneration++;
          IcomScopeReadbackPending = true;
        }
      }
      return true;
    }

    internal bool RequestIcomFixedEdgeReadback(
      int frequencyRange,
      int edgeNumber)
    {
      if (!SupportsIcomScopeOutput ||
          frequencyRange is < 1 or > 3 ||
          edgeNumber is < 1 or > 4)
        return false;

      Volatile.Write(
        ref IcomFixedEdgeReadbackPending,
        new IcomFixedEdgeReadbackRequest
        {
          FrequencyRange =
            frequencyRange,
          EdgeNumber =
            edgeNumber
        });

      return true;
    }

    internal bool RequestIcomScopeCommand(
      string command)
    {
      if (!SupportsIcomScopeOutput ||
          string.IsNullOrWhiteSpace(command) ||
          !command.StartsWith(
            "U SCOPE_",
            StringComparison.Ordinal))
        return false;

      long droppedBefore =
        IcomScopeControlCommands.DroppedCount;

      IcomScopeControlCommands.Enqueue(
        command);

      if (IcomScopeControlCommands.DroppedCount >
          droppedBefore)
        Log.Warning(
          "IC-9700 scope-control queue was full; dropped the oldest pending command before queuing {Command}",
          command);

      LogInfo(
        $"Queued IC-9700 scope control: {command}");

      return true;
    }




    //----------------------------------------------------------------------------------------------
    //                                        thread 
    //----------------------------------------------------------------------------------------------
    protected override void Cycle()
    {
      ReadPtt();
      if (NeedToWriteTxFreqModeBeforePtt()) TryWriteTxFreqModeBeforePtt();

      // If a requested CTCSS update failed, do not key the transmitter with a
      // stale/unknown encoder state. Keep the write pending and retry next cycle.
      bool ctcssReady = !NeedToWriteCtcss() || TryWriteCtcss();
      if (ctcssReady) TryWritePtt();

      if (NeedToReadRxFrequency()) TryReadRxFrequency();
      if (NeedToReadTxFrequency()) TryReadTxFrequency();

      if (NeedToWriteRxFrequency()) TryWriteRxFrequency(RequestedRxFrequency);
      if (NeedToWriteTxFrequency()) TryWriteTxFrequency();

      if (NeedToWriteRxMode()) TryWriteRxMode();
      if (NeedToWriteTxMode()) TryWriteTxMode();

      TryReassertCtcssAfterTune();

      if (IcomScopeOutputPending) TryEnableIcomScopeOutput();

      if (IcomScopeControlCommands.TryDequeue(
            out string? scopeCommand))
        TryWriteIcomScopeControl(
          scopeCommand);

      // Latest slider value wins; all radio I/O runs on this CAT thread.
      int rfGainToWrite = Interlocked.Exchange(
        ref RequestedIcomRfGain, -1);
      if (rfGainToWrite >= 0)
        TryWriteIcomRfGain(rfGainToWrite);

      if (IcomRfGainReadbackPending)
        TryReadIcomRfGain();

      if (IcomScopeReadbackPending)
        TryReadIcomScopeState();

      IcomFixedEdgeReadbackRequest? fixedEdgeRequest =
        Interlocked.Exchange(
          ref IcomFixedEdgeReadbackPending,
          null);

      if (fixedEdgeRequest != null)
        TryReadIcomFixedEdge(
          fixedEdgeRequest);

      if (RequestedArmingTone.HasValue) TrySendArmingTone();
    }

    protected override bool Setup()
    {
      try
      {
        SelectOperatingMode();

        switch (CatMode)
        {
          case OperatingMode.RxOnly:
          case OperatingMode.TxOnly:
          case OperatingMode.Simplex: return SendWriteCommands(commands.setup_simplex); 
          case OperatingMode.Split:   return SendWriteCommands(commands.setup_split); 
          case OperatingMode.Duplex:  return SendWriteCommands(commands.setup_duplex); 
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to set up radio.");
      }

      return false;
      }

    private RigCtldCommands commands = RigCtldCommands.RigCtld;

    private void SelectOperatingMode()
    {
        // get radio capabilities, either from SkyCAT for from a file
        RadioCapabilities = ReadCapabilitiesFromSkyCat() ?? RadioCapabilities.LoadDefaultCapabilities();
        Log.Information($"Loaded radio capabilities for: {RadioCapabilities.model}");

        // Set appropriate command set
        commands = RadioCapabilities.model == "rigctld.exe" ?
            RigCtldCommands.RigCtld : RigCtldCommands.SkyCat;

        // determmine CatMode, get radio Caps in that mode
        if (rx && !tx)
        {
            CatMode = OperatingMode.RxOnly;
            Caps = RadioCapabilities.simplex!;
        }
        else if (!rx && tx)
        {
            CatMode = OperatingMode.TxOnly;
            Caps = RadioCapabilities.simplex!;
        }
        else if (crossband && RadioCapabilities.duplex != null)
        {
            CatMode = OperatingMode.Duplex;
            Caps = RadioCapabilities.duplex!;
        }
        else if (RadioCapabilities.CanSplitTune(crossband))
        {
            CatMode = OperatingMode.Split;
            Caps = RadioCapabilities.split!;
        }
        else if (RadioCapabilities.simplex != null)
        {
            CatMode = OperatingMode.Simplex;
            Caps = RadioCapabilities.simplex!;
        }
        else
            throw new Exception("Radio does not support any operating modes");
    }

    private RadioCapabilities? ReadCapabilitiesFromSkyCat()
    {
      string json = SendReadCommand("a") ?? string.Empty;

      // The private "a" capability command exists only in SkyCAT. A generic
      // rigctld may answer with different RPRT error codes depending on Hamlib
      // version/backend; any RPRT response means "no SkyCAT capability JSON" and
      // should fall back to the embedded rigctld capability description.
      if (json.StartsWith("RPRT ", StringComparison.Ordinal))
        return null;

      if (json == string.Empty) 
        throw new Exception("Failed to read radio capabilities from SkyCAT");

      return RadioCapabilities.LoadFromJson(json) ??
        throw new Exception("Failed to parse radio capabilities from SkyCAT");
    }




    //----------------------------------------------------------------------------------------------
    //                                    read frequencies
    //----------------------------------------------------------------------------------------------
    private void TryReadRxFrequency()
    {
      string command = GetReadRxFrequencyCommand();
      if (command == string.Empty) return;

      long? readFrequency = ReadFrequency(command);
      if (!readFrequency.HasValue) return;
      long frequency = readFrequency.Value;

      DialKnobSpinning = LastReadRxFrequency != 0 && // first read - ignore, no previous value
        IsDiff(frequency, LastReadRxFrequency) &&    // same freq as before, no change
        IsDiff(frequency, LastWrittenRxFrequency) && // new freq was written, ingnore
          IsDiff(frequency, LastWrittenTxFrequency); // tx freq read by accident, ignore

      LastReadRxFrequency = frequency;
      
      if (DialKnobSpinning)
      {
        // cancel previously requested frequency changes, they would undo the dial knob change
        if (RequestedRxFrequency != NOT_ASSIGNED && IsDiff(RequestedRxFrequency, frequency))
          LogInfo($"Canceling pending RX frequency change ({RequestedRxFrequency}) due to manual change ({frequency})");
        RequestedRxFrequency = NOT_ASSIGNED;
        RequestedTxFrequency = NOT_ASSIGNED;

        OnRxFrequencyChanged();
      }
    }

    private void TryReadTxFrequency()
    {
      string command = GetReadTxFrequencyCommand();
      if (command == string.Empty) return;

      long? readFrequency = ReadFrequency(command);
      if (!readFrequency.HasValue) return;
      long frequency = readFrequency.Value;

      bool changed = LastReadTxFrequency != 0 && LastWrittenTxFrequency != 0 &&
        IsDiff(frequency, LastReadTxFrequency) && IsDiff(frequency, LastWrittenTxFrequency) &&
        IsDiff(frequency, LastWrittenRxFrequency);

      LastReadTxFrequency = frequency;
      if (changed) OnTxFrequencyChanged();
    }

    private long? ReadFrequency(string command)
    {
      var reply = SendReadCommand(command);
      if (reply == null) return null;

      if (!long.TryParse(reply, CultureInfo.InvariantCulture, out long frequency))
      {
        BadReply(reply);
        return null;
      }

      return RoundToStep(frequency);
    }




    //----------------------------------------------------------------------------------------------
    //                                 write frequencies
    //----------------------------------------------------------------------------------------------
    private void TryWriteRxFrequency(long frequency)
    {
      if (frequency == NOT_ASSIGNED) return;

      string command = GetWriteRxFrequencyCommand(frequency);
      if (command == string.Empty) return;

      if (!SendWriteCommand(command)) return;

      LastWrittenRxFrequency = frequency;
      LogFreqs("Rx frequency written");
    }

    private void TryWriteTxFrequency()
    {
      long frequency = RequestedTxFrequency;
      string command = GetWriteTxFrequencyCommand(frequency);
      if (command == string.Empty) return;

      if (!SendWriteCommand(command)) return;

      LastWrittenTxFrequency = frequency;
      LogFreqs("Tx frequency written");
    }

    // if no commands to set split frequency/mode
    // and cannot set main frequency/mode when transmitting,
    // set it immediately before switching to transmit mode
    private void TryWriteTxFreqModeBeforePtt()
    {
      LogInfo("Writing TX frequency and mode before PTT ON");

      // simplex mode: setting RX frequency, then switching to TX, and it becomes TX frequency
      TryWriteRxFrequency(RequestedTxFrequency);
      TryWriteRxMode(RequestedTxMode);
    }




    //----------------------------------------------------------------------------------------------
    //                                       mode
    //----------------------------------------------------------------------------------------------
    private void TryWriteRxMode(Slicer.Mode? mode = null)
    {
      Slicer.Mode newMode = (mode ?? RequestedRxMode!).Value;

      string command = GetWriteRxModeCommand(newMode);
      if (command == string.Empty) return;

      bool ok = SendWriteCommand(command);

      if (!ok)
      {
        RemoveDataFromMode(ref newMode);
        command = GetWriteRxModeCommand(newMode);
        if (command == string.Empty || !SendWriteCommand(command))
          return;
      }

      LastWrittenRxMode = RequestedRxMode;
    }

    private void TryWriteTxMode()
    {
      Slicer.Mode mode = RequestedTxMode!.Value;

      string command = GetWriteTxModeCommand(mode);
      if (command == string.Empty) return;

      bool ok = SendWriteCommand(command);

      if (!ok)
      {
        RemoveDataFromMode(ref mode);
        command = GetWriteTxModeCommand(mode);
        if (command == string.Empty || !SendWriteCommand(command))
          return;
      }

      LastWrittenTxMode = RequestedTxMode!;
    }


    private void RemoveDataFromMode(ref Slicer.Mode mode)
    {
      mode = mode switch
      {
        Slicer.Mode.USB_D => Slicer.Mode.USB,
        Slicer.Mode.LSB_D => Slicer.Mode.LSB,
        Slicer.Mode.FM_D => Slicer.Mode.FM,
        _ => mode
      };
    }




    //----------------------------------------------------------------------------------------------
    //                                         ptt
    //----------------------------------------------------------------------------------------------
    internal bool CanPtt()
    {
      return CatMode != OperatingMode.RxOnly &&
             commands.set_ptt_on != null &&
             commands.set_ptt_off != null;
    }

    private void ReadPtt()
    {
      PttChanged = false;
      if (commands.read_ptt == null) return;

      var reply = SendReadCommand(commands.read_ptt);
      if (reply != "0" && reply != "1")
      {
        if (reply != null) BadReply(reply);
        return;
      }

      bool newPtt = reply == "1";

      PttChanged = newPtt != Ptt;
      Ptt = newPtt;

      if (PttChanged)
        if (Ptt == true) LastWrittenTxFrequency = NOT_ASSIGNED; else LastWrittenRxFrequency = NOT_ASSIGNED;

      // A CAT write can time out after the radio already acted on it. Treat a
      // matching hardware read-back as confirmation of our pending request.
      // Conversely, any observed RX state proves there is no SkyRoof-owned PTT
      // left to release.
      if (!Ptt)
      {
        PttOwnedByApplication = false;
        PttOnAttempted = false;
        PttReleased.Set();
      }
      else if (PttOnAttempted)
      {
        // We attempted PTT ON and the next hardware read says TX. Even if the
        // write reply was lost, treat the transmission as ours for fail-safe
        // release purposes.
        PttOwnedByApplication = true;
        PttOnAttempted = false;
        PttReleased.Reset();
      }

      if (RequestedPtt.HasValue && RequestedPtt.Value == Ptt)
      {
        // Merely requesting ON while another client already has the rig keyed
        // does not establish ownership. Ownership is granted only by a
        // successful write or by readback after an actual ON attempt.
        if (!Ptt)
        {
          PttOwnedByApplication = false;
          PttOnAttempted = false;
          PttReleased.Set();
        }

        RequestedPtt = null;
      }

      LogInfo($"ReadPtt: {Ptt} (changed={PttChanged})");
    }

    private void TryWritePtt()
    {
      if (CatMode == OperatingMode.RxOnly) return;
      if (!RequestedPtt.HasValue) return;
      if (RequestedPtt == Ptt)
      {
        // The state is already satisfied. Do not claim ownership of an
        // externally asserted PTT merely because SkyRoof requested the same
        // state; only a successful write/read-back can establish ownership.
        if (!Ptt)
        {
          PttOwnedByApplication = false;
          PttReleased.Set();
        }

        RequestedPtt = null;
        return;
      }

      string? command = RequestedPtt == true
        ? commands.set_ptt_on
        : commands.set_ptt_off;

      if (command == null)
        return;

      if (RequestedPtt == true)
      {
        PttOnAttempted = true;
        PttReleased.Reset();
      }

      // Never claim a PTT transition that the radio rejected. A failed ON
      // remains "possibly owned" because the reply may have been lost after
      // the radio keyed; a failed OFF remains pending for retry.
      if (!SendWriteCommand(command))
        return;

      Ptt = RequestedPtt.Value;
      PttChanged = true;

      if (Ptt)
      {
        PttOwnedByApplication = true;
        PttOnAttempted = false;
        PttReleased.Reset();
        LastWrittenTxFrequency = NOT_ASSIGNED;
      }
      else
      {
        PttOwnedByApplication = false;
        PttOnAttempted = false;
        PttReleased.Set();
        LastWrittenRxFrequency = NOT_ASSIGNED;
      }

      RequestedPtt = null;
    }




    //----------------------------------------------------------------------------------------------
    //                                        ctcss
    //----------------------------------------------------------------------------------------------
    // the radio can turn the CTCSS encoder on and off
    internal bool CanEnableCtcss()
    {
      return CatMode != OperatingMode.RxOnly &&
             Caps != null &&
             commands.enable_ctcss != null &&
             commands.disable_ctcss != null &&
             Caps.Can(CatAction.enable_ctcss, false) &&
             Caps.Can(CatAction.disable_ctcss, false);
    }

    // the radio can select the CTCSS tone frequency over CAT. some rigs, e.g. the IC-706MKIIG,
    // can only switch the encoder on and off, the tone must be selected on the front panel
    internal bool CanSetCtcssTone()
    {
      return CatMode != OperatingMode.RxOnly &&
             Caps != null &&
             commands.set_ctcss_tone != null &&
             Caps.Can(CatAction.write_ctcss_tone, false);
    }

    // the arming burst needs the tone, the encoder switch and CAT PTT
    internal bool CanSendArmingTone()
    {
      return CanSetCtcssTone() && CanEnableCtcss() && CanPtt();
    }

    private bool TryWriteCtcss()
    {
      bool toneOk = true;
      bool enableOk = true;

      if (CanSetCtcssTone())
        toneOk = SendWriteCommand(
          commands.set_ctcss_tone!.Replace("{tone}", $"{CtcssTones.ToTenths(CtcssTone)}"));

      if (CanEnableCtcss())
        enableOk = SendWriteCommand(
          CtcssEnabled == true ? commands.enable_ctcss! : commands.disable_ctcss!);

      bool ok = toneOk && enableOk;
      if (ok) CtcssPending = false;
      return ok;
    }

    private void TryReassertCtcssAfterTune()
    {
      if (!CtcssReassertAfterTune || !CtcssEnabled.HasValue) return;

      // Do not consume the one-shot request until all frequency/mode changes that could
      // cause a backend VFO/Main-Sub swap have actually been applied.
      if (NeedToWriteRxFrequency() || NeedToWriteTxFrequency() ||
          NeedToWriteRxMode() || NeedToWriteTxMode())
        return;

      LogInfo("Reasserting CTCSS after tune");
      if (TryWriteCtcss())
        CtcssReassertAfterTune = false;
    }

    private void TrySendArmingTone()
    {
      double toneHz = RequestedArmingTone!.Value;
      RequestedArmingTone = null;

      if (!CanSendArmingTone()) return;

      // the operator is transmitting, do not interfere
      if (Ptt) return;

      Log.Information($"Sending {CtcssTones.ARMING_DURATION_MS} ms arming carrier with a {toneHz} Hz tone");

      bool toneOk = SendWriteCommand(
        commands.set_ctcss_tone!.Replace("{tone}", $"{CtcssTones.ToTenths(toneHz)}"));
      bool enableOk = SendWriteCommand(commands.enable_ctcss!);
      if (!toneOk || !enableOk)
      {
        Log.Warning("Arming carrier aborted because CTCSS setup was rejected.");
        return;
      }

      if (!SendWriteCommand(commands.set_ptt_on!))
      {
        Log.Warning("Arming carrier aborted because PTT ON was rejected.");
        return;
      }

      Ptt = true;
      PttOwnedByApplication = true;
      PttReleased.Reset();
      LastWrittenTxFrequency = NOT_ASSIGNED;

      Thread.Sleep(CtcssTones.ARMING_DURATION_MS);

      if (SendWriteCommand(commands.set_ptt_off!))
      {
        Ptt = false;
        PttOwnedByApplication = false;
        PttReleased.Set();
        LastWrittenRxFrequency = NOT_ASSIGNED;
      }
      else
      {
        // Keep our state at TX and schedule an explicit OFF retry. ReadPtt on
        // the next cycle can refine the observed hardware state.
        RequestedPtt = false;
        Log.Error("Arming carrier PTT OFF was rejected; queued another PTT OFF attempt.");
      }

      // re-apply the tone and the on/off state saved for this transmitter
      _ = TryWriteCtcss();
    }




    private void TryEnableIcomScopeOutput()
    {
      IcomScopeOutputPending = false;

      // These are SkyCAT extensions backed by IC-9700 CI-V 27 10 / 27 11.
      // Generic rigctld does not expose an equivalent command, so simply leave passive
      // sniffing in place when SkyCAT is not the active backend.
      if (!ReferenceEquals(commands, RigCtldCommands.SkyCat))
      {
        LogInfo("IC-9700 scope output request skipped: CAT backend is not SkyCAT");
        return;
      }

      bool scopeOk = SendWriteCommand("U SCOPE 1");
      bool dataOk = SendWriteCommand("U SCOPE_DATA 1");

      if (scopeOk && dataOk)
        LogInfo(
          "IC-9700 scope and waveform output enabled through SkyCAT");
      else
        Log.Warning(
          "SkyCAT did not accept all IC-9700 scope output commands. " +
          "Update SkyCAT to a build that supports U SCOPE / U SCOPE_DATA.");
    }


    private void TryReadIcomFixedEdge(
      IcomFixedEdgeReadbackRequest request)
    {
      if (!ReferenceEquals(
            commands,
            RigCtldCommands.SkyCat))
        return;

      string? reply =
        SendReadCommand(
          $"U SCOPE_READ_EDGE {request.FrequencyRange} {request.EdgeNumber}");

      if (string.IsNullOrWhiteSpace(
            reply) ||
          reply.StartsWith(
            "RPRT ",
            StringComparison.Ordinal))
      {
        Log.Warning(
          "SkyCAT IC-9700 fixed-edge readback failed for range {Range}, edge {Edge}: {Reply}",
          request.FrequencyRange,
          request.EdgeNumber,
          reply ?? "<null>");
        return;
      }

      try
      {
        IcomFixedEdgeReadbackState state =
          IcomFixedEdgeReadbackState.Parse(
            reply);

        IcomFixedEdgeReadbackReceived?.Invoke(
          state);
      }
      catch (FormatException ex)
      {
        Log.Warning(
          ex,
          "SkyCAT returned malformed fixed-edge readback: {Reply}",
          reply);
      }
    }


    private void TryWriteIcomRfGain(int gain)
    {
      if (!SupportsIcomRfGain)
        return;

      if (!SendWriteCommand($"U RF_GAIN {gain}"))
      {
        Log.Warning("SkyCAT rejected IC-9700 RF gain: {Gain}", gain);
        return;
      }

      // Confirm the actual register value, do not assume command acceptance
      // implies the physical RF gain knob changed.
      IcomRfGainReadbackPending = true;
    }

    private void TryReadIcomRfGain()
    {
      IcomRfGainReadbackPending = false;
      if (!SupportsIcomRfGain)
        return;

      string? reply = SendReadCommand("U RF_GAIN_READ");
      if (reply == null || reply.StartsWith("RPRT ", StringComparison.Ordinal) ||
          !int.TryParse(reply, NumberStyles.None,
            CultureInfo.InvariantCulture, out int gain) ||
          gain is < 0 or > 255)
      {
        Log.Warning("SkyCAT RF gain readback failed: {Reply}", reply);
        return;
      }

      syncContext.Post(_ => IcomRfGainReadbackReceived?.Invoke(gain), null);
    }

    private void TryReadIcomScopeState()
    {
      // A single radio register is queried on this CAT cycle. Do NOT hold
      // the state lock during socket I/O: UI cancellation stays responsive.
      // A generation marker discards late responses after Cancel or a write.
      if (!ReferenceEquals(commands, RigCtldCommands.SkyCat))
      {
        lock (IcomScopeReadbackSync)
        {
          IcomScopeReadbackPending = false;
          IcomScopeReadbackIndex = 0;
          IcomScopeReadbackValues.Clear();
          IcomScopeReadbackGeneration++;
        }
        return;
      }

      string key;
      int generation;
      lock (IcomScopeReadbackSync)
      {
        if (!IcomScopeReadbackPending ||
            IcomScopeReadbackIndex >= IcomScopeReadbackFields.Length)
          return;
        key = IcomScopeReadbackFields[IcomScopeReadbackIndex++];
        generation = IcomScopeReadbackGeneration;
      }

      string? reply = SendReadCommand($"U SCOPE_READ_FIELD {key}");
      string? partial = null;
      int successful = 0;
      lock (IcomScopeReadbackSync)
      {
        if (!IcomScopeReadbackPending ||
            generation != IcomScopeReadbackGeneration)
          return;

        if (reply != null && reply.StartsWith(key + "=", StringComparison.Ordinal))
          IcomScopeReadbackValues[key] = reply[(key.Length + 1)..];
        else
          Log.Warning(
            "SkyCAT scope field {Field} unavailable: {Reply}; continuing other fields.",
            key, reply ?? "<no reply>");

        if (IcomScopeReadbackIndex != IcomScopeReadbackFields.Length)
          return;

        IcomScopeReadbackPending = false;
        IcomScopeReadbackIndex = 0;
        successful = IcomScopeReadbackValues.Count;
        if (IcomScopeReadbackValues.ContainsKey("SELECT"))
          partial = string.Join(";",
            IcomScopeReadbackValues.Select(kv => $"{kv.Key}={kv.Value}"));
        IcomScopeReadbackValues.Clear();
      }

      if (partial == null)
      {
        Log.Warning("IC-9700 scope receiver selection unavailable; cannot apply partial state.");
        return;
      }
      try
      {
        IcomScopeReadbackState state = IcomScopeReadbackState.ParsePartial(partial);
        IcomScopeReadbackReceived?.Invoke(state);
        if (state.IsPartial)
          Log.Warning(
            "IC-9700 scope synchronization partial ({Received}/{Total}); missing properties are preserved.",
            successful, IcomScopeReadbackFields.Length);
      }
      catch (FormatException ex)
      {
        Log.Warning(ex, "Malformed partial scope readback: {Readback}", partial);
      }
    }


    private void TryWriteIcomScopeControl(
      string command)
    {
      // An operator change invalidates values collected earlier in the batch;
      // restart the next read at SELECT to avoid mixing before/after states.
      lock (IcomScopeReadbackSync)
      {
        if (IcomScopeReadbackPending)
        {
          IcomScopeReadbackIndex = 0;
          IcomScopeReadbackValues.Clear();
          IcomScopeReadbackGeneration++;
        }
      }
      if (!ReferenceEquals(
            commands,
            RigCtldCommands.SkyCat))
      {
        LogInfo(
          $"IC-9700 scope control skipped because CAT backend is not SkyCAT: {command}");
        return;
      }

      if (!SendWriteCommand(command))
      {
        Interlocked.Increment(
          ref RejectedIcomScopeControlCountValue);

        Log.Warning(
          "SkyCAT rejected IC-9700 scope control command: {Command}",
          command);
      }
    }


    //----------------------------------------------------------------------------------------------
    //                                    get command
    //----------------------------------------------------------------------------------------------
    private string GetReadRxFrequencyCommand()
    {
      switch (CatMode)
      {
        case OperatingMode.TxOnly:
          return string.Empty;

        case OperatingMode.RxOnly:
          if (Ptt == false && Caps.Can(CatAction.read_rx_frequency, Ptt)) return commands.read_rx_frequency!;
          if (Ptt == true && Caps.Can(CatAction.read_tx_frequency, Ptt)) return commands.read_tx_frequency!;
          break;

        case OperatingMode.Simplex:
          if (Ptt == false && Caps.Can(CatAction.read_rx_frequency, Ptt)) return commands.read_rx_frequency!;
          break;

        // split or duplex
        default:
          if (Caps.Can(CatAction.read_rx_frequency, Ptt)) return commands.read_rx_frequency!;
          break;
      }

      return string.Empty;
    }

    private string GetReadTxFrequencyCommand()
    {
      switch (CatMode)
      {
        case OperatingMode.RxOnly:
          return string.Empty;

        case OperatingMode.TxOnly:
          if (Ptt == false && Caps.Can(CatAction.read_rx_frequency, Ptt)) return commands.read_rx_frequency!;
          if (Ptt == true && Caps.Can(CatAction.read_tx_frequency, Ptt)) return commands.read_tx_frequency!;
          break;

        case OperatingMode.Simplex:
          if (Ptt == true && Caps.Can(CatAction.read_tx_frequency, Ptt)) return commands.read_tx_frequency!;
          break;

        // split or duplex
        default:
          if (Caps.Can(CatAction.read_tx_frequency, Ptt)) return commands.read_tx_frequency!;
          break;
      }

      return string.Empty;
    }

    private string GetWriteRxFrequencyCommand(long frequency)
    {
      switch (CatMode)
      {
        case OperatingMode.TxOnly:
          return string.Empty;

        case OperatingMode.RxOnly:
          if (Ptt == false && Caps.Can(CatAction.write_rx_frequency, Ptt)) return commands.write_rx_frequency.Replace("{frequency}", $"{frequency}");
          else if (Ptt == true && Caps.Can(CatAction.write_tx_frequency, Ptt)) return commands.write_tx_frequency.Replace("{frequency}", $"{frequency}");
          break;

        case OperatingMode.Simplex:
          if (Ptt == false && Caps.Can(CatAction.write_rx_frequency, Ptt)) return commands.write_rx_frequency.Replace("{frequency}", $"{frequency}");
          break;

        default:
          if (Caps.Can(CatAction.write_rx_frequency, Ptt)) return commands.write_rx_frequency.Replace("{frequency}", $"{frequency}");
          break;
      }

      return string.Empty;
    }

    private string GetWriteTxFrequencyCommand(long frequency)
    {
      switch (CatMode)
      {
        case OperatingMode.RxOnly:
          return string.Empty;

        case OperatingMode.TxOnly:
          if (Ptt == false && Caps.Can(CatAction.write_rx_frequency, Ptt)) return commands.write_rx_frequency!.Replace("{frequency}", $"{frequency}");
          if (Ptt == true && Caps.Can(CatAction.write_tx_frequency, Ptt)) return commands.write_tx_frequency!.Replace("{frequency}", $"{frequency}");
          break;

        case OperatingMode.Simplex:
          if (Ptt == true && Caps.Can(CatAction.write_tx_frequency, Ptt)) return commands.write_tx_frequency!.Replace("{frequency}", $"{frequency}");
          break;

        default:
          if (Caps.Can(CatAction.write_tx_frequency, Ptt)) return commands.write_tx_frequency!.Replace("{frequency}", $"{frequency}");
          break;
      }

      return string.Empty;
    }

    private string GetWriteRxModeCommand(Slicer.Mode mode)
    {
      switch (CatMode)
      {
        case OperatingMode.TxOnly:
          return string.Empty;

        case OperatingMode.RxOnly:
          if (Ptt == false && Caps.CanSetup(CatAction.write_rx_mode)) return commands.write_rx_mode!.Replace("{mode}", $"{mode}");
          if (Ptt == true && Caps.Can(CatAction.write_tx_mode, Ptt)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          break;

        case OperatingMode.Simplex:
          if (Ptt == false && Caps.CanSetup(CatAction.write_rx_mode)) return commands.write_rx_mode!.Replace("{mode}", $"{mode}");
          break;

        default:
          if (Ptt == false && Caps.CanSetup(CatAction.write_rx_mode)) return commands.write_rx_mode!.Replace("{mode}", $"{mode}");
          if (Ptt == true && Caps.Can(CatAction.write_rx_mode, Ptt)) return commands.write_rx_mode!.Replace("{mode}", $"{mode}");
          break;
      }

      return string.Empty;
    }

    private string GetWriteTxModeCommand(Slicer.Mode mode)
    {
      switch (CatMode)
      {
        case OperatingMode.RxOnly:
          return string.Empty;

        case OperatingMode.TxOnly:
          if (Ptt == false && Caps.CanSetup(CatAction.write_rx_mode)) return commands.write_rx_mode!.Replace("{mode}", $"{mode}");
          if (Ptt == true && Caps.Can(CatAction.write_tx_mode, Ptt)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          break;

        case OperatingMode.Simplex:
          if (Ptt == false && Caps.CanSetup(CatAction.write_tx_mode)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          if (Ptt == true && Caps.Can(CatAction.write_tx_mode, Ptt)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          break;

        default:
          if (Ptt == false && Caps.CanSetup(CatAction.write_tx_mode)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          if (Ptt == true && Caps.Can(CatAction.write_tx_mode, Ptt)) return commands.write_tx_mode!.Replace("{mode}", $"{mode}");
          break;
      }

      return string.Empty;
    }




    //----------------------------------------------------------------------------------------------
    //                                  check conditions
    //----------------------------------------------------------------------------------------------
    private bool NeedToWriteTxFreqModeBeforePtt()
    {
      // not simplex
      if (CatMode != OperatingMode.Simplex) return false;

      // not swsitching to tx
      if (RequestedPtt != true) return false;

      // nothing to write
      if (RequestedTxFrequency == NOT_ASSIGNED && !RequestedTxMode.HasValue) return false;

      return true;
    }

    private bool NeedToReadRxFrequency()
    {
      if (CatMode == OperatingMode.TxOnly) return false;
      return !IgnoreDialKnob && (Ptt == false || CatMode == OperatingMode.RxOnly);
    }

    private bool NeedToReadTxFrequency()
    {
      if (CatMode == OperatingMode.RxOnly) return false;
      return !IgnoreDialKnob && (Ptt == true || CatMode == OperatingMode.TxOnly);
    }

    private bool NeedToWriteRxFrequency()
    {
      if (CatMode == OperatingMode.TxOnly) return false;
      if (RequestedRxFrequency == NOT_ASSIGNED) return false; // never assigned
      if (CatMode == OperatingMode.Simplex && PttChanged) return true;
      return IsDiff(RequestedRxFrequency, LastWrittenRxFrequency);
    }

    private bool NeedToWriteTxFrequency()
    {
      if (CatMode == OperatingMode.RxOnly) return false;
      if (RequestedTxFrequency == NOT_ASSIGNED) return false;
      if (CatMode == OperatingMode.Simplex && PttChanged) return true;
      return IsDiff(RequestedTxFrequency, LastWrittenTxFrequency);
    }

    private bool NeedToWriteCtcss()
    {
      // never set by the app
      if (!CtcssEnabled.HasValue) return false;
      if (CtcssPending) return true;

      // Icom Auto Repeater and similar features may drop the tone when the frequency changes,
      // re-assert it immediately before every transmission
      return RequestedPtt == true && Ptt == false;
    }

    private bool NeedToWriteRxMode()
    {
      if (CatMode == OperatingMode.TxOnly) return false;
      if (!RequestedRxMode.HasValue) return false;
      if (CatMode == OperatingMode.Simplex && PttChanged) return true;
      return RequestedRxMode != LastWrittenRxMode;
    }

    private bool NeedToWriteTxMode()
    {
      if (CatMode == OperatingMode.RxOnly) return false;
      if (!RequestedTxMode.HasValue) return false;
      if (CatMode == OperatingMode.Simplex && PttChanged) return true;
      return RequestedTxMode != LastWrittenTxMode;
    }

    // may be changed to allow some slack
    private bool IsDiff(long freq1, long freq2)
    {
      return Math.Abs(freq1 - freq2) > 0;
    }




    //----------------------------------------------------------------------------------------------
    //                                      shutdown
    //----------------------------------------------------------------------------------------------
    public override void Dispose()
    {
      // Cancel a not-yet-sent SkyRoof PTT-ON request. If SkyRoof actually keyed
      // the rig, ask the worker to send PTT OFF and give it a bounded opportunity
      // to receive the radio acknowledgement before the TCP connection is torn down.
      if (!PttMayBeOwnedByApplication && RequestedPtt == true)
        RequestedPtt = null;

      if (PttMayBeOwnedByApplication)
      {
        RequestedPtt = false;

        if (!PttReleased.Wait(4000))
          Log.Warning(
            "CAT engine stopped while SkyRoof-owned or possibly-owned PTT could not be confirmed OFF.");
      }

      base.Dispose();
      PttReleased.Dispose();
    }




    //----------------------------------------------------------------------------------------------
    //                                      events
    //----------------------------------------------------------------------------------------------
    protected void OnRxFrequencyChanged()
    {
      LogFreqs($"OnRxFrequencyChanged");
      syncContext.Post(s => RxTuned?.Invoke(this, EventArgs.Empty), null);
    }

    protected void OnTxFrequencyChanged()
    {
      LogInfo($"OnTxFrequencyChanged:  (TxRequested={RequestedTxFrequency}  TxWritten={LastWrittenTxFrequency}  TxRdead={LastReadTxFrequency}  RxWritten={LastWrittenRxFrequency}  RxRead={LastReadRxFrequency})");

      syncContext.Post(s => TxTuned?.Invoke(this, EventArgs.Empty), null);
    }
  }
}
