using System;
using System.Collections.Generic;
namespace SkyRoof.SSTV
{
  public enum SstvExtraLayout
  {
    P, Mp, Mr, NarrowMp, NarrowMc, RobotBw, Robot24, Avt
  }

  /// <summary>
  /// Transmit-only protocol timing catalog transcribed from MMSSTV Main.cpp:
  /// LineP, LineMP, LineMR, LineMN, LineMC, LineRM, LineR24, LineAVT;
  /// and mode.txt (extended VIS and narrow N-VIS).
  /// </summary>
  public sealed record SstvExtraTxSpec(
    string Name, SstvExtraLayout Layout,
    int VisCode, double ScanMs, double SyncMs, double PorchMs,
    int Width, int Height)
  {
    public int RowsPerScan =>
      Layout is SstvExtraLayout.Mp or SstvExtraLayout.NarrowMp or
        SstvExtraLayout.RobotBw or SstvExtraLayout.Robot24 ? 2 : 1;
    public int LineCount => Height / RowsPerScan;
    public bool Narrow => Layout is SstvExtraLayout.NarrowMp or SstvExtraLayout.NarrowMc;
    public bool Extended => !Narrow && VisCode > 0xFF;
    public double HeaderMs => Narrow ? 950.0 : Extended ? 1150.0 :
      Layout == SstvExtraLayout.Avt ? 3 * 910.0 + 32 * 17 * 9.7646 + .30514375
       : 910.0;
    public double LineMs => Layout switch
    {
      SstvExtraLayout.P => SyncMs + 4 * PorchMs + 3 * ScanMs,
      SstvExtraLayout.Mp or SstvExtraLayout.NarrowMp =>
        SyncMs + PorchMs + 4 * ScanMs,
      SstvExtraLayout.Mr => SyncMs + PorchMs + 2 * ScanMs + .3,
      SstvExtraLayout.NarrowMc => SyncMs + PorchMs + 3 * ScanMs,
      SstvExtraLayout.RobotBw => SyncMs + PorchMs + ScanMs,
      SstvExtraLayout.Robot24 =>
        SyncMs + PorchMs + ScanMs + 3 + 1 + ScanMs / 2 + 3 + 1 + ScanMs / 2,
      SstvExtraLayout.Avt => 3 * ScanMs,
      _ => throw new InvalidOperationException()
    };

    public static readonly IReadOnlyList<SstvExtraTxSpec> All = new[]
    {
      new SstvExtraTxSpec("Robot BW 8", SstvExtraLayout.RobotBw, 0x02, 58.89709, 6, 2, 320, 240),
      new SstvExtraTxSpec("Robot BW 12", SstvExtraLayout.RobotBw, 0x06, 92, 6, 2, 320, 240),
      new SstvExtraTxSpec("Robot 24", SstvExtraLayout.Robot24, 0x04, 92, 6, 2, 320, 240),
      new SstvExtraTxSpec("AVT 90", SstvExtraLayout.Avt, 0x44, 125, 0, 0, 320, 240),
      new SstvExtraTxSpec("P3", SstvExtraLayout.P, 0x71, 133.333, 5.208, 1.042, 640, 496),
      new SstvExtraTxSpec("P5", SstvExtraLayout.P, 0x72, 200.000, 7.813, 1.562375, 640, 496),
      new SstvExtraTxSpec("P7", SstvExtraLayout.P, 0x73, 266.667, 10.417, 2.083, 640, 496),

      new SstvExtraTxSpec("MP73", SstvExtraLayout.Mp, 0x2523, 140, 9, 1, 320, 256),
      new SstvExtraTxSpec("MP115", SstvExtraLayout.Mp, 0x2923, 223, 9, 1, 320, 256),
      new SstvExtraTxSpec("MP140", SstvExtraLayout.Mp, 0x2A23, 270, 9, 1, 320, 256),
      new SstvExtraTxSpec("MP175", SstvExtraLayout.Mp, 0x2C23, 340, 9, 1, 320, 256),

      new SstvExtraTxSpec("MR73", SstvExtraLayout.Mr, 0x4523, 138, 9, 1, 320, 256),
      new SstvExtraTxSpec("MR90", SstvExtraLayout.Mr, 0x4623, 171, 9, 1, 320, 256),
      new SstvExtraTxSpec("MR115", SstvExtraLayout.Mr, 0x4923, 220, 9, 1, 320, 256),
      new SstvExtraTxSpec("MR140", SstvExtraLayout.Mr, 0x4A23, 269, 9, 1, 320, 256),
      new SstvExtraTxSpec("MR175", SstvExtraLayout.Mr, 0x4C23, 337, 9, 1, 320, 256),

      new SstvExtraTxSpec("ML180", SstvExtraLayout.Mr, 0x8523, 176.5, 9, 1, 640, 496),
      new SstvExtraTxSpec("ML240", SstvExtraLayout.Mr, 0x8623, 236.5, 9, 1, 640, 496),
      new SstvExtraTxSpec("ML280", SstvExtraLayout.Mr, 0x8923, 277.5, 9, 1, 640, 496),
      new SstvExtraTxSpec("ML320", SstvExtraLayout.Mr, 0x8A23, 317.5, 9, 1, 640, 496),

      new SstvExtraTxSpec("MP73-N", SstvExtraLayout.NarrowMp, 0x02, 140, 9, 1, 320, 256),
      new SstvExtraTxSpec("MP110-N", SstvExtraLayout.NarrowMp, 0x04, 212, 9, 1, 320, 256),
      new SstvExtraTxSpec("MP140-N", SstvExtraLayout.NarrowMp, 0x05, 270, 9, 1, 320, 256),
      new SstvExtraTxSpec("MC110-N", SstvExtraLayout.NarrowMc, 0x14, 143, 8, .5, 320, 256),
      new SstvExtraTxSpec("MC140-N", SstvExtraLayout.NarrowMc, 0x15, 180, 8, .5, 320, 256),
      new SstvExtraTxSpec("MC180-N", SstvExtraLayout.NarrowMc, 0x16, 232, 8, .5, 320, 256),
    };
  }
}
