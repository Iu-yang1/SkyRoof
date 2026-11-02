using System;
using System.ComponentModel;
using System.Globalization;
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

  // Enum numeric values are the nominal full two-sided IF / AF bandwidth.
  [TypeConverter(typeof(IfAfSsbPresetConverter))]
  public enum IfAfSsbBandwidth
  {
    Narrow1_2k = 1200, Narrow1_8k = 1800, Speech2_1k = 2100,
    Speech2_4k = 2400, Normal2_7k = 2700, Wide3k = 3000,
    Wide3_3k = 3300
  }
  [TypeConverter(typeof(IfAfCwPresetConverter))]
  public enum IfAfCwBandwidth
  {
    UltraNarrow100 = 100, VeryNarrow200 = 200, Narrow300 = 300,
    Narrow400 = 400, Normal500 = 500, Wide800 = 800, Wide1000 = 1000
  }
  [TypeConverter(typeof(IfAfFmPresetConverter))]
  public enum IfAfFmBandwidth { Narrow6_25k = 6250, Normal12k = 12000 }

  // Display friendly 1.2 kHz / 300 Hz labels in the PropertyGrid dropdown
  // while persisting stable enum integers in Settings.json.
  public abstract class IfAfBandwidthPresetConverter : EnumConverter
  {
    protected IfAfBandwidthPresetConverter(Type type) : base(type) { }

    private string Label(Enum value)
    {
      int hz = Convert.ToInt32(value, CultureInfo.InvariantCulture);
      if (EnumType == typeof(IfAfCwBandwidth))
        return hz.ToString(CultureInfo.InvariantCulture) + " Hz";
      return (hz == 12000 && EnumType == typeof(IfAfFmBandwidth)
        ? "12.5" : (hz / 1000.0).ToString(
          EnumType == typeof(IfAfSsbBandwidth) ? "0.0" : "0.##",
          CultureInfo.InvariantCulture)) + " kHz";
    }

    public override object? ConvertTo(ITypeDescriptorContext? context,
      CultureInfo? culture, object? value, Type destinationType)
    {
      if (destinationType == typeof(string) && value is Enum entry &&
          entry.GetType() == EnumType)
        return Label(entry);
      return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context,
      CultureInfo? culture, object value)
    {
      if (value is string label)
        foreach (Enum entry in Enum.GetValues(EnumType))
          if (string.Equals(Label(entry), label.Trim(),
            StringComparison.OrdinalIgnoreCase))
            return entry;
      return base.ConvertFrom(context, culture, value);
    }
  }
  public sealed class IfAfSsbPresetConverter : IfAfBandwidthPresetConverter
  {
    public IfAfSsbPresetConverter() : base(typeof(IfAfSsbBandwidth)) { }
  }
  public sealed class IfAfCwPresetConverter : IfAfBandwidthPresetConverter
  {
    public IfAfCwPresetConverter() : base(typeof(IfAfCwBandwidth)) { }
  }
  public sealed class IfAfFmPresetConverter : IfAfBandwidthPresetConverter
  {
    public IfAfFmPresetConverter() : base(typeof(IfAfFmBandwidth)) { }
  }

  public enum IfAfFmVoiceProcessing { Off, Voice50us, Voice75us }
  public enum IfAfSsbVoiceNotch { Off, Tone1000Hz, Tone1500Hz }

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

    [Category("Receive filters")]
    [DisplayName("SSB bandwidth")]
    [Description("USB / LSB audio passband width. Presets: 1.2, 1.8, 2.1, 2.4, 2.7, 3.0 and 3.3 kHz. 2.7 kHz is normal voice, 3.0/3.3 kHz is wider voice / digital. All modes use an 180-Hz lower edge.")]
    [DefaultValue(IfAfSsbBandwidth.Wide3k)]
    public IfAfSsbBandwidth SsbBandwidth { get; set; } = IfAfSsbBandwidth.Wide3k;

    [Category("Receive filters")]
    [DisplayName("CW bandwidth")]
    [Description("CW audio bandpass centered at 700 Hz. Choose 100, 200, 300, 400, 500, 800 or 1000 Hz. 100/200 Hz are for well-centered, stable CW; 500 Hz is normal. Applies to both CW sidebands.")]
    [DefaultValue(IfAfCwBandwidth.Normal500)]
    public IfAfCwBandwidth CwBandwidth { get; set; } = IfAfCwBandwidth.Normal500;

    [Category("Receive filters")]
    [DisplayName("FM bandwidth")]
    [Description("FM channel-spacing presets: 6.25 kHz (6 kHz IF, 2.1 kHz AF, ±0.9 kHz deviation) and 12.5 kHz (11 kHz IF, 3.0 kHz AF, ±2.5 kHz deviation).")]
    [DefaultValue(IfAfFmBandwidth.Normal12k)]
    public IfAfFmBandwidth FmBandwidth { get; set; } = IfAfFmBandwidth.Normal12k;

    // Keep older numeric filter fields readable without exposing them in the
    // UI or writing redundant, potentially conflicting JSON settings.
    private int? legacySsbLow, legacySsbHigh;
    [JsonProperty("SsbLowHz", NullValueHandling = NullValueHandling.Ignore)]
    private int? LegacySsbLow { get => null; set => legacySsbLow = value; }
    [JsonProperty("SsbHighHz", NullValueHandling = NullValueHandling.Ignore)]
    private int? LegacySsbHigh { get => null; set => legacySsbHigh = value; }
    [JsonProperty("FmDeviationHz", NullValueHandling = NullValueHandling.Ignore)]
    private int? LegacyFmDeviation { get => null; set { } }

    [JsonIgnore]
    public int EffectiveSsbLowHz => 180;
    [JsonIgnore]
    public int EffectiveSsbHighHz => EffectiveSsbLowHz + (int)SsbBandwidth;
    [JsonIgnore]
    public int EffectiveCwCenterHz => 700;
    [JsonIgnore]
    public int EffectiveCwBandwidthHz => (int)CwBandwidth;
    [JsonIgnore]
    public int EffectiveFmIfCutoffHz =>
      FmBandwidth == IfAfFmBandwidth.Narrow6_25k ? 3000 : 5500;
    [JsonIgnore]
    public int EffectiveFmAfCutoffHz =>
      FmBandwidth == IfAfFmBandwidth.Narrow6_25k ? 2100 : 3000;
    [JsonIgnore]
    public int EffectiveFmDeviationHz =>
      FmBandwidth == IfAfFmBandwidth.Narrow6_25k ? 900 : 2500;

    [Category("Audio enhancements")]
    [DisplayName("FM voice processing")]
    [Description("Off preserves discriminator audio (digital/telemetry safe). Voice50us / Voice75us add the corresponding analog FM de-emphasis after AF filtering.")]
    [DefaultValue(IfAfFmVoiceProcessing.Off)]
    public IfAfFmVoiceProcessing FmVoiceProcessing { get; set; } =
      IfAfFmVoiceProcessing.Off;

    [Category("Audio enhancements")]
    [DisplayName("FM voice squelch")]
    [Description("Optional RF power-based voice squelch only. Off by default so weak-signal digital/FM data and AFC raw IF are never silently muted.")]
    [DefaultValue(false)]
    public bool FmVoiceSquelchEnabled { get; set; } = false;

    [Category("Audio enhancements")]
    [DisplayName("FM voice squelch threshold (dBFS)")]
    [Description("Filtered complex IF carrier-power threshold. Calibrate using the IF RMS; not an RF S-meter or calibrated SINAD.")]
    [DefaultValue(-45)]
    public int FmVoiceSquelchDbfs { get; set; } = -45;

    [Category("Audio enhancements")]
    [DisplayName("SSB voice notch")]
    [Description("Optional narrow fixed-frequency audio notch to suppress a stable 1000/1500 Hz interfering tone. Disabled for USB-D/LSB-D and CW.")]
    [DefaultValue(IfAfSsbVoiceNotch.Off)]
    public IfAfSsbVoiceNotch SsbVoiceNotch { get; set; } =
      IfAfSsbVoiceNotch.Off;

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
      // Only migrate old SSB edges if no newer explicit bandwidth was saved.
      // Default older passband 180-3300 Hz maps to the closest 3.0-kHz preset.
      if (SsbBandwidth == IfAfSsbBandwidth.Wide3k &&
          legacySsbLow.HasValue && legacySsbHigh.HasValue)
      {
        int width = legacySsbHigh.Value - legacySsbLow.Value;
        SsbBandwidth = width <= 1900 ? IfAfSsbBandwidth.Narrow1_2k :
          width <= 2850 ? IfAfSsbBandwidth.Normal2_7k : IfAfSsbBandwidth.Wide3k;
      }
      legacySsbLow = legacySsbHigh = null;
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
        Slicer.Mode.LSB => IfAfMode.Lsb,
        Slicer.Mode.USB_D => IfAfMode.UsbDigital,
        Slicer.Mode.LSB_D => IfAfMode.LsbDigital,
        Slicer.Mode.CW => IfAfMode.CwUsb,
        Slicer.Mode.FM or Slicer.Mode.FM_D => IfAfMode.Fm,
        _ => IfAfMode.Usb
      };
    }
    public override string ToString() => Enabled ? "Enabled" : "Disabled";
  }
}
