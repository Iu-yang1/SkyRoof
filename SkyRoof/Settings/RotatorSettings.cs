using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyRoof
{
  // Auto=0 is retained only to import older Settings.json files.
  // The PropertyGrid offers exactly Native / LegacyRotctld.
  public enum RotatorDriverMode { Auto = 0, Native = 1, LegacyRotctld = 2 }

  // Mirror the serial port enum values while keeping Settings itself free
  // of System.IO.Ports types. An absent optional driver DLL must not crash
  // SkyRoof's constructor before the user can even open Settings.
  public enum RotatorSerialParity { None = 0, Odd = 1, Even = 2, Mark = 3, Space = 4 }
  public enum RotatorSerialStopBits { None = 0, One = 1, Two = 2, OnePointFive = 3 }
  public enum RotatorSerialHandshake { None = 0, XOnXOff = 1, RequestToSend = 2, RequestToSendXOnXOff = 3 }

  public enum Rc3040sMotionProfile { Direct = 0, PrecisionStop = 1, Trapezoidal = 2, SCurve = 3 }

  public class RotatorSettings : IControlEngineSettings
  {
    [DisplayName("Controller Driver")]
    [Description("Native directly controls the configured serial/TCP/UDP hardware. LegacyRotctld connects to an external Hamlib server.")]
    [TypeConverter(typeof(RotatorDriverModeConverter))]
    [DefaultValue(RotatorDriverMode.Native)]
    public RotatorDriverMode DriverMode { get; set; } = RotatorDriverMode.Native;

    [Browsable(false)]
    [Description("Old settings-file field. Use DriverMode to select a rotator driver.")]
    public bool UseNativeDriver { get; set; } = false;

    // Migrate a previous Auto entry once at settings load. Explicit choices
    // remain untouched; custom remote rotctld endpoints are preserved.
    public void MigrateDriverMode()
    {
      if (DriverMode != RotatorDriverMode.Auto) return;
      DriverMode = ResolveUseNativeDriver()
        ? RotatorDriverMode.Native
        : RotatorDriverMode.LegacyRotctld;
    }

    public bool ResolveUseNativeDriver()
    {
      if (DriverMode == RotatorDriverMode.Native) return true;
      if (DriverMode == RotatorDriverMode.LegacyRotctld) return false;

      // An existing user may have a nondefault remote rotctld server;
      // silently reinterpreting its TCP address as a native serial port is
      // undesirable. Default localhost:4533 migrates automatically to
      // native COM control. Explicit old Native=true is preserved.
      if (UseNativeDriver || NativeTransport != NativeRotator.RotatorTransportKind.Serial)
        return true;
      bool local = Host is "127.0.0.1" or "localhost" or "::1";
      return local && Port == 4533;
    }

    private SkyRoof.NativeRotator.RotatorProtocolKind nativeProtocol = SkyRoof.NativeRotator.RotatorProtocolKind.Gs232;
    [DisplayName("Native Protocol")]
    [DefaultValue(SkyRoof.NativeRotator.RotatorProtocolKind.Gs232)]
    public SkyRoof.NativeRotator.RotatorProtocolKind NativeProtocol
    {
      get => nativeProtocol;
      set { nativeProtocol=value; if(value==SkyRoof.NativeRotator.RotatorProtocolKind.DtracNrl2)
        NativeTransport=SkyRoof.NativeRotator.RotatorTransportKind.Udp;
        else if(value==SkyRoof.NativeRotator.RotatorProtocolKind.Rc3040sLan)
        { NativeTransport=SkyRoof.NativeRotator.RotatorTransportKind.Tcp;
          if(Port==4533)Port=60020; } }
    }

    [DisplayName("Native Transport")]
    [DefaultValue(SkyRoof.NativeRotator.RotatorTransportKind.Serial)]
    public SkyRoof.NativeRotator.RotatorTransportKind NativeTransport { get; set; } = SkyRoof.NativeRotator.RotatorTransportKind.Serial;

    [DisplayName("NRL2 Server Host")]
    [Description("DTrac-compatible relay host (not the RC-3040S LAN address).")]
    public string DtracNrlServerHost { get; set; } = "";

    [DisplayName("NRL2 Server Port")]
    [DefaultValue((ushort)60050)]
    public ushort DtracNrlServerPort { get; set; } = 60050;

    [DisplayName("RC-3040S Rotor ID")]
    [Editor(typeof(SkyRoof.NativeRotator.DtracRotorIdEditor),typeof(System.Drawing.Design.UITypeEditor))]
    public string DtracNrlRotorId { get; set; } = "";

    [DisplayName("NRL2 Client SID")]
    [Description("Persistent 14-hex-character client ID, generated locally.")]
    public string DtracNrlAppSid { get; set; } =
      Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(7));

    [DisplayName("DTrac Single Target PARK")]
    [Description("PARK is a single firmware-positioned W movement, not mechanical zero calibration.")]
    [DefaultValue(false)]
    public bool DtracUseNativePark { get; set; } = false;

    [DisplayName("DTrac DEMO Start AZ")]
    [DefaultValue(0d)]
    public double DtracDemoStartAz { get; set; } = 0;
    [DisplayName("DTrac DEMO End AZ")]
    [DefaultValue(180d)]
    public double DtracDemoEndAz { get; set; } = 180;
    [DisplayName("DTrac DEMO EL")]
    [DefaultValue(30d)]
    public double DtracDemoElevation { get; set; } = 30;
    [DisplayName("DTrac DEMO Step")]
    [DefaultValue(10d)]
    public double DtracDemoStepDegrees { get; set; } = 10;
    [DisplayName("DTrac DEMO Dwell Seconds")]
    [DefaultValue(3)]
    public int DtracDemoDwellSeconds { get; set; } = 3;
    [DisplayName("DTrac DEMO Cycles")]
    [DefaultValue(1)]
    public int DtracDemoCycles { get; set; } = 1;

    [DisplayName("RC-3040S Motion Profile")]
    [DefaultValue(Rc3040sMotionProfile.Direct)]
    public Rc3040sMotionProfile Rc3040sMotionMode { get; set; } = Rc3040sMotionProfile.Direct;

    [DisplayName("RC-3040S Motion Planner Enabled")]
    [Description("Opt-in software setpoint planning; not native hardware speed control.")]
    [DefaultValue(false)]
    public bool Rc3040sPlannerEnabled { get; set; }

    [DisplayName("RC-3040S AZ Planned Speed (deg/s)")]
    [DefaultValue(2.0)]
    public double Rc3040sPlanAzSpeed { get; set; } = 2.0;

    [DisplayName("RC-3040S EL Planned Speed (deg/s)")]
    [DefaultValue(2.0)]
    public double Rc3040sPlanElSpeed { get; set; } = 2.0;

    [DisplayName("RC-3040S Acceleration (deg/s²)")]
    [DefaultValue(0.8)]
    public double Rc3040sPlanAcceleration { get; set; } = 0.8;

    [DisplayName("RC-3040S Jerk (deg/s³)")]
    [DefaultValue(1.5)]
    public double Rc3040sPlanJerk { get; set; } = 1.5;

    [DisplayName("RC-3040S AT Readback")]
    [Description("WebSocket/60030 on-demand management: read-only unless explicitly saved.")]
    [DefaultValue(true)]
    public bool Rc3040sAtEnabled { get; set; } = true;

    [DisplayName("Serial Port")]
    [DefaultValue("COM9")]
    public string NativeSerialPort { get; set; } = "COM9";

    [DisplayName("Baud Rate")]
    [DefaultValue(9600)]
    public int NativeBaudRate { get; set; } = 9600;

    [DisplayName("Serial Data Bits")]
    [DefaultValue(8)]
    public int NativeDataBits { get; set; } = 8;

    [DisplayName("Serial Parity")]
    [DefaultValue(RotatorSerialParity.None)]
    public RotatorSerialParity NativeParity { get; set; } = RotatorSerialParity.None;

    [DisplayName("Serial Stop Bits")]
    [DefaultValue(RotatorSerialStopBits.One)]
    public RotatorSerialStopBits NativeStopBits { get; set; } = RotatorSerialStopBits.One;

    [DisplayName("Serial Handshake")]
    [DefaultValue(RotatorSerialHandshake.None)]
    public RotatorSerialHandshake NativeHandshake { get; set; } = RotatorSerialHandshake.None;

    [DisplayName("Serial DTR Enabled")]
    [DefaultValue(false)]
    public bool NativeDtrEnable { get; set; } = false;

    [DisplayName("Serial RTS Enabled")]
    [Description("Only used when the handshake is not RequestToSend.")]
    [DefaultValue(false)]
    public bool NativeRtsEnable { get; set; } = false;

    [DisplayName("Native Poll Interval (ms)")]
    [DefaultValue(1000)]
    public int NativePollMs { get; set; } = 1000;

    [DisplayName("Native Response Timeout (ms)")]
    [DefaultValue(600)]
    public int NativeReadTimeout { get; set; } = 600;

    [DisplayName("Allow Open Loop Motion")]
    [Description("Explicit opt-in for protocols with no feedback or STOP. Never enable without checking real mechanical safety.")]
    [DefaultValue(false)]
    public bool NativeAllowOpenLoopMotion { get; set; } = false;

    [DisplayName("Custom Point Command")]
    [DefaultValue("P $AZ $EL\\n")]
    public string NativePointTemplate { get; set; } = "P $AZ $EL\\n";
    [DisplayName("Custom STOP Command")]
    [DefaultValue("")]
    public string NativeStopTemplate { get; set; } = "";
    [DisplayName("Custom Query Command")]
    [DefaultValue("")]
    public string NativeQueryTemplate { get; set; } = "";
    [DisplayName("Custom Response Format")]
    [DefaultValue("")]
    public string NativeResponseTemplate { get; set; } = "";

    [DefaultValue(300)]
    [Description("Delay between the command cycles, ms")]
    public int Delay { get; set; } = 300;

    [DisplayName("Log Traffic")]
    [Description("Log command traffic for debugging")]
    [DefaultValue(false)]
    public bool LogTraffic { get; set; }

    [DisplayName("Send Timeout")]
    [Description("TCP send timeout in milliseconds")]
    [DefaultValue(2000)]
    public int SendTimeout { get; set; } = 2000;

    [DisplayName("Receive Timeout")]
    [Description("TCP receive timeout in milliseconds. Increase for high-latency networks.")]
    [DefaultValue(5000)]
    public int ReceiveTimeout { get; set; } = 5000;

    [DisplayName("Reconnect Delay")]
    [Description("Milliseconds to wait between reconnect attempts after a connection is lost.")]
    [DefaultValue(5000)]
    public int ReconnectDelay { get; set; } = 5000;

    [DefaultValue(false)]
    public bool Enabled { get; set; } = false;

    [DisplayName("Rotator Network Host")]
    [Description("Host/IP for native TCP or UDP controller, or legacy rotctld TCP server.")]
    [DefaultValue("127.0.0.1")]
    public string Host { get; set; } = "127.0.0.1";

    [DisplayName("Rotator Network Port")]
    [Description("Remote native TCP/UDP controller port, or legacy rotctld port.")]
    [DefaultValue((ushort)4533)]
    public ushort Port { get; set; } = 4533;


    [DisplayName("Minimum Azimuth")]
    [DefaultValue(0)]
    public int MinAzimuth { get; set; } = 0;

    [DisplayName("Maximum Azimuth")]
    [DefaultValue(450)]
    public int MaxAzimuth { get; set; } = 450;

    [DisplayName("Azimuth Offset")]
    [DefaultValue(0)]
    public int AzimuthOffset { get; set; } = 0;

    [DisplayName("Minimum Elevation")]
    [DefaultValue(0)]
    public int MinElevation{ get; set; } = 0;

    [DisplayName("Maximum Elevation")]
    [DefaultValue(180)]
    public int MaxElevation { get; set; } = 180;

    [DisplayName("Elevation Offset")]
    [DefaultValue(0)]
    public int ElevationOffset { get; set; } = 0;

    [DisplayName("Tracking Step Size")]
    [Description("Automatic satellite-tracking step size in degrees. Manual direction-wheel hold uses continuous move/stop commands and is not stepped.")]
    [DefaultValue(5f)]
    public float StepSize { get; set; } = 5f;

    [DisplayName("Position Command Resolution (deg)")]
    [Description("Effective backend positioning resolution in degrees. Use 1.0 for Hamlib GS-232B (integer-degree W commands); default 0.1 preserves other models. Duplicate positions at the selected resolution are not sent again.")]
    [DefaultValue(0.1)]
    public double PositionCommandResolutionDegrees { get; set; } = 0.1;

    [DisplayName("Position Jump Limit")]
    [Description("Stop and latch a safety warning if consecutive fresh rotator position readings differ by more than this many degrees. Reapply rotator settings after checking the antenna.")]
    [DefaultValue(30)]
    public int PositionJumpLimitDegrees { get; set; } = 30;

    [DisplayName("Parking Azimuth")]
    [Description("Azimuth of the parking position")]
    [DefaultValue(0f)]
    public float ParkAzimuth { get; set; }

    [DisplayName("Parking Elevation")]
    [Description("Elevation of the parking position")]
    [DefaultValue(0f)]
    public float ParkElevation { get; set; }

    [Browsable(false)]
    [Description("Ordered PARK waypoints. Empty means legacy single-point parking.")]
    public List<RotatorParkWaypoint> ParkWaypoints { get; set; } = new();

    public override string ToString() { return string.Empty; }
  }
}
