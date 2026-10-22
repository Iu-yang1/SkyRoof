using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyRoof
{
  public class RotatorSettings : IControlEngineSettings
  {
    [DisplayName("Controller Driver")]
    [Description("Native: directly control serial/TCP/UDP rotators without a Hamlib daemon. Legacy: connect to an existing rotctld server.")]
    [DefaultValue(true)]
    public bool UseNativeDriver { get; set; } = true;

    [DisplayName("Native Protocol")]
    [DefaultValue(SkyRoof.NativeRotator.RotatorProtocolKind.Gs232)]
    public SkyRoof.NativeRotator.RotatorProtocolKind NativeProtocol { get; set; } = SkyRoof.NativeRotator.RotatorProtocolKind.Gs232;

    [DisplayName("Native Transport")]
    [DefaultValue(SkyRoof.NativeRotator.RotatorTransportKind.Serial)]
    public SkyRoof.NativeRotator.RotatorTransportKind NativeTransport { get; set; } = SkyRoof.NativeRotator.RotatorTransportKind.Serial;

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
    [DefaultValue(System.IO.Ports.Parity.None)]
    public System.IO.Ports.Parity NativeParity { get; set; } = System.IO.Ports.Parity.None;

    [DisplayName("Serial Stop Bits")]
    [DefaultValue(System.IO.Ports.StopBits.One)]
    public System.IO.Ports.StopBits NativeStopBits { get; set; } = System.IO.Ports.StopBits.One;

    [DisplayName("Serial Handshake")]
    [DefaultValue(System.IO.Ports.Handshake.None)]
    public System.IO.Ports.Handshake NativeHandshake { get; set; } = System.IO.Ports.Handshake.None;

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
