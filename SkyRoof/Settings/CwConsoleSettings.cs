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
    [DefaultValue(CwReceiveAudioSource.SDR)]
    public CwReceiveAudioSource AudioSource { get; set; } =
      CwReceiveAudioSource.SDR;

    [DisplayName("WASAPI Capture Device")]
    [Description(
      "Windows capture endpoint used when Audio Source is WasapiCapture.")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? CaptureDeviceId { get; set; } =
      Soundcard.GetDefaultSoundcardId(DataFlow.Capture);

    [DisplayName("RS-BA1 Loopback Device")]
    [Description(
      "Windows render endpoint captured in loopback mode when Audio Source is RsBa1Loopback. Leave unselected to reuse the output endpoint selected for the RS-BA1 Remote Utility AF Gain session. Loopback captures the entire endpoint mix, so a dedicated endpoint is recommended.")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? RsBa1LoopbackDeviceId { get; set; }

    [DisplayName("Enable CW Transmit")]
    [Description(
      "Permit the CW Console to use SkyCAT's fail-closed IC-9700 Command 17 keyer. Disabled by default. The Console still requires an explicit non-persistent Arm TX action before every transmitting session.")]
    [DefaultValue(false)]
    public bool TransmitEnabled { get; set; } = false;

    [DisplayName("SkyCAT CW Keyer Port")]
    [Description(
      "Loopback-only SkyCAT CW keyer TCP port. Default 4538. This must match skycatd --cw-port.")]
    [DefaultValue(4538)]
    public int CwKeyerPort { get; set; } = 4538;

    [Browsable(false)]
    [DefaultValue(2.4)]
    public double TrackingAnalysisSeconds { get; set; } = 2.4;

    public override string ToString() => string.Empty;
  }
}
