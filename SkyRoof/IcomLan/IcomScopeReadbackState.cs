using System.Globalization;

namespace SkyRoof
{
  internal sealed class IcomScopeReadbackState
  {
    // A field may be absent because the radio firmware rejected its query.
    // Never apply fallback values to SkyRoof settings as real radio state.
    internal IReadOnlySet<string>? FieldsPresent { get; private set; }
    internal bool IsPartial => FieldsPresent != null && FieldsPresent.Count < 16;
    internal bool HasField(string key) =>
      FieldsPresent == null || FieldsPresent.Contains(key);

    internal byte SelectedScope { get; init; }

    internal IcomScopeMode MainMode { get; init; }
    internal long MainSpanHz { get; init; }
    internal int MainEdge { get; init; }
    internal double MainReferenceDb { get; init; }
    internal IcomScopeSweepSpeed MainSpeed { get; init; }
    internal IcomScopeVbw MainVbw { get; init; }

    internal IcomScopeMode SubMode { get; init; }
    internal long SubSpanHz { get; init; }
    internal int SubEdge { get; init; }
    internal double SubReferenceDb { get; init; }
    internal IcomScopeSweepSpeed SubSpeed { get; init; }
    internal IcomScopeVbw SubVbw { get; init; }

    internal bool ScopeDuringTx { get; init; }
    internal IcomScopeCenterType CenterType { get; init; }
    internal IcomScopeMarkerPosition MarkerPosition { get; init; }

    internal static IcomScopeReadbackState Parse(
      string text)
    {
      if (string.IsNullOrWhiteSpace(
            text))
        throw new FormatException(
          "Empty scope readback.");

      Dictionary<string, string> values =
        text
          .Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
          .Select(
            item =>
            {
              int equals =
                item.IndexOf('=');

              if (equals <= 0 ||
                  equals ==
                    item.Length - 1)
                throw new FormatException(
                  $"Malformed scope readback field '{item}'.");

              return new KeyValuePair<string, string>(
                item[..equals],
                item[(equals + 1)..]);
            })
          .ToDictionary(
            pair =>
              pair.Key,
            pair =>
              pair.Value,
            StringComparer.OrdinalIgnoreCase);

      return new IcomScopeReadbackState
      {
        SelectedScope =
          ParseScope(
            Get(values, "SELECT")),

        MainMode =
          ParseMode(
            Get(values, "MAIN.MODE")),
        MainSpanHz =
          ParseLong(
            values,
            "MAIN.SPAN"),
        MainEdge =
          ParseEdge(
            values,
            "MAIN.EDGE"),
        MainReferenceDb =
          ParseReference(
            values,
            "MAIN.REF"),
        MainSpeed =
          ParseSpeed(
            Get(values, "MAIN.SPEED")),
        MainVbw =
          ParseVbw(
            Get(values, "MAIN.VBW")),

        SubMode =
          ParseMode(
            Get(values, "SUB.MODE")),
        SubSpanHz =
          ParseLong(
            values,
            "SUB.SPAN"),
        SubEdge =
          ParseEdge(
            values,
            "SUB.EDGE"),
        SubReferenceDb =
          ParseReference(
            values,
            "SUB.REF"),
        SubSpeed =
          ParseSpeed(
            Get(values, "SUB.SPEED")),
        SubVbw =
          ParseVbw(
            Get(values, "SUB.VBW")),

        ScopeDuringTx =
          ParseBoolean01(
            Get(values, "TX"),
            "TX"),
        CenterType =
          ParseCenterType(
            Get(values, "CENTER")),
        MarkerPosition =
          ParseMarkerPosition(
            Get(values, "MARKER"))
      };
    }

    internal static IcomScopeReadbackState ParsePartial(string text)
    {
      // Keep Parse() strict for legacy full readback and existing tests.
      // Neutral fillers below are ONLY for parsing a partial object. Callers
      // must use HasField() before applying a property to radio/UI settings.
      var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["SELECT"] = "MAIN",
        ["MAIN.MODE"] = "CENTER", ["MAIN.SPAN"] = "25000",
        ["MAIN.EDGE"] = "1", ["MAIN.REF"] = "0.0",
        ["MAIN.SPEED"] = "FAST", ["MAIN.VBW"] = "WIDE",
        ["SUB.MODE"] = "CENTER", ["SUB.SPAN"] = "25000",
        ["SUB.EDGE"] = "1", ["SUB.REF"] = "0.0",
        ["SUB.SPEED"] = "FAST", ["SUB.VBW"] = "WIDE",
        ["TX"] = "0", ["CENTER"] = "FILTER", ["MARKER"] = "FILTER"
      };

