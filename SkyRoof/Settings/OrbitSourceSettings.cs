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
    public const string DefaultSatellitesUrl =
      "https://db.satnogs.org/api/satellites/?format=json";
    public const string DefaultTransmittersUrl =
      "https://db.satnogs.org/api/transmitters/?format=json";
    public const string DefaultTleUrl =
      "https://db.satnogs.org/api/tle/?format=json";
    public const string DefaultCelestrakOmmCsvUrl =
      "https://celestrak.org/NORAD/elements/gp.php?GROUP=amateur&FORMAT=csv";
    public const string DefaultAutoTleUrl =
      "http://autotle.bi4pym.cn/AutoTLE.txt";

    public const string DefaultDe440sSources =
      "https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/planets/de440s.bsp;" +
      "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de440s.bsp";

    public const string DefaultDe421Sources =
      "https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/planets/a_old_versions/de421.bsp;" +
      "https://ssd.jpl.nasa.gov/ftp/eph/planets/bsp/de421.bsp";

    [Category("Satellite database")]
    [DisplayName("Satellite List URL")]
    [Description("Primary satellite-list endpoint. The default is SatNOGS. You may replace it with a compatible SatNOGS JSON endpoint. Leave blank to keep the last cached file instead of updating it.")]
    [DefaultValue(DefaultSatellitesUrl)]
    public string SatellitesUrl { get; set; } = DefaultSatellitesUrl;

    [Category("Satellite database")]
    [DisplayName("Transmitter List URL")]
    [Description("Primary transmitter-list endpoint. The default is SatNOGS. You may replace it with a compatible SatNOGS JSON endpoint. Leave blank to keep the last cached file instead of updating it.")]
    [DefaultValue(DefaultTransmittersUrl)]
    public string TransmittersUrl { get; set; } = DefaultTransmittersUrl;

    [Category("Orbit elements")]
    [DisplayName("CelesTrak OMM CSV URL")]
    [Description("Highest-priority automatic orbit source. The default is CelesTrak's amateur GP/OMM CSV feed. OMM is propagated directly; it is not converted back to legacy TLE.")]
    [DefaultValue(DefaultCelestrakOmmCsvUrl)]
    public string CelestrakOmmCsvUrl { get; set; } = DefaultCelestrakOmmCsvUrl;

    [Category("Orbit elements")]
    [DisplayName("Manual Orbit Source URLs")]
    [Description("User-configured URL or local-file orbit sources, separated by semicolons or new lines. These are below CelesTrak OMM CSV but above the built-in AutoTLE and SatNOGS sources. Earlier entries have higher priority. TLE text, SatNOGS JSON, and CelesTrak OMM JSON/CSV are supported.")]
    public string CustomTleSources { get; set; } = string.Empty;

    [Category("Orbit elements")]
    [DisplayName("AutoTLE URL")]
    [Description("Built-in AutoTLE fallback source. It is below Manual Orbit Source URLs and CelesTrak OMM CSV, but above the original SatNOGS orbit source. Leave blank to disable it.")]
    [DefaultValue(DefaultAutoTleUrl)]
    public string AutoTleUrl { get; set; } = DefaultAutoTleUrl;

    [Category("Orbit elements")]
    [DisplayName("SatNOGS Orbit URL")]
    [Description("Lowest-priority automatic orbit source. The default is the original SatNOGS TLE endpoint. Leave blank to disable updates from this source.")]
    [DefaultValue(DefaultTleUrl)]
    public string TleUrl { get; set; } = DefaultTleUrl;

    [Category("JPL ephemeris")]
    [DisplayName("DE440s Download Sources")]
    [Description("Ordered DE440s download URLs, separated by semicolons or new lines. SkyRoof tries each source in order. The defaults are the official NASA/JPL NAIF and SSD hosts; edit or remove either URL if necessary.")]
    [DefaultValue(DefaultDe440sSources)]
    public string De440sSources { get; set; } = DefaultDe440sSources;

    [Category("JPL ephemeris")]
    [DisplayName("DE421 Download Sources")]
    [Description("Ordered DE421 download URLs, separated by semicolons or new lines. SkyRoof tries each source in order. The defaults are the official NASA/JPL NAIF and SSD hosts; edit or remove either URL if necessary.")]
    [DefaultValue(DefaultDe421Sources)]
    public string De421Sources { get; set; } = DefaultDe421Sources;

    [Category("JPL ephemeris")]
    [DisplayName("JPL Ephemeris Kernel")]
    [Description("Planetary/lunar SPK kernel used for Moon, Sun and Venus tracking. DE440s is recommended for modern dates; DE421 is retained for compatibility.")]
    [DefaultValue(JplEphemerisKernel.DE440s)]
    public JplEphemerisKernel JplKernel { get; set; } = JplEphemerisKernel.DE440s;

    [Category("JPL ephemeris")]
    [DisplayName("Custom JPL BSP File")]
    [Description("Path to a local JPL/NAIF DAF-SPK .bsp file when JPL Ephemeris Kernel is CustomFile.")]
    public string JplKernelFile { get; set; } = string.Empty;

    [Category("JPL ephemeris")]
    [DisplayName("Show Solar-System Targets")]
    [Description("Add Moon, Sun and Venus to the normal satellite/target database when a compatible JPL kernel is available.")]
    [DefaultValue(true)]
    public bool ShowSolarSystemTargets { get; set; } = true;

    [Category("JPL ephemeris")]
    [DisplayName("Auto-download JPL Kernel")]
    [Description("When DE440s or DE421 is selected and not cached yet, download it once from the configured source list. Custom BSP files are never downloaded automatically.")]
    [DefaultValue(true)]
    public bool AutoDownloadJplKernel { get; set; } = true;

    public override string ToString() => string.Empty;
  }
}
