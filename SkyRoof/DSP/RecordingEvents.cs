using System.Text.Json;
using VE3NEA.Clock;

namespace SkyRoof
{
  public class RecordingEvents
  {
    public class RecordingEvent
    {
      public DateTime Utc { get; set; }
      public string EventType { get; set; } = string.Empty;
      public string Satellite { get; set; } = string.Empty;
      public int? NoradId { get; set; }
      public string Transmitter { get; set; } = string.Empty;
      public string TransmitterId { get; set; } = string.Empty;
      public string Mode { get; set; } = string.Empty;
      public string Callsign { get; set; } = string.Empty;

      public string GetTooltipText()
      {
        string time = ClockWidget.Stamp(Utc, "HH:mm:ss");
        List<string> lines = EventType switch
        {
          "satellite" => [$"{time} satellite selected"],
          "transmitter" => [$"{time} transmitter selected"],
          "mode" => [$"{time} mode selected"],
          "qso" => [$"{time} QSO saved"],
          _ => [$"{time} {EventType}"],
        };

        if (EventType == "satellite")
        {
          AddTooltipLine(lines, "SAT", Satellite);
          AddTooltipLine(lines, "TX", Transmitter);
          AddTooltipLine(lines, "MODE", Mode);
        }
        else if (EventType == "transmitter")
        {
          AddTooltipLine(lines, "TX", Transmitter);
          AddTooltipLine(lines, "MODE", Mode);
        }
        else if (EventType == "mode") AddTooltipLine(lines, "MODE", Mode);
        else if (EventType == "qso") AddTooltipLine(lines, "CALL", Callsign);

        return string.Join("\n", lines);
      }

      private static void AddTooltipLine(List<string> lines, string label, params string[] values)
      {
        string value = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        if (value != string.Empty) lines.Add($"{label}: {value}");
      }
    }

    public List<RecordingEvent> Events { get; set; } = [];
    // Exact recording timeline origin; previously marker positions used the
    // first satellite event and shifted when selection was initially empty.
    public DateTime StartUtc { get; set; }

    private string? lastSatelliteValue;
    private string? lastTransmitterValue;
    private string? lastModeValue;

    public void Start(Context ctx)
    {
      Events.Clear();

      lastSatelliteValue = GetSelectedSatelliteValue(ctx);
      lastTransmitterValue = GetSelectedTransmitterValue(ctx);
      lastModeValue = GetDownlinkModeValue(ctx);

      var utc = DateTime.UtcNow;
      StartUtc = utc;
      if (!string.IsNullOrWhiteSpace(lastSatelliteValue))
        Add(utc, "satellite", lastSatelliteValue, GetSelectedNoradId(ctx), lastTransmitterValue ?? string.Empty, GetSelectedTransmitterId(ctx), lastModeValue ?? string.Empty, string.Empty);
    }

