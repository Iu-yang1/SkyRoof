using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SkyRoof.SSTV
{
  public readonly record struct SstvMacroContext(
    string MyCall, string MyGrid, string TheirCall,
    string Satellite, string Mode, double TxHz, double RxHz,
    DateTime Utc)
  {
    public string RstSent { get; init; } = "";
    public string RstReceived { get; init; } = "";
    public string ContactName { get; init; } = "";
    public string ContactQth { get; init; } = "";
    public string Notes { get; init; } = "";
  }

  /// <summary>
  /// Safe, operator-editable SSTV overlay macros; unknown tokens remain
  /// literal text and no script or filesystem expressions are evaluated.
  /// </summary>
  public static class SstvTextMacros
  {
    private static readonly Regex Token = new(
      @"\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant,
      TimeSpan.FromMilliseconds(100));

    // The operator can insert these in any of up to eight independently
    // positioned text layers. Aliases stay accepted for old templates.
    public static readonly (string Token, string Label)[] Catalog =
    {
      ("{MYCALL}", "己方呼号"), ("{HISCALL}", "对方呼号"),
      ("{MYGRID}", "己方网格"), ("{SAT}", "卫星名称"),
      ("{MODE}", "SSTV 模式"), ("{RSV_TX}", "己方信号报告"),
      ("{RSV_RX}", "对方信号报告"), ("{NAME}", "对方姓名"),
      ("{QTH}", "对方 QTH"), ("{NOTE}", "通联备注"),
      ("{UTC}", "UTC 时间"), ("{DATE}", "UTC 日期"),
      ("{HHMMSS}", "UTC 时分秒"),
      ("{TXMHZ}", "上行频率 MHz"), ("{RXMHZ}", "下行频率 MHz"),
      ("{TXHZ}", "上行频率 Hz"), ("{RXHZ}", "下行频率 Hz")
    };

    public static string Expand(string overlay, SstvMacroContext context)
    {
      if (overlay == null) throw new ArgumentNullException(nameof(overlay));
      if (context.Utc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("UTC time required");
      if (overlay.Length > 512)
        throw new ArgumentException("SSTV picture text cannot exceed 512 characters.");
      return Token.Replace(overlay, match =>
      {
        string key = match.Groups["name"].Value.ToUpperInvariant();
        return key switch
        {
          "MYCALL" or "CALL" => context.MyCall,
          "MYGRID" or "GRID" => context.MyGrid,
          "HISCALL" or "DXCALL" => context.TheirCall,
          "SAT" or "SATELLITE" => context.Satellite,
          "MODE" => context.Mode,
          "RSV_TX" or "RST_TX" or "RST_SENT" or "MYSIG" => context.RstSent,
          "RSV_RX" or "RST_RX" or "RST_RCVD" or "HISSIG" => context.RstReceived,
          "NAME" or "HISNAME" => context.ContactName,
          "QTH" or "HISQTH" => context.ContactQth,
          "NOTE" or "COMMENT" => context.Notes,
          "UTC" => context.Utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC",
          "DATE" => context.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          "HHMM" or "TIME" => context.Utc.ToString("HH:mm", CultureInfo.InvariantCulture),
          "HHMMSS" => context.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
          "TXMHZ" => context.TxHz > 0 ? (context.TxHz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture) : "",
          "RXMHZ" => context.RxHz > 0 ? (context.RxHz / 1e6).ToString("0.000000", CultureInfo.InvariantCulture) : "",
          "TXHZ" => context.TxHz > 0 ? context.TxHz.ToString("0", CultureInfo.InvariantCulture) : "",
          "RXHZ" => context.RxHz > 0 ? context.RxHz.ToString("0", CultureInfo.InvariantCulture) : "",
          _ => match.Value
        };
      });
    }
  }
}
