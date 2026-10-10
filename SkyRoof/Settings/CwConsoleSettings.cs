using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

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
      "Radio USB / WASAPI Capture reads a Windows capture endpoint such as Microphone (USB Audio CODEC). RS-BA1 Loopback captures the Windows playback/render endpoint that RS-BA1 is playing into.")]
    [DefaultValue(CwReceiveAudioSource.WasapiCapture)]
    public CwReceiveAudioSource AudioSource { get; set; } =
      CwReceiveAudioSource.WasapiCapture;

    [DisplayName("Radio / USB Capture Device (RX)")]
    [Description(
      "Windows capture/input endpoint used for direct radio RX audio. For an IC-9700 USB connection this is normally Microphone (USB Audio CODEC), not the Speakers/USB Audio CODEC render endpoint.")]
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

    [DisplayName("Enable CW Transmit")]
    [Description(
      "Permit the CW Console to use SkyCAT's fail-closed IC-9700 Command 17 keyer. Enabled by default for new configurations. The Console still requires an explicit non-persistent Arm TX action before every transmitting session.")]
    [DefaultValue(true)]
    public bool TransmitEnabled { get; set; } = true;

    [DisplayName("SkyCAT CW Keyer Port")]
    [Description(
      "Loopback-only SkyCAT CW keyer TCP port. Default 4538. This must match skycatd --cw-port.")]
    [DefaultValue(4538)]
    public int CwKeyerPort { get; set; } = 4538;

    [DisplayName("CW Message Macros")]
    [Description(
      "Persistent F1-F8 text presets. F1-F8 load a preset into the composer; Shift+F1-F8 explicitly sends it only when TX is already enabled and armed.")]
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
