using System;
using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof.IF
{
  public enum IfAfSelection { FollowRadio, Usb, Lsb, CwUsb, CwLsb, Fm }

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

    [DisplayName("Virtual audio render device")]
    [Description("CABLE Input / VAC playback endpoint. NEVER choose USB Audio CODEC (radio TX audio).")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? VacRenderDeviceId { get; set; }

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
