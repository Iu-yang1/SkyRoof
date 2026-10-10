using System.ComponentModel;
using Newtonsoft.Json;

namespace SkyRoof
{
  public enum IcomLanScopeBand
  {
    Auto = 0,
    Main = 1,
    Sub = 2
  }

  public enum IcomLanSpectrumSource
  {
    SkyCat = 0,
    RsBa1 = 1,
    DirectLan = 2
  }

  public enum IcomScopeControlPath
  {
    Auto = 0,
    SkyCat = 1,
    DirectLan = 2,
    ReadOnly = 3
  }

  public enum IcomScopeWaterfallPalette
  {
    Classic = 0,
    Grayscale = 1,
    Blue = 2,
    Heat = 3
  }

  public sealed class IcomScopeFixedEdgePreset
  {
    public long LowerHz { get; set; }
    public long UpperHz { get; set; }
  }

  public class IcomLanSpectrumSettings
  {
    [DisplayName("Radio IPv4 address")]
    [Description("IC-9700 IPv4 address. Optional for passive RS-BA1 capture, but required for the authenticated Direct LAN source.")]
    public string RadioAddress { get; set; } = "";

    [DisplayName("CI-V LAN port")]
    [Description("Icom LAN serial/CI-V UDP source port. IC-9700 default is 50002.")]
    [DefaultValue(50002)]
    public int SerialPort { get; set; } = 50002;

    [DisplayName("Scope band")]
    [Description("Auto shows whichever scope stream is present. MAIN or SUB filters CI-V 27 00 frames to that scope only.")]
    [DefaultValue(IcomLanScopeBand.Auto)]
    public IcomLanScopeBand ScopeBand { get; set; } = IcomLanScopeBand.Auto;

    [DisplayName("Scope source")]
    [Description("Direct LAN is an experimental, manually started authenticated IC-9700 network session. SkyCAT uses skycatd's loopback scope stream. RS-BA1 passively observes an existing RS-BA1 LAN session.")]
    [DefaultValue(IcomLanSpectrumSource.SkyCat)]
    public IcomLanSpectrumSource Source { get; set; } = IcomLanSpectrumSource.SkyCat;

    [DisplayName("Scope control path")]
    [Description("Select how spectrum-scope writes are sent independently of the waveform source. Auto uses SkyCAT for a SkyCAT source, leaves passive RS-BA1 read-only, and preserves experimental Direct LAN behavior. Select SkyCAT explicitly to control the radio while using RS-BA1 as the waveform source.")]
    [DefaultValue(IcomScopeControlPath.Auto)]
    public IcomScopeControlPath ControlPath { get; set; } =
      IcomScopeControlPath.Auto;

    [DisplayName("Estimated RX SSB filter width (Hz)")]
    [Description("Local red receive passband overlay only. IC-9700 nominal SSB FIL2 is 2400 Hz. Set to the radio's current BW if different. Does not change radio IF filters or Twin PBT.")]
    [DefaultValue(2400)]
    public int RxSsbEstimatedBandwidthHz { get; set; } = 2400;

    [DisplayName("Estimated RX SSB-D filter width (Hz)")]
    [Description("Local receive overlay only; nominal IC-9700 SSB-D FIL2 is 1200 Hz. Not an IF filter readback or write.")]
    [DefaultValue(1200)]
    public int RxDataEstimatedBandwidthHz { get; set; } = 1200;

    [DisplayName("Estimated RX CW filter width (Hz)")]
    [Description("Local receive overlay only; nominal IC-9700 CW FIL2 is 500 Hz. Assumes a filter centered at the displayed carrier; CW pitch/IF shift/PBT is not read back.")]
    [DefaultValue(500)]
    public int RxCwEstimatedBandwidthHz { get; set; } = 500;

    [DisplayName("Estimated RX FM/FM-D filter width (Hz)")]
    [Description("Local receive overlay only; nominal IC-9700 FM FIL1 is 15000 Hz. Select the width matching your radio's FM filter selection, such as 10000 or 7000.")]
    [DefaultValue(15000)]
    public int RxFmEstimatedBandwidthHz { get; set; } = 15000;

    [DisplayName("Scope edge number")]
    [Description("Selected IC-9700 fixed/scroll-fixed scope edge, 1 through 4.")]
    [DefaultValue(1)]
    public int ScopeEdgeNumber { get; set; } = 1;

    [DisplayName("Scope reference level")]
    [Description("IC-9700 scope reference level in dB, from -20.0 to +20.0 in 0.5 dB steps.")]
    [DefaultValue(0d)]
    public double ScopeReferenceLevelDb { get; set; } = 0;

    [DisplayName("Scope sweep speed")]
    [Description("IC-9700 scope sweep speed used by the explicit spectrum controls.")]
    [DefaultValue(IcomScopeSweepSpeed.Fast)]
    public IcomScopeSweepSpeed ScopeSweepSpeed { get; set; } =
      IcomScopeSweepSpeed.Fast;

    [Browsable(false)]
    [DefaultValue(false)]
    public bool ManageAdvancedScopeControls { get; set; }

    [DisplayName("Scope during TX")]
    [Description("IC-9700 CENTER-type scope display while transmitting.")]
    [DefaultValue(false)]
    public bool ScopeDuringTx { get; set; }

