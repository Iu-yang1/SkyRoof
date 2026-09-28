using System.ComponentModel;

namespace SkyRoof
{
  public enum JplEphemerisKernel
  {
    DE440s = 0,
    DE421 = 1,
    CustomFile = 2
  }

  public class OrbitSourceSettings
  {
    [DisplayName("Custom TLE Sources")]
    [Description("Optional TLE URLs or local file paths, separated by semicolons or new lines. Sources are re-applied after the normal SatNOGS TLE refresh. Plain 2-line/3-line TLE text and SatNOGS-style JSON are supported.")]
    public string CustomTleSources { get; set; } = string.Empty;

    [DisplayName("JPL Ephemeris Kernel")]
    [Description("Planetary/lunar SPK kernel used for Moon, Sun and Venus tracking. DE440s is recommended for modern dates; DE421 is retained for compatibility.")]
    [DefaultValue(JplEphemerisKernel.DE440s)]
    public JplEphemerisKernel JplKernel { get; set; } = JplEphemerisKernel.DE440s;

    [DisplayName("Custom JPL BSP File")]
    [Description("Path to a local JPL/NAIF DAF-SPK .bsp file when JPL Ephemeris Kernel is CustomFile.")]
    public string JplKernelFile { get; set; } = string.Empty;

    [DisplayName("Show Solar-System Targets")]
    [Description("Add Moon, Sun and Venus to the normal satellite/target database when a compatible JPL kernel is available.")]
    [DefaultValue(true)]
    public bool ShowSolarSystemTargets { get; set; } = true;

    public override string ToString() => string.Empty;
  }
}
