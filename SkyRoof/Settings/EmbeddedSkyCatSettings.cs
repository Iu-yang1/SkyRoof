using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using skycatd;

namespace SkyRoof
{
  /// <summary>
  /// Opt-in, loopback-only SkyCAT server. Default-disabled so existing CAT,
  /// rotctld, RS-BA1 and operator-owned serial configurations are untouched.
  /// </summary>
  public sealed class EmbeddedSkyCatSettings
  {
    [DefaultValue(false)]
    [DisplayName("Enable Embedded SkyCAT")]
    [Description("Run SkyCAT inside SkyRoof. One owner of the rig's serial port; do not start an external skycatd on the same COM port.")]
    public bool Enabled { get; set; }

    [DefaultValue("3081")]
    [DisplayName("Radio Model")]
    [TypeConverter(typeof(EmbeddedSkyCatModelConverter))]
    [Description("Select a model from the SkyCAT rig definitions (e.g. IC-9700 / 3081). Custom model IDs and names can still be entered.")]
    public string Model { get; set; } = "3081";

    [DefaultValue("")]
    [DisplayName("CI-V Serial Port")]
    [TypeConverter(typeof(EmbeddedSkyCatSerialPortConverter))]
    [Description("Select a detected Windows COM port. Reopen the dropdown to scan again; disconnected saved ports and manual entries are supported. Never open a port in two CAT servers.")]
    public string SerialPort { get; set; } = "";

    [DefaultValue(115200)]
    [DisplayName("Serial Baud Rate")]
    public int BaudRate { get; set; } = 115200;

    [DefaultValue(4532)]
    [DisplayName("Main CAT TCP Port")]
    public int CatPort { get; set; } = 4532;

    [DefaultValue(true)]
    [DisplayName("WSJT-X Proxy Enabled")]
    public bool WsjtXEnabled { get; set; } = true;

    [DefaultValue(4534)]
    [DisplayName("WSJT-X TCP Port")]
    public int WsjtXPort { get; set; } = 4534;

    [DefaultValue(4535)]
    [DisplayName("Spectrum TCP Port")]
    public int SpectrumPort { get; set; } = 4535;

    [DefaultValue(true)]
    [DisplayName("Switch Enabled")]
    public bool SwitchEnabled { get; set; } = true;

    [DefaultValue(4537)]
    [DisplayName("Switch TCP Port")]
    public int SwitchPort { get; set; } = 4537;

    [DefaultValue(true)]
    [DisplayName("CW Keyer Enabled")]
    public bool CwEnabled { get; set; } = true;

    [DefaultValue(4538)]
    [DisplayName("CW Keyer TCP Port")]
    public int CwPort { get; set; } = 4538;

    internal string? Validate()
    {
      if(!Enabled)return null;
      if(string.IsNullOrWhiteSpace(Model))
        return "SkyCAT radio model is required.";
      if(string.IsNullOrWhiteSpace(SerialPort))
        return "Set the CI-V serial port before enabling embedded SkyCAT.";
      if(BaudRate<300 || BaudRate>4000000)
        return "SkyCAT baud rate is out of range.";
      var ports=new List<int>{CatPort,SpectrumPort};
      if(WsjtXEnabled)ports.Add(WsjtXPort);
      if(SwitchEnabled)ports.Add(SwitchPort);
      if(CwEnabled)ports.Add(CwPort);
      if(ports.Any(p=>p<1||p>65535))
        return "SkyCAT TCP ports must be between 1 and 65535.";
      if(ports.Count!=ports.Distinct().Count())
        return "Embedded SkyCAT TCP ports must be unique.";
      return null;
    }

    internal Options BuildOptions() => new()
    {
      Model=Model.Trim(),
      RigFile=SerialPort.Trim(),
      SerialSpeed=BaudRate,
      Port=CatPort,
      AllowRemote=false,
      WsjtXPort=WsjtXPort,
      DisableWsjtXProxy=!WsjtXEnabled,
      ScopePort=SpectrumPort,
      SwitchPort=SwitchPort,
      DisableSwitchPort=!SwitchEnabled,
      CwPort=CwPort,
      DisableCwPort=!CwEnabled,
      FileLog=false
    };

    internal string ConfigurationKey() =>
      string.Join("|",Enabled,Model.Trim(),SerialPort.Trim(),BaudRate,
        CatPort,WsjtXEnabled,WsjtXPort,SpectrumPort,
        SwitchEnabled,SwitchPort,CwEnabled,CwPort);

    public override string ToString() =>
      Enabled ? $"{EmbeddedSkyCatModelConverter.GetDisplayName(Model)} / {SerialPort} (child process)" : "Off";
  }
}
