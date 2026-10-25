using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;

namespace SkyRoof
{
  /// <summary>
  /// PropertyGrid selector for the models actually shipped with SkyCAT.
  /// Persist the original model ID, not the friendly display text.
  /// Never construct SkyCAT's serial/command engine on the UI thread.
  /// </summary>
  public sealed class EmbeddedSkyCatModelConverter : StringConverter
  {
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Models =
      new(() => LoadModels(Path.Combine(AppContext.BaseDirectory, "Rigs")));

    internal static IReadOnlyDictionary<string, string> LoadModels(string rigsDirectory)
    {
      var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      if (!Directory.Exists(rigsDirectory)) return result;

      try
      {
        foreach (string file in Directory.EnumerateFiles(rigsDirectory, "*.json")
          .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
        {
          try
          {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("id", out var idProperty) ||
                !idProperty.TryGetInt32(out int id) || id <= 0)
              continue;

            // Use the same model name SkyCAT derives from the JSON filename.
            result.TryAdd(id.ToString(CultureInfo.InvariantCulture),
              Path.GetFileNameWithoutExtension(file));
          }
          catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or JsonException or InvalidOperationException)
          {
            // One unavailable/malformed rig definition must not crash Settings.
            // The selected custom/legacy value remains editable.
          }
        }
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException)
      {
        // The Rigs directory may disappear while devices/files are changing.
      }

      return result;
    }

    internal static string DisplayModel(string model,
      IReadOnlyDictionary<string, string> models)
    {
      if (string.IsNullOrWhiteSpace(model)) return model ?? string.Empty;
      if (models.TryGetValue(model, out var name)) return $"{name} ({model})";

      var existing = models.FirstOrDefault(pair =>
        string.Equals(pair.Value, model, StringComparison.OrdinalIgnoreCase));
      return string.IsNullOrEmpty(existing.Key)
        ? model : $"{existing.Value} ({existing.Key})";
    }

    internal static string ParseModel(string text,
      IReadOnlyDictionary<string, string> models)
    {
      string value = text.Trim();
      int separator = value.LastIndexOf(" (", StringComparison.Ordinal);
      if (separator < 1 || !value.EndsWith(')')) return value;

      string candidateId = value.Substring(separator + 2, value.Length - separator - 3);
      string candidateName = value.Substring(0, separator);
      if (models.TryGetValue(candidateId, out var name) &&
          string.Equals(name, candidateName, StringComparison.OrdinalIgnoreCase))
        return candidateId;

      // Unknown/custom model names remain untouched for SkyCAT compatibility.
      return value;
    }

    public static string GetDisplayName(string model) => DisplayModel(model, Models.Value);

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
      var values = Models.Value.OrderBy(pair => pair.Value,
        StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key).ToList();
      string? selected = (context?.Instance as EmbeddedSkyCatSettings)?.Model;
      if (!string.IsNullOrWhiteSpace(selected) &&
          !values.Contains(selected, StringComparer.OrdinalIgnoreCase) &&
          !Models.Value.Values.Contains(selected, StringComparer.OrdinalIgnoreCase))
        values.Add(selected);
      return new StandardValuesCollection(values);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context,
      CultureInfo? culture, object value)
    {
      return value is string text ? ParseModel(text, Models.Value)
        : base.ConvertFrom(context, culture, value);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context,
      CultureInfo? culture, object? value, Type destinationType)
    {
      if (destinationType == typeof(string) && value is string model)
        return DisplayModel(model, Models.Value);
      return base.ConvertTo(context, culture, value, destinationType);
    }
  }

  /// <summary>
  /// A live, non-invasive Windows COM port dropdown. Enumeration is repeated
  /// when opened, but never opens a port or changes the existing serial owner.
  /// </summary>
  public sealed class EmbeddedSkyCatSerialPortConverter : StringConverter
  {
    internal static string[] SortPorts(IEnumerable<string> detected,
      string? selected = null)
    {
      var ports = detected.Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

      // Preserve a saved COM device even if it is temporarily unplugged.
      if (!string.IsNullOrWhiteSpace(selected) &&
          !ports.Contains(selected.Trim(), StringComparer.OrdinalIgnoreCase))
        ports.Add(selected.Trim());

      return ports.OrderBy(name => PortNumber(name))
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int PortNumber(string name) =>
      name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
      int.TryParse(name.AsSpan(3), NumberStyles.None,
        CultureInfo.InvariantCulture, out int number)
        ? number : int.MaxValue;

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
      string[] found;
      try
      {
        found = SerialPort.GetPortNames();
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or InvalidOperationException
        or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
      {
        found = Array.Empty<string>();
      }

      string? selected = (context?.Instance as EmbeddedSkyCatSettings)?.SerialPort;
      return new StandardValuesCollection(SortPorts(found, selected));
    }
  }
}
