using System.ComponentModel;

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
    public string DirectLanPassword { get; set; } = "";

    [DisplayName("Direct LAN client name")]
    [Description("Client name presented to the Icom LAN server. 'icom-pc' matches the conservative RS-BA1-compatible baseline.")]
    [DefaultValue("icom-pc")]
    public string DirectLanClientName { get; set; } = "icom-pc";

    [DisplayName("Auto start")]
    [Description("Start SkyCAT or passive RS-BA1 capture automatically when the panel opens. Experimental Direct LAN always requires a manual Start.")]
    [DefaultValue(true)]
    public bool AutoStart { get; set; } = true;

    [DisplayName("Waterfall history")]
    [Description("Number of raw 475-bin scope rows retained by the display.")]
    [DefaultValue(240)]
    public int WaterfallRows { get; set; } = 240;

    public override string ToString() => string.Empty;
  }
}
