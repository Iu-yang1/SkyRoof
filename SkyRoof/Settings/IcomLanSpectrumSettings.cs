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
    RsBa1 = 1
  }

  public class IcomLanSpectrumSettings
  {
    [DisplayName("Radio IPv4 address")]
    [Description("Optional IC-9700 IPv4 address. Leave empty to sniff all inbound UDP packets whose source port is the configured CI-V LAN port.")]
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
    [Description("Select who owns/enables IC-9700 scope waveform output. SkyCAT actively reasserts CI-V 27 10 / 27 11; RS-BA1 leaves scope control to the RS-BA1 Spectrum Scope window and SkyRoof only sniffs the LAN stream.")]
    [DefaultValue(IcomLanSpectrumSource.SkyCat)]
    public IcomLanSpectrumSource Source { get; set; } = IcomLanSpectrumSource.SkyCat;

    [DisplayName("Auto start")]
    [Description("Start passive WinDivert capture automatically when the Icom LAN Spectrum panel is opened.")]
    [DefaultValue(true)]
    public bool AutoStart { get; set; } = true;

    [DisplayName("Waterfall history")]
    [Description("Number of raw 475-bin scope rows retained by the display.")]
    [DefaultValue(240)]
    public int WaterfallRows { get; set; } = 240;

    public override string ToString() => string.Empty;
  }
}