    [DisplayName("CENTER type display")]
    [Description("IC-9700 CENTER-type reference: filter center, carrier point, or absolute-frequency carrier point.")]
    [DefaultValue(IcomScopeCenterType.FilterCenter)]
    public IcomScopeCenterType ScopeCenterType { get; set; } =
      IcomScopeCenterType.FilterCenter;

    [DisplayName("Scope VBW")]
    [Description("IC-9700 scope video bandwidth. The current SkyRoof UI treats this as one operator preference and applies it to MAIN and SUB.")]
    [DefaultValue(IcomScopeVbw.Wide)]
    public IcomScopeVbw ScopeVbw { get; set; } =
      IcomScopeVbw.Wide;

    [DisplayName("Fixed/scroll marker position")]
    [Description("IC-9700 marker reference for FIXED and SCROLL modes.")]
    [DefaultValue(IcomScopeMarkerPosition.FilterCenter)]
    public IcomScopeMarkerPosition ScopeMarkerPosition { get; set; } =
      IcomScopeMarkerPosition.FilterCenter;


    [Browsable(false)]
    public Dictionary<string, IcomScopeFixedEdgePreset> FixedEdgePresets
      { get; set; } = new();

    [DisplayName("SkyCAT scope TCP port")]
    [Description("Loopback TCP port exported by skycatd for native IC-9700 scope frames.")]
    [DefaultValue(4535)]
    public int SkyCatScopePort { get; set; } = 4535;

    [DisplayName("Direct LAN control port")]
    [Description("IC-9700 authenticated LAN control port. The factory/default RS-BA1 control port is 50001.")]
    [DefaultValue(50001)]
    public int DirectLanControlPort { get; set; } = 50001;

    [DisplayName("Direct LAN username")]
    [Description("Network username configured in the IC-9700 for RS-BA1/LAN remote access.")]
    public string DirectLanUsername { get; set; } = "";

    [DisplayName("Direct LAN password")]
    [Description("Network password configured in the IC-9700 for RS-BA1/LAN remote access.")]
    [PasswordPropertyText(true)]
    [JsonIgnore]
    public string DirectLanPassword { get; set; } = "";

    [Browsable(false)]
    [JsonProperty("DirectLanPasswordProtected")]
    public string DirectLanPasswordProtected
    {
      get => SecretProtector.Protect(DirectLanPassword);
      set => DirectLanPassword = SecretProtector.Unprotect(value);
    }

    [Browsable(false)]
    [JsonProperty("DirectLanPassword", NullValueHandling = NullValueHandling.Ignore)]
    private string? LegacyDirectLanPassword
    {
      get => null;
      set
      {
        if (value == null) return;
        DirectLanPassword = value;
        SecretMigrationNeeded = true;
      }
    }

    [JsonIgnore]
    [Browsable(false)]
    internal bool SecretMigrationNeeded { get; private set; }

    [DisplayName("Direct LAN client name")]
    [Description("Client name presented to the Icom LAN server. 'icom-pc' matches the conservative RS-BA1-compatible baseline.")]
    [DefaultValue("icom-pc")]
    public string DirectLanClientName { get; set; } = "icom-pc";

    [DisplayName("Auto start")]
    [Description("Start SkyCAT or passive RS-BA1 capture automatically when the panel opens. Experimental Direct LAN always requires a manual Start.")]
    [DefaultValue(true)]
    public bool AutoStart { get; set; } = true;

    [DisplayName("Spectrum averaging")]
    [Description("Number of complete IC-9700 sweeps averaged for the local spectrum trace. Waterfall rows remain unaveraged.")]
    [DefaultValue(1)]
    public int SpectrumAverageSweeps { get; set; } = 1;

    [DisplayName("Spectrum smoothing")]
    [Description("Odd-numbered frequency-bin moving average applied only to the local spectrum and peak traces. Waterfall data remains raw.")]
    [DefaultValue(1)]
    public int SpectrumSmoothingBins { get; set; } = 1;

    [DisplayName("Waterfall history")]
    [Description("Number of raw 475-bin scope rows retained by the display.")]
    [DefaultValue(240)]
    public int WaterfallRows { get; set; } = 240;

    [DisplayName("Spectrum height")]
    [Description("Percentage of the spectrum/waterfall display allocated to the spectrum trace. Drag the divider in the spectrum view to change it.")]
    [DefaultValue(36)]
    public int SpectrumHeightPercent { get; set; } = 36;

    [DisplayName("Show waterfall")]
    [Description("Show the waterfall below the spectrum trace.")]
    [DefaultValue(true)]
    public bool ShowWaterfall { get; set; } = true;

    [DisplayName("Waterfall brightness")]
    [Description("Display-level offset applied to waterfall colors. Valid useful range is about -80 to +80.")]
    [DefaultValue(0)]
    public int WaterfallBrightness { get; set; } = 0;

    [DisplayName("Waterfall contrast")]
    [Description("Waterfall contrast in percent. 100 preserves the raw IC-9700 scope level mapping.")]
    [DefaultValue(100)]
    public int WaterfallContrast { get; set; } = 100;

    [DisplayName("Waterfall palette")]
    [Description("Color palette used by the local SkyRoof waterfall renderer.")]
    [DefaultValue(IcomScopeWaterfallPalette.Classic)]
    public IcomScopeWaterfallPalette WaterfallPalette { get; set; } =
      IcomScopeWaterfallPalette.Classic;

    public override string ToString() => string.Empty;
  }
}
