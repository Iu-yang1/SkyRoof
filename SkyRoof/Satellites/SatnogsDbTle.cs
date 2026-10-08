using SGPdotNET.Parsers;

namespace SkyRoof
{
  public class SatnogsDbTle
  {
    public string tle0 { get; set; } = string.Empty;
    public string tle1 { get; set; } = string.Empty;
    public string tle2 { get; set; } = string.Empty;
    public string tle_source { get; set; } = string.Empty;
    public string sat_id { get; set; } = string.Empty;
    public int? norad_cat_id { get; set; }
    public DateTime updated { get; set; }

    // OMM is kept alongside the legacy TLE fields so Satellites.json can persist
    // CelesTrak CSV/JSON mean elements without lossy conversion back to 69-column TLE.
    // SGP.NET 1.6+ accepts OmmData directly and therefore also supports 6-digit
    // NORAD catalogue numbers that cannot be represented by the old TLE layout.
    public OmmData? omm { get; set; }

    public bool IsOmm => omm != null;
  }


  public class SatnogsDbTleList : List<SatnogsDbTle> { }
}
