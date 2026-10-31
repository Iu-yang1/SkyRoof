using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SkyRoof.SSTV
{
  public readonly record struct SstvMacroContext(
    string MyCall, string MyGrid, string TheirCall,
    string Satellite, string Mode, double TxHz, double RxHz,
    DateTime Utc);

  /// <summary>
  /// Safe, operator-editable SSTV overlay macros; unknown tokens remain
  /// literal text and no script or filesystem expressions are evaluated.
  /// </summary>
  public static class SstvTextMacros
  {
    private static readonly Regex Token = new(
      @"\{(?<name>[A-Za-z_]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant,
      TimeSpan.FromMilliseconds(100));

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
          "UTC" => context.Utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC",
          "DATE" => context.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          "TXHZ" => context.TxHz > 0 ? context.TxHz.ToString("0", CultureInfo.InvariantCulture) : "",
          "RXHZ" => context.RxHz > 0 ? context.RxHz.ToString("0", CultureInfo.InvariantCulture) : "",
          _ => match.Value
        };
      });
    }
  }
}
