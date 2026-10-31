using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// An SSTV picture is not automatically a two-way QSO. Records are created
  /// only when the operator explicitly saves the other station's callsign.
  /// Each row preserves UTC, the satellite context and attached picture.
  /// </summary>
  public sealed record SstvQsoRecord(
    DateTime Utc, string Call, string MyCall, string SstvMode,
    string SatelliteName, double TxHz, double RxHz, string PictureFile,
    string Notes);

  public static class SstvQsoLogStore
  {
    private static readonly object Gate = new();

    public static string DefaultLogPath(string userFolder) =>
      Path.Combine(userFolder, "SstvImages", "sstv-qsos.jsonl");

    public static void Append(string path, SstvQsoRecord record)
    {
      ArgumentNullException.ThrowIfNull(record);
      if (record.Utc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("QSO timestamp must be UTC.");
      if (!Regex.IsMatch(record.Call, @"^[A-Z0-9/]{3,16}$"))
        throw new ArgumentException("请确认对方的有效呼号。");
      if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("Invalid log path.", nameof(path));
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
      string line = JsonSerializer.Serialize(record) + "\n";
      lock (Gate)
        File.AppendAllText(path, line, new UTF8Encoding(false));
    }

    public static IReadOnlyList<SstvQsoRecord> Read(string path, int maxRows = 1000)
    {
      if (!File.Exists(path)) return Array.Empty<SstvQsoRecord>();
      var rows = new List<SstvQsoRecord>();
      lock (Gate)
      {
        foreach (string line in File.ReadLines(path))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;
          try
          {
            SstvQsoRecord? record = JsonSerializer.Deserialize<SstvQsoRecord>(line);
            if (record != null) rows.Add(record);
          }
          catch (JsonException) { }
          if (rows.Count > 100000)
            throw new InvalidDataException("SSTV log exceeds safe size limit.");
        }
      }
      return rows.OrderByDescending(x => x.Utc).Take(Math.Clamp(maxRows, 1, 100000))
        .ToArray();
    }

    public static string ExportAdif(IEnumerable<SstvQsoRecord> records)
    {
      var adif = new StringBuilder("SkyRoof SSTV QSO Log\n<ADIF_VER:5>3.1.6\n<PROGRAMID:7>SkyRoof\n<EOH>\n");
      foreach (SstvQsoRecord q in records.OrderBy(x => x.Utc))
      {
        Field("QSO_DATE", q.Utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Field("TIME_ON", q.Utc.ToString("HHmmss", CultureInfo.InvariantCulture));
        Field("CALL", q.Call);
        Field("STATION_CALLSIGN", q.MyCall);
        Field("MODE", "SSTV");
        Field("APP_SKYROOF_SSTV_MODE", q.SstvMode);
        if (q.TxHz > 0) Field("FREQ", (q.TxHz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture));
        if (q.RxHz > 0) Field("FREQ_RX", (q.RxHz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(q.SatelliteName))
        {
          Field("PROP_MODE", "SAT");
          Field("SAT_NAME", q.SatelliteName);
        }
        if (!string.IsNullOrWhiteSpace(q.Notes)) Field("COMMENT", q.Notes);
        if (!string.IsNullOrWhiteSpace(q.PictureFile))
          Field("APP_SKYROOF_SSTV_PICTURE", Path.GetFileName(q.PictureFile));
        adif.Append("<EOR>\n");
      }
      return adif.ToString();

      void Field(string key, string value) =>
        adif.Append('<').Append(key).Append(':').Append(value.Length)
          .Append('>').Append(value).Append('\n');
    }
  }
}
