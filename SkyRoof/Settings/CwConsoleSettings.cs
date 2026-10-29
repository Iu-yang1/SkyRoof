using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;
using SkyRoof.CW;

namespace SkyRoof
{
  public enum CwReceiveAudioSource
  {
    SDR,
    WasapiCapture,
    RsBa1Loopback
  }

  public sealed class CwConsoleSettings
  {
    [DisplayName("Enable Receive")]
    [Description(
      "Enable the CW receive audio pipeline. This is disabled by default so an upgrade never opens a microphone or loopback endpoint unexpectedly.")]
    [DefaultValue(false)]
    public bool ReceiveEnabled { get; set; } = false;

    [DisplayName("Audio Source")]
    [Description(
      "IC-9700 USB AF Input reads a Windows capture endpoint such as Microphone (USB Audio CODEC); the radio USB AF/IF Output must be AF, not IF. RS-BA1 Playback Loopback captures the Windows playback/render endpoint that RS-BA1 is playing into.")]
    [DefaultValue(CwReceiveAudioSource.WasapiCapture)]
    public CwReceiveAudioSource AudioSource { get; set; } =
      CwReceiveAudioSource.WasapiCapture;

    [DisplayName("Radio / USB Capture Device (RX)")]
    [Description(
      "Windows capture/input endpoint used for direct radio RX AF audio. For an IC-9700 USB connection this is normally Microphone (USB Audio CODEC), not the Speakers/USB Audio CODEC render endpoint. The IC-9700 USB AF/IF Output must be set to AF for the CW decoder; IF output is not this input mode.")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? CaptureDeviceId { get; set; } =
      Soundcard.GetPreferredSoundcardId(
        DataFlow.Capture,
        "USB Audio CODEC",
        "ICOM");

    [DisplayName("RS-BA1 Playback Endpoint (loopback)")]
    [Description(
      "Windows playback/render endpoint captured in loopback mode. Select the speaker/virtual device that RS-BA1 Remote Utility is actually playing into (for example a remote-audio virtual endpoint). Do not select the radio's Microphone (USB Audio CODEC) capture endpoint here.")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? RsBa1LoopbackDeviceId { get; set; }

    [DisplayName("Spectrum Cleanup")]
    [Description(
      "Display only. Bypass shows raw PCM. HamNoise Classic/V2 denoise only the CW Skimmer-style spectrum/waterfall copy; detector, tracker, DeepCW and transcript decoding always use untouched raw PCM.")]
    [DefaultValue(CwDenoiseMode.Bypass)]
    public CwDenoiseMode SpectrumDenoiseMode { get; set; } =
      CwDenoiseMode.Bypass;

    // The 245–308 Hz phase gate has only synthetic validation. Do not
    // change production weak-CW behaviour without a labelled IC-9700 AF
    // comparison. A *new* property intentionally ignores a saved true value
    // from experimental installers' legacy VerifyCarrierPhase field.
    [DisplayName("Experimental CW Carrier Phase Verification")]
    [Description("A/B only: distinguish keyed tones from coloured AF noise. Off by default until real weak-signal recordings validate sensitivity.")]
    [DefaultValue(false)]
    public bool ExperimentalVerifyCarrierPhase {get;set;} = false;

    // New inference paths are intentionally OFF until real labelled
    // IC-9700 PCM A/B tests establish no regression.
    [DisplayName("Experimental CW Multi-Hypothesis Gate")]
    [Description("Require sustained independent CW keying evidence before assigning an RX lane. A/B experiment; disabled by default.")]
    [DefaultValue(false)]
    public bool ExperimentalStationGate {get;set;} = false;

    [DisplayName("Experimental Coherent CW Separation")]
    [Description("Try bounded joint multi-tone real-I/Q least-squares separation on nearby CW signals; fallback on bad residual. Not yet validated for 5 Hz / 8 dB pileups.")]
    [DefaultValue(false)]
    public bool ExperimentalCoherentSeparation {get;set;} = false;

    [DisplayName("Experimental Bayesian Morse HSMM")]
    [Description("Cross-check neural CTC with an independent soft-keying duration decoder. Neural remains primary; test on labelled recordings first.")]
    [DefaultValue(false)]
    public bool ExperimentalHsmmArbitration {get;set;} = false;

    // Compatibility field for older SkyRoof settings files. Manual CW
    // transmission no longer requires or consults a separate software
    // enable gate; the real radio/SkyCAT status is verified on Send.
    [Browsable(false)]
    [DefaultValue(true)]
    public bool TransmitEnabled { get; set; } = true;

    [DisplayName("SkyCAT CW Keyer Port")]
    [Description(
      "Loopback-only SkyCAT CW keyer TCP port. Default 4538. This must match skycatd --cw-port.")]
    [DefaultValue(4538)]
    public int CwKeyerPort { get; set; } = 4538;

    [DisplayName("CW Message Macros")]
    [Description(
      "Persistent F1-F8 text presets. Right-click a macro button to edit; F1-F8 load into the composer and Shift+F1-F8 explicitly sends after automatic radio/interlock preflight.")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public CwMacroSettings Macros { get; set; } = new();

    [Browsable(false)]
    [DefaultValue(2.4)]
    public double TrackingAnalysisSeconds { get; set; } = 2.4;

    public override string ToString() => string.Empty;
  }
}


namespace SkyRoof
{
  public sealed class CwMacroSettings
  {
    [DisplayName("F1")]
    [DefaultValue("")]
    public string F1 { get; set; } = string.Empty;

    [DisplayName("F2")]
    [DefaultValue("")]
    public string F2 { get; set; } = string.Empty;

    [DisplayName("F3")]
    [DefaultValue("")]
    public string F3 { get; set; } = string.Empty;

    [DisplayName("F4")]
    [DefaultValue("")]
    public string F4 { get; set; } = string.Empty;

    [DisplayName("F5")]
    [DefaultValue("")]
    public string F5 { get; set; } = string.Empty;

    [DisplayName("F6")]
    [DefaultValue("")]
    public string F6 { get; set; } = string.Empty;

    [DisplayName("F7")]
    [DefaultValue("")]
    public string F7 { get; set; } = string.Empty;

    [DisplayName("F8")]
    [DefaultValue("")]
    public string F8 { get; set; } = string.Empty;

    public override string ToString() =>
      string.Empty;
  }
}