    public void Add(DateTime utc, string eventType)
    {
      Add(utc, eventType, string.Empty, null, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    public void Add(DateTime utc, string eventType, string satellite, int? noradId, string transmitter, string transmitterId, string mode, string callsign)
    {
      Events.Add(new RecordingEvent
      {
        Utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
        EventType = eventType,
        Satellite = satellite,
        NoradId = noradId,
        Transmitter = transmitter,
        TransmitterId = transmitterId,
        Mode = mode,
        Callsign = callsign,
      });
    }

    public bool RememberChanges(Context ctx)
    {
      DateTime utc = DateTime.UtcNow;
      string? satelliteValue = GetSelectedSatelliteValue(ctx);
      string? transmitterValue = GetSelectedTransmitterValue(ctx);
      string? modeValue = GetDownlinkModeValue(ctx);
      bool satelliteChanged = !string.IsNullOrWhiteSpace(satelliteValue) && satelliteValue != lastSatelliteValue;
      bool transmitterChanged = !string.IsNullOrWhiteSpace(transmitterValue) && transmitterValue != lastTransmitterValue;
      bool modeChanged = !string.IsNullOrWhiteSpace(modeValue) && modeValue != lastModeValue;

      if (satelliteChanged)
      {
        lastSatelliteValue = satelliteValue;
        lastTransmitterValue = transmitterValue;
        lastModeValue = modeValue;
        Add(utc, "satellite", satelliteValue!, GetSelectedNoradId(ctx), transmitterValue ?? string.Empty, GetSelectedTransmitterId(ctx), modeValue ?? string.Empty, string.Empty);
        return true;
      }

      if (transmitterChanged)
      {
        lastTransmitterValue = transmitterValue;
        if (modeChanged) lastModeValue = modeValue;
        Add(utc, "transmitter", string.Empty, null, transmitterValue!, GetSelectedTransmitterId(ctx), modeValue ?? string.Empty, string.Empty);
        return true;
      }

      if (modeChanged)
      {
        lastModeValue = modeValue;
        Add(utc, "mode", string.Empty, null, string.Empty, string.Empty, modeValue!, string.Empty);
        return true;
      }

      return false;
    }

    public bool RememberQsoSaved(string callsign)
    {
      if (string.IsNullOrWhiteSpace(callsign)) return false;

      string call = callsign.Trim().ToUpperInvariant();
      Add(DateTime.UtcNow, "qso", string.Empty, null, string.Empty, string.Empty, string.Empty, call);
      return true;
    }

    public double GetRelativeSeconds(RecordingEvent recordingEvent)
    {
      if (Events.Count == 0) return 0;
      return Math.Max(0, (recordingEvent.Utc - Events[0].Utc).TotalSeconds);
    }

    public void Save(string fileName)
    {
      string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
      File.WriteAllText(fileName, json);
    }

    public string? GetSingleSatelliteName()
    {
      string[] satellites = Events
        .Select(e => e.Satellite)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

      return satellites.Length == 1 ? satellites[0] : null;
    }

    public static RecordingEvents Load(string fileName)
    {
      try
      {
        if (!File.Exists(fileName)) return new();

        string json = File.ReadAllText(fileName);
        RecordingEvents? recordingEvents = JsonSerializer.Deserialize<RecordingEvents>(json);
        if (recordingEvents == null) return new();

        if (recordingEvents.StartUtc == default && recordingEvents.Events.Count > 0)
          recordingEvents.StartUtc = recordingEvents.Events.Min(e => e.Utc);
        if (recordingEvents.StartUtc != default)
          recordingEvents.StartUtc = DateTime.SpecifyKind(recordingEvents.StartUtc, DateTimeKind.Utc);
        foreach (var recordingEvent in recordingEvents.Events)
          if (recordingEvent.Utc.Kind == DateTimeKind.Unspecified)
            recordingEvent.Utc = DateTime.SpecifyKind(recordingEvent.Utc, DateTimeKind.Utc);

        if (recordingEvents.Events.Count > 0)
        {
          recordingEvents.lastSatelliteValue = recordingEvents.Events.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Satellite))?.Satellite;
          recordingEvents.lastTransmitterValue = recordingEvents.Events.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Transmitter))?.Transmitter;
          recordingEvents.lastModeValue = recordingEvents.Events.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Mode))?.Mode;
        }

        return recordingEvents;
      }
      catch
      {
        return new();
      }
    }

    private static string? GetSelectedSatelliteValue(Context ctx)
    {
      return ctx.SatelliteSelector.SelectedSatellite?.name;
    }

    private static string? GetSelectedTransmitterValue(Context ctx)
    {
      return ctx.SatelliteSelector.SelectedTransmitter?.description;
    }

    private static int? GetSelectedNoradId(Context ctx)
    {
      return ctx.SatelliteSelector.SelectedSatellite?.norad_cat_id;
    }

    private static string GetSelectedTransmitterId(Context ctx)
    {
      return ctx.SatelliteSelector.SelectedTransmitter?.uuid ?? string.Empty;
    }

    private static string? GetDownlinkModeValue(Context ctx)
    {
      if (ctx.FrequencyControl.RadioLink.TxCust == null) return null;
      return ctx.FrequencyControl.RadioLink.DownlinkMode.ToString();
    }
  }
}
