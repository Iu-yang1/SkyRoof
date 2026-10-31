using System;
using System.Collections.Generic;
using System.Linq;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// User-selectable transmit profile. Native Robot/PD mode handling stays
  /// delegated to SkySignals. RGB profiles use timing constants audited
  /// against MMSSTV Main.cpp (LineSCT, LineMRT, LineSC2180).
  /// </summary>
  public sealed record SstvTxModeProfile(
    string Name, int Width, int Height, SstvMode? Native,
    SstvRgbTxSpec? Rgb)
  {
    public static readonly IReadOnlyList<SstvTxModeProfile> All =
      SstvModes.All
        .Select(x => new SstvTxModeProfile(x.Name, x.Width, x.Height, x.Mode, null))
        .Concat(SstvRgbTxSpec.All.Select(x =>
          new SstvTxModeProfile(x.Name, x.Width, x.Height, null, x)))
        .ToArray();
    public override string ToString() => Name;
  }

  public enum SstvRgbTxLayout { Scottie, Martin, Sc2 }

  public sealed record SstvRgbTxSpec(
    string Name, SstvRgbTxLayout Layout,
    int VisCode7, double ScanMs,
    double SyncMs, double PorchMs,
    int Width = 320, int Height = 256)
  {
    public double LineMs => Layout switch
    {
      SstvRgbTxLayout.Scottie => 3 * ScanMs + 13.5,
      SstvRgbTxLayout.Martin => 3 * ScanMs + SyncMs + 4 * PorchMs,
      SstvRgbTxLayout.Sc2 => 3 * ScanMs + SyncMs + PorchMs,
      _ => throw new InvalidOperationException()
    };

    public static readonly IReadOnlyList<SstvRgbTxSpec> All = new[]
    {
      new SstvRgbTxSpec("Scottie 1", SstvRgbTxLayout.Scottie, 0x3C,
        138.240, 9.000, 1.500),
      new SstvRgbTxSpec("Scottie 2", SstvRgbTxLayout.Scottie, 0x38,
        88.064, 9.000, 1.500),
      new SstvRgbTxSpec("Scottie DX", SstvRgbTxLayout.Scottie, 0x4C,
        345.600, 9.000, 1.500),
      new SstvRgbTxSpec("Martin 1", SstvRgbTxLayout.Martin, 0x2C,
        146.432, 4.862, 0.572),
      new SstvRgbTxSpec("Martin 2", SstvRgbTxLayout.Martin, 0x28,
        73.216, 4.862, 0.572),
      new SstvRgbTxSpec("SC2 60", SstvRgbTxLayout.Sc2, 0x3B,
        78.128, 5.5006, 0.500),
      new SstvRgbTxSpec("SC2 120", SstvRgbTxLayout.Sc2, 0x3F,
        156.500, 5.52248, 0.500),
      new SstvRgbTxSpec("SC2 180", SstvRgbTxLayout.Sc2, 0x37,
        235.000, 5.5437, 0.500)
    };
  }
}
