using System.ComponentModel;

namespace SkyRoof
{
  public enum RotatorTrackingTarget
  {
    Satellite = 0,
    Moon = 1
  }

  public class OrbitSourceSettings
  {
    [DisplayName("Custom TLE Sources")]
    [Description("Optional TLE URLs or local file paths, separated by semicolons or new lines. Sources are re-applied after the normal SatNOGS TLE refresh. Plain 2-line/3-line TLE text and SatNOGS-style JSON are supported.")]
    public string CustomTleSources { get; set; } = string.Empty;

    [DisplayName("Moon / EME Ephemeris CSV")]
    [Description("Optional observer ephemeris CSV for Moon/EME tracking. The file should contain UTC time, azimuth in degrees and elevation in degrees. When no usable row is available, SkyRoof can use its built-in lunar position model.")]
    public string MoonEphemerisFile { get; set; } = string.Empty;

    [DisplayName("Built-in Moon Fallback")]
    [Description("Use SkyRoof's built-in topocentric lunar position model when an imported Moon ephemeris file is missing or outside its time span.")]
    [DefaultValue(true)]
    public bool UseBuiltInMoonFallback { get; set; } = true;

    [DisplayName("Rotator Tracking Target")]
    [Description("Default automatic rotator target. Satellite follows the selected satellite pass; Moon follows the imported/built-in lunar ephemeris.")]
    [DefaultValue(RotatorTrackingTarget.Satellite)]
    public RotatorTrackingTarget RotatorTarget { get; set; } = RotatorTrackingTarget.Satellite;

    public override string ToString() => string.Empty;
  }
}
