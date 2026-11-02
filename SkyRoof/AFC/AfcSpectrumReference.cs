using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// User spectrum choices do not directly command CAT.
  /// FM uses a nullable, mode-local acquisition candidate; CW/SSB retain
  /// the independent tone reference.
  /// </summary>
  public static class AfcSpectrumReference
  {
    public static double AnalysisOffset(AfcSettings cfg, AfcSignalMode mode)
    {
      if (mode != AfcSignalMode.FM) return cfg.TargetOffsetHz;
      return cfg.FmReferenceCandidateHz is double hz &&
        double.IsFinite(hz) && Math.Abs(hz) <= 6000 ? hz : double.NaN;
    }

    public static double DisplayMarker(AfcSettings cfg, AfcSignalMode mode)
    {
      double hz = AnalysisOffset(cfg, mode);
      return double.IsFinite(hz) ? hz : 0;
    }

    public static bool Select(AfcSettings cfg, AfcSignalMode mode, double hz)
    {
      if (!double.IsFinite(hz) || Math.Abs(hz) > 6000) return false;
      if (mode == AfcSignalMode.FM) cfg.FmReferenceCandidateHz = hz;
      else cfg.TargetOffsetHz = hz;
      return true;
    }

    public static void Reset(AfcSettings cfg, AfcSignalMode mode)
    {
      if (mode == AfcSignalMode.FM) cfg.FmReferenceCandidateHz = null;
      else cfg.TargetOffsetHz = 0;
    }
  }
}
