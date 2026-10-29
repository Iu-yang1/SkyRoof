using System;
using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof
{
  public enum AfcIfSource { WasapiCapture, PlaybackLoopback }

  /// <summary>
  /// Frequency tracking is independent of the saved satellite/transponder
  /// offsets and never changes the TX frequency model.
  /// </summary>
  public sealed class AfcSettings
  {
    [DisplayName("AFC enabled")]
    [DefaultValue(false)]
    public bool Enabled { get; set; } = false;

    [DisplayName("AFC input (12 kHz IF)")]
    [Description("Windows capture endpoint carrying the radio's USB 12 kHz IF output. Select a different endpoint if another program uses the radio's USB audio for AF.")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? InputDeviceId { get; set; } =
      Soundcard.GetPreferredSoundcardId(DataFlow.Capture, "USB Audio CODEC", "ICOM");

    [DisplayName("Control")]
    [DefaultValue(AFC.AfcControlMode.Assist)]
    public AFC.AfcControlMode Control { get; set; } = AFC.AfcControlMode.Assist;

    [DisplayName("IF transport")]
    [DefaultValue(AfcIfSource.WasapiCapture)]
    public AfcIfSource InputSource { get; set; } = AfcIfSource.WasapiCapture;

    [DisplayName("IF playback loopback endpoint")]
    [Description("Use the render/virtual speaker endpoint carrying remote 12-kHz IF; not an AF-only speaker feed.")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? LoopbackDeviceId { get; set; }

    [DisplayName("Nominal IF frequency (Hz)")]
    [DefaultValue(12000.0)]
    public double IfReferenceHz { get; set; } = 12000;

    [DisplayName("RF/IF direction (0=auto, +/-1=override)")]
    [Description("0 learns polarity from a stable tracked signal and observed CAT tuning. Only required for automatic CAT control; Assist works without polarity.")]
    [DefaultValue(0)]
    public int RfIfSign { get; set; } = 0;

    [DisplayName("Target IF offset (Hz)")]
    [DefaultValue(0.0)]
    public double TargetOffsetHz { get; set; } = 0;

    [DisplayName("SSB reference available")]
    [Description("Only enable when there is an identifiable continuous CW/pilot reference in the selected IF channel; normal suppressed-carrier speech has no unique absolute AFC measurement.")]
    [DefaultValue(false)]
    public bool SsbPilotEnabled { get; set; } = false;

    [DisplayName("Maximum AFC correction (Hz)")]
    [DefaultValue(2000.0)]
    public double MaxCorrectionHz { get; set; } = 2000;

    [DisplayName("FM write deadband (Hz)")]
    [DefaultValue(20.0)]
    public double FmDeadbandHz { get; set; } = 20;

    [DisplayName("CW/SSB write deadband (Hz)")]
    [DefaultValue(5.0)]
    public double ToneDeadbandHz { get; set; } = 5;

    public override string ToString() => "Adaptive Frequency Tracking";
  }
}

namespace SkyRoof.AFC
{
  public enum AfcSignalMode { FM, CW, SSB }
  public enum AfcControlMode { Off, Assist, Auto }
  public enum AfcLockState { Off, Acquiring, Locked, Coasting, Hold, Lost, Fault }

  public readonly record struct AfcObservation(
    AfcSignalMode Mode,
    DateTime SampleUtc,
    long Generation,
    double IfOffsetHz,
    double SigmaHz,
    double Confidence,
    bool HasAbsoluteReference,
    bool Ambiguous = false);

  public readonly record struct AfcSnapshot(
    AfcLockState State,
    AfcSignalMode Mode,
    AfcControlMode Control,
    double ObservedIfOffsetHz,
    double CorrectionHz,
    double ErrorHz,
    double Confidence,
    int MappingSign,
    DateTime LastObservationUtc,
    string Detail);
}