      var received = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (string field in text.Split(';',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      {
        int separator = field.IndexOf('=');
        if (separator <= 0 || separator == field.Length - 1)
          throw new FormatException($"Invalid partial scope field '{field}'.");
        string key = field[..separator];
        if (!defaults.ContainsKey(key) || !received.Add(key))
          throw new FormatException($"Unknown/duplicate partial scope field '{key}'.");
        defaults[key] = field[(separator + 1)..];
      }

      if (!received.Contains("SELECT"))
        throw new FormatException("Partial scope response requires SELECT.");
      var parsed = Parse(string.Join(";",
        defaults.Select(kv => $"{kv.Key}={kv.Value}")));
      parsed.FieldsPresent = received;
      return parsed;
    }

    private static string Get(
      Dictionary<string, string> values,
      string key)
    {
      if (!values.TryGetValue(
            key,
            out string? value))
        throw new FormatException(
          $"Missing scope readback field '{key}'.");

      return value;
    }

    private static byte ParseScope(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "MAIN" => 0,
        "SUB" => 1,
        _ =>
          throw new FormatException(
            $"Invalid selected scope '{value}'.")
      };

    private static IcomScopeMode ParseMode(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "CENTER" =>
          IcomScopeMode.Center,
        "FIXED" =>
          IcomScopeMode.Fixed,
        "SCROLL-C" =>
          IcomScopeMode.ScrollCenter,
        "SCROLL-F" =>
          IcomScopeMode.ScrollFixed,
        _ =>
          throw new FormatException(
            $"Invalid scope mode '{value}'.")
      };

    private static long ParseLong(
      Dictionary<string, string> values,
      string key)
    {
      string value =
        Get(
          values,
          key);

      if (!long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long parsed) ||
          parsed <= 0)
        throw new FormatException(
          $"Invalid scope readback value '{key}={value}'.");

      return parsed;
    }

    private static int ParseEdge(
      Dictionary<string, string> values,
      string key)
    {
      long edge =
        ParseLong(
          values,
          key);

      if (edge is < 1 or > 4)
        throw new FormatException(
          $"Invalid scope edge '{edge}'.");

      return (int)edge;
    }

    private static double ParseReference(
      Dictionary<string, string> values,
      string key)
    {
      string value =
        Get(
          values,
          key);

      if (!double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) ||
          !double.IsFinite(
            parsed) ||
          parsed < -20.0 ||
          parsed > 20.0)
        throw new FormatException(
          $"Invalid scope reference '{value}'.");

      return parsed;
    }

    private static IcomScopeSweepSpeed ParseSpeed(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "FAST" =>
          IcomScopeSweepSpeed.Fast,
        "MID" =>
          IcomScopeSweepSpeed.Mid,
        "SLOW" =>
          IcomScopeSweepSpeed.Slow,
        _ =>
          throw new FormatException(
            $"Invalid scope speed '{value}'.")
      };

    private static IcomScopeVbw ParseVbw(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "NARROW" =>
          IcomScopeVbw.Narrow,
        "WIDE" =>
          IcomScopeVbw.Wide,
        _ =>
          throw new FormatException(
            $"Invalid scope VBW '{value}'.")
      };

    private static bool ParseBoolean01(
      string value,
      string name) =>
      value switch
      {
        "0" => false,
        "1" => true,
        _ =>
          throw new FormatException(
            $"Invalid {name} flag '{value}'.")
      };

    private static IcomScopeCenterType ParseCenterType(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "FILTER" =>
          IcomScopeCenterType.FilterCenter,
        "CARRIER" =>
          IcomScopeCenterType.CarrierPoint,
        "ABS" =>
          IcomScopeCenterType.CarrierPointAbsolute,
        _ =>
          throw new FormatException(
            $"Invalid CENTER type '{value}'.")
      };

    private static IcomScopeMarkerPosition ParseMarkerPosition(
      string value) =>
      value.ToUpperInvariant() switch
      {
        "FILTER" =>
          IcomScopeMarkerPosition.FilterCenter,
        "CARRIER" =>
          IcomScopeMarkerPosition.CarrierPoint,
        _ =>
          throw new FormatException(
            $"Invalid marker position '{value}'.")
      };
  }

  internal sealed class IcomFixedEdgeReadbackState
  {
    internal int FrequencyRange { get; init; }
    internal int EdgeNumber { get; init; }
    internal long LowerHz { get; init; }
    internal long UpperHz { get; init; }

    internal static IcomFixedEdgeReadbackState Parse(
      string text)
    {
      if (string.IsNullOrWhiteSpace(
            text))
        throw new FormatException(
          "Empty fixed-edge readback.");

      Dictionary<string, string> values =
        text
          .Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
          .Select(
            item =>
            {
              int equals =
                item.IndexOf('=');

              if (equals <= 0 ||
                  equals ==
                    item.Length - 1)
                throw new FormatException(
                  $"Malformed fixed-edge field '{item}'.");

              return new KeyValuePair<string, string>(
                item[..equals],
                item[(equals + 1)..]);
            })
          .ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

      int range =
        ParseInt(
          values,
          "RANGE");
      int edge =
        ParseInt(
          values,
          "EDGE");
      long lower =
        ParseLong(
          values,
          "LOWER");
      long upper =
        ParseLong(
          values,
          "UPPER");

      if (range is < 1 or > 3)
        throw new FormatException(
          $"Invalid fixed-edge range '{range}'.");

      if (edge is < 1 or > 4)
        throw new FormatException(
          $"Invalid fixed-edge number '{edge}'.");

      if (upper <= lower)
        throw new FormatException(
          "Fixed-edge upper frequency must be greater than lower frequency.");

      return new IcomFixedEdgeReadbackState
      {
        FrequencyRange = range,
        EdgeNumber = edge,
        LowerHz = lower,
        UpperHz = upper
      };
    }

    private static int ParseInt(
      Dictionary<string, string> values,
      string key)
    {
      long value =
        ParseLong(
          values,
          key);

      if (value >
          int.MaxValue)
        throw new FormatException(
          $"Fixed-edge field '{key}' is too large.");

      return (int)value;
    }

    private static long ParseLong(
      Dictionary<string, string> values,
      string key)
    {
      if (!values.TryGetValue(
            key,
            out string? text) ||
          !long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value) ||
          value < 0)
        throw new FormatException(
          $"Invalid fixed-edge field '{key}'.");

      return value;
    }
  }

}
