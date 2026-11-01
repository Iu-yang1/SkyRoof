using System;
using System.ComponentModel;
using Newtonsoft.Json;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof.IF
{
  public enum IfAfSelection { FollowRadio, Usb, Lsb, CwUsb, CwLsb, Fm }

  public enum IfAfAudioRoute { PreferIfAfAudio, ExistingVirtualCable, AutoSignedCable }

  public enum IfAfLatencyPreset
  {
    Fast20Ms = 20,
    Balanced40Ms = 40,
    Stable80Ms = 80,
    Compatible200Ms = 200
  }

  /// <summary>Opt-in IF-AF test path. Uses only receiver PCM; no CAT or PTT
  /// writes. VAC device must be an explicit Windows RENDER endpoint and never
  /// the IC-9700 USB Audio CODEC playback/TX input.</summary>
  public sealed class IfAfSettings
  {
    [DisplayName("Enable USB IF -> AF")]
    [DefaultValue(false)]
    public bool Enabled { get; set; } = false;

    [DisplayName("12 kHz IF capture (USB)")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? InputDeviceId { get; set; } =
      Soundcard.GetPreferredSoundcardId(DataFlow.Capture, "USB Audio CODEC", "ICOM");

    [DisplayName("IF capture channel")]
    [DefaultValue(AudioChannelChoice.Left)]
    public AudioChannelChoice CaptureChannel { get; set; } = AudioChannelChoice.Left;

    [DisplayName("Demodulation")]
    [DefaultValue(IfAfSelection.FollowRadio)]
    public IfAfSelection Mode { get; set; } = IfAfSelection.FollowRadio;

    [DisplayName("AF audio route")]
    [Description("AutoSignedCable detects an installed VB-CABLE or VAC Lite playback/capture pair. ExistingVirtualCable uses your selected output. PreferIfAfAudio is a legacy experimental route and is not installed by SkyRoof.")]
    [DefaultValue(IfAfAudioRoute.AutoSignedCable)]
    public IfAfAudioRoute AudioRoute { get; set; } = IfAfAudioRoute.AutoSignedCable;

    [DisplayName("AF output latency preset")]
    [Description("Requested shared-mode WASAPI render buffer; 20ms can glitch on some devices. Actual hardware/OS end-to-end latency is measured separately.")]
    [DefaultValue(IfAfLatencyPreset.Balanced40Ms)]
    public IfAfLatencyPreset LatencyPreset { get; set; } =
      IfAfLatencyPreset.Balanced40Ms;

    [DisplayName("Installed virtual audio cable status")]
    [Description("Shows paired Windows playback and recording endpoints. This is not a cryptographic signature check. Obtain drivers only from the vendor.")]
    [ReadOnly(true)]
    [JsonIgnore]
    public string DriverStatus
    {
      get
      {
        var detected = IfAfSignedCableDiscovery.Detect();
        return detected.Status;
      }
    }

    [DisplayName("Virtual microphone input for WSJT-X")]
    [ReadOnly(true)]
    [JsonIgnore]
    public string VirtualCaptureDevice => IfAfSignedCableDiscovery.Detect() is var selected &&
      selected.Ready ? selected.Provider + " (Windows recording endpoint)" :
      "(No automatically paired recording endpoint)";

    [DisplayName("Virtual audio render device")]
    [Description("Only for ExistingVirtualCable; choose the third-party cable playback end (e.g. CABLE Input). Never select IC-9700 USB Audio CODEC TX.")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? VacRenderDeviceId { get; set; }

    [DisplayName("SSB AF low cut (Hz)")]
    [DefaultValue(180)]
    public int SsbLowHz { get; set; } = 180;

    [DisplayName("SSB AF high cut (Hz)")]
    [DefaultValue(3300)]
    public int SsbHighHz { get; set; } = 3300;

    [DisplayName("AF output gain (dB)")]
    [DefaultValue(0)]
    public int GainDb { get; set; } = 0;

    [DisplayName("FM peak deviation (Hz)")]
    [Description("2500 Hz for narrow FM, 5000 Hz for wider FM. Must match the incoming modulation.")]
    [DefaultValue(2500)]
    public int FmDeviationHz { get; set; } = 2500;

    public static IfAfMode Resolve(IfAfSelection selection, Slicer.Mode radio)
    {
      if (selection != IfAfSelection.FollowRadio)
        return selection switch
        {
          IfAfSelection.Lsb => IfAfMode.Lsb,
          IfAfSelection.CwUsb => IfAfMode.CwUsb,
          IfAfSelection.CwLsb => IfAfMode.CwLsb,
          IfAfSelection.Fm => IfAfMode.Fm,
          _ => IfAfMode.Usb
        };
      return radio switch
      {
        Slicer.Mode.LSB or Slicer.Mode.LSB_D => IfAfMode.Lsb,
        Slicer.Mode.CW => IfAfMode.CwUsb,
        Slicer.Mode.FM or Slicer.Mode.FM_D => IfAfMode.Fm,
        _ => IfAfMode.Usb
      };
    }
    public override string ToString() => Enabled ? "Enabled" : "Disabled";
  }
}
