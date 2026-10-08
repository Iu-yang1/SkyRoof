namespace SkyRoof
{
  // User-authored transmitter metadata stored separately from downloaded
  // SatNOGS/JE9PEL data. The UUID is stable so per-transmitter settings such as
  // modes, CTCSS and base-frequency corrections continue to follow the record.
  public sealed class CustomTransmitterDefinition
  {
    public string uuid { get; set; } =
      $"local-{Guid.NewGuid():N}";

    public string? sat_id { get; set; }
    public int? norad_cat_id { get; set; }

    public string description { get; set; } = "";
    public long? downlink_hz { get; set; }
    public long? uplink_hz { get; set; }
    public string mode { get; set; } = "FM";
    public DateTime updated_utc { get; set; } =
      DateTime.UtcNow;

    internal bool HasAnyFrequency =>
      downlink_hz.HasValue ||
      uplink_hz.HasValue;
  }

  public sealed class CustomTransmitterDefinitionList :
    List<CustomTransmitterDefinition>
  {
  }
}
