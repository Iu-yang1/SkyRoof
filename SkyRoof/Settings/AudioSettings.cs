using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof
{
  public enum RecordingAudioSource
  {
    Auto,
    Sdr,
    WasapiCapture,
    RsBa1Loopback
  }

  public class AudioSettings
  {
    // non-browsable
    public int SoundcardVolume = -25;
    // Optional explicit Windows mixer session for RS-BA1 Remote Utility.
    // Never change the global endpoint volume or an unrelated application.
    public string? RsBa1PlaybackProcessName;
    public string? RsBa1PlaybackDeviceId;
    public bool SpeakerEnabled = true;

    [DisplayName("Recorder Audio Source")]
    [Description("Auto uses live SDR when enabled, otherwise the configured RS-BA1 playback endpoint; explicitly choose USB Capture or RS-BA1 Loopback when recording from an IC-9700.")]
    [DefaultValue(RecordingAudioSource.Auto)]
    public RecordingAudioSource RecorderSource { get; set; } = RecordingAudioSource.Auto;

    [DisplayName("Recorder USB AF Capture Device")]
    [Description("Windows capture/recording endpoint for radio USB AF (normally Microphone / USB Audio CODEC).")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? RecorderCaptureDeviceId { get; set; } = Soundcard.GetPreferredSoundcardId(DataFlow.Capture, "USB Audio CODEC", "ICOM");

    [DisplayName("Recorder RS-BA1 Playback Device")]
    [Description("Windows render/playback endpoint used by RS-BA1 Remote Utility, not the radio USB microphone input. WASAPI loopback captures the entire endpoint mix.")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? RecorderLoopbackDeviceId { get; set; }


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
