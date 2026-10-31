using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SkyRoof.SSTV
{
  /// <summary>Persistent, reusable operator picture layout.</summary>
  public sealed record SstvUserTemplate(
    string Name, string Style, string OverlayText, DateTime SavedUtc)
  {
    // Optional properties preserve all previously saved template JSON files.
    public string? MyCallText { get; init; }
    public string? TheirCallText { get; init; }
    public SstvTextLayer? MyCallStyle { get; init; }
    public SstvTextLayer? TheirCallStyle { get; init; }
    public SstvTextLayer? OverlayStyle { get; init; }
    public override string ToString() => Name;
  }

  public static class SstvTemplateStore
  {
    private static readonly string[] Styles =
      { "经典呼号", "QSL 卡片", "卫星横幅", "纯图片" };
    private const int MaxTemplates = 100;

    public static string DefaultPath(string folder) =>
      Path.Combine(folder, "SstvImages", "templates.json");

    public static IReadOnlyList<SstvUserTemplate> Read(string path)
    {
      if (!File.Exists(path)) return Array.Empty<SstvUserTemplate>();
      var data = new FileInfo(path);
      if (data.Length > 256 * 1024)
        throw new InvalidDataException("SSTV template library is too large");
      try
      {
        SstvUserTemplate[]? templates = JsonSerializer.Deserialize<SstvUserTemplate[]>(
          File.ReadAllText(path, Encoding.UTF8));
        return (templates ?? Array.Empty<SstvUserTemplate>())
          .Where(Valid).Take(MaxTemplates).ToArray();
      }
      catch (JsonException) { return Array.Empty<SstvUserTemplate>(); }
    }

    public static void Save(string path, SstvUserTemplate template)
    {
      if (!Valid(template))
        throw new ArgumentException("模板名称、内容或样式不符合要求。");
      var list = Read(path).Where(x =>
        !string.Equals(x.Name, template.Name, StringComparison.OrdinalIgnoreCase)).ToList();
      list.Add(template);
      if (list.Count > MaxTemplates)
        throw new InvalidOperationException("已达到 100 个图片模板上限。");
      string full = Path.GetFullPath(path);
      WriteAtomically(full, list);
    }

    /// <summary>Remove only one named user template; built-in presets are not stored here.</summary>
    public static bool Delete(string path, string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      var existing = Read(path);
      var remaining = existing.Where(x =>
        !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
      if (remaining.Length == existing.Count) return false;
      WriteAtomically(Path.GetFullPath(path), remaining);
      return true;
    }

    private static void WriteAtomically(string full, IReadOnlyList<SstvUserTemplate> entries)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
      try
      {
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries,
          new JsonSerializerOptions { WriteIndented = true }),
          new UTF8Encoding(false));
        File.Move(temporary, full, true);
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Valid(SstvUserTemplate template) =>
      template != null &&
      !string.IsNullOrWhiteSpace(template.Name) &&
      template.Name.Length <= 48 &&
      template.Style != null &&
      template.OverlayText != null &&
      template.OverlayText.Length <= 512 &&
      (template.MyCallText == null || template.MyCallText.Length <= 60) &&
      (template.TheirCallText == null || template.TheirCallText.Length <= 60) &&
      Styles.Contains(template.Style) &&
      template.SavedUtc.Kind == DateTimeKind.Utc;
  }
}
