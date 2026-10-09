using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof
{
  public class AudioSettings
  {
    // non-browsable
    public int SoundcardVolume = -25;
    // Optional explicit Windows mixer session for RS-BA1 Remote Utility.
    // Never change the global endpoint volume or an unrelated application.
    public string? RsBa1PlaybackProcessName;
    public string? RsBa1PlaybackDeviceId;
    public bool SpeakerEnabled = true;

    [DisplayName("Speaker Audio Device")]
    [Description("Soundcard for audio output")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? SpeakerSoundcard { get; set; } = Soundcard.GetDefaultSoundcardId(DataFlow.Render);

    [DisplayName("FM Squelch")]
    [Description("Enable Squelch in the FM mode")]
    [DefaultValue(true)]
    public bool Squelch { get; set; } = true;

    public override string ToString() { return string.Empty; }
  }
}
