using System;
using System.ComponentModel;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof.IF
{
  public enum IfAfSelection { FollowRadio, Usb, Lsb, CwUsb, CwLsb, Fm }

  // Keep the persisted numeric values 1 and 2. Legacy value 0 (or its
  // previous string label) is migrated when older Settings.json is loaded.
  [JsonConverter(typeof(IfAfAudioRouteJsonConverter))]
  public enum IfAfAudioRoute { ExistingVirtualCable = 1, AutoSignedCable = 2 }

  public sealed class IfAfAudioRouteJsonConverter : JsonConverter<IfAfAudioRoute>
  {
    public override IfAfAudioRoute ReadJson(JsonReader reader, Type objectType,
      IfAfAudioRoute existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
      string? value = Convert.ToString(reader.Value);
      return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, nameof(IfAfAudioRoute.ExistingVirtualCable),
          StringComparison.OrdinalIgnoreCase)
        ? IfAfAudioRoute.ExistingVirtualCable : IfAfAudioRoute.AutoSignedCable;
    }

    public override void WriteJson(JsonWriter writer, IfAfAudioRoute value,
      JsonSerializer serializer) =>
      writer.WriteValue((int)(value == IfAfAudioRoute.ExistingVirtualCable
        ? IfAfAudioRoute.ExistingVirtualCable : IfAfAudioRoute.AutoSignedCable));
  }

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
    [Description("AutoSignedCable (recommended) automatically pairs VB-CABLE / VAC Lite playback and recording endpoints. ExistingVirtualCable uses a manually selected playback endpoint without automatic capture pairing.")]
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

    // Legacy Settings.json contained one global GainDb. Read it, but never
    // write that ambiguous key again. Preserve a user's safe FM attenuation;
    // an old 0/-12 dB value is migrated to the safe -15 dB baseline.
    private int? legacyGainDb;
    private int fmGainDb = -15;
    private bool explicitFmGain;
    [JsonProperty("GainDb", NullValueHandling = NullValueHandling.Ignore)]
    private int? LegacyGainDb
    {
      get => null;
      set => legacyGainDb = value;
    }

    [OnDeserialized]
    private void MigrateLegacyAudioGain(StreamingContext context)
    {
      if (!explicitFmGain && legacyGainDb.HasValue)
        fmGainDb = Math.Min(-15, legacyGainDb.Value);
      legacyGainDb = null;
    }

    [DisplayName("SSB output trim (dB)")]
    [Description("Manual adjustment after SSB AGC. AGC controls long-term RMS; this trim is independent of FM and CW.")]
    [DefaultValue(0)]
    public int SsbGainDb { get; set; } = 0;

    [DisplayName("CW output trim (dB)")]
    [Description("Manual adjustment after the slow CW AGC. A slow release preserves keying gaps.")]
    [DefaultValue(0)]
    public int CwGainDb { get; set; } = 0;

    [DisplayName("FM output gain (dB)")]
    [Description("FM discriminator is frequency-deviation-normalized and needs its own gain; default -15 dB. Does not change SSB/CW volume.")]
    [DefaultValue(-15)]
    public int FmGainDb
    {
      get => fmGainDb;
      set { fmGainDb = value; explicitFmGain = true; }
    }

    [DisplayName("SSB/CW slow automatic gain control")]
    [Description("Bounded slow audio AGC after demodulation. Preserves the raw 12 kHz IF for AFC and avoids rapid pumping of FT4/FT8 or CW envelopes.")]
    [DefaultValue(true)]
    public bool SsbCwAgcEnabled { get; set; } = true;

    [DisplayName("SSB/CW AGC target RMS (dBFS)")]
    [Description("Target RMS for sufficiently strong demodulated audio; default -24 dBFS, not a guaranteed signal level.")]
    [DefaultValue(-24)]
    public int SsbCwAgcTargetDbfs { get; set; } = -24;

    [DisplayName("SSB/CW AGC max gain (dB)")]
    [Description("Maximum leveler boost. Capped to prevent unlimited amplification of receiver noise.")]
    [DefaultValue(24)]
    public int SsbCwAgcMaxGainDb { get; set; } = 24;

    [DisplayName("FM peak deviation (Hz)")]
    [Description("2500 Hz for narrow FM, 5000 Hz for wider FM. Must match the incoming modulation.")]
    [DefaultValue(2500)]
    public int FmDeviationHz { get; set; } = 2500;

    /// <summary>Resolve the vendor render endpoint; never fall back to radio TX.</summary>
    public static string? ResolveRenderEndpoint(IfAfAudioRoute route,
      string? manualRenderId, string? signedRenderId) =>
      route switch
      {
        IfAfAudioRoute.AutoSignedCable => signedRenderId,
        IfAfAudioRoute.ExistingVirtualCable => manualRenderId,
        _ => null
      };

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
