using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Adjustable receiver acquisition strategy. Computation can refine the
  /// frequency estimate, but cannot replace the independent samples required
  /// to establish signal SNR and prevent false CAT writes.
  /// </summary>
  public enum AfcAcquisitionPreset
  {
    Adaptive,
    Fast,
    Balanced,
    Sensitive
  }

  public readonly record struct AfcTrackingProfile(
    int MinimumSamples, int WindowSamples, int CadenceMilliseconds,
    double CoastSeconds, double MaximumRateHzPerSecond)
  {
    public static AfcTrackingProfile For(AfcSignalMode mode, bool ssbPilot,
      AfcAcquisitionPreset preset = AfcAcquisitionPreset.Balanced)
    {
      // Suppressed-carrier speech has no unique absolute frequency reference.
      if (mode == AfcSignalMode.SSB && !ssbPilot)
        return new(36000, 48000, 250, 0, 0);

      return (mode, preset) switch
      {
        // Earliest initial observation ~250 ms, 100 ms overlapped cadence.
        // Adaptive retains as much as 750-900 ms of IF once available.
        (AfcSignalMode.FM, AfcAcquisitionPreset.Adaptive) =>
          new(12000, 36000, 100, 1.5, 90),
        (AfcSignalMode.CW, AfcAcquisitionPreset.Adaptive) =>
          new(12000, 38400, 100, 2.5, 35),
        (AfcSignalMode.SSB, AfcAcquisitionPreset.Adaptive) =>
          new(12000, 43200, 125, 2.0, 30),

        (AfcSignalMode.FM, AfcAcquisitionPreset.Fast) =>
          new(12000, 19200, 100, 1.5, 90),
        (AfcSignalMode.CW, AfcAcquisitionPreset.Fast) =>
          new(12000, 19200, 100, 2.5, 35),
        (AfcSignalMode.SSB, AfcAcquisitionPreset.Fast) =>
          new(14400, 24000, 125, 2.0, 30),

        (AfcSignalMode.FM, AfcAcquisitionPreset.Sensitive) =>
          new(24000, 48000, 200, 1.5, 90),
        (AfcSignalMode.CW, AfcAcquisitionPreset.Sensitive) =>
          new(18000, 48000, 150, 2.5, 35),
        (AfcSignalMode.SSB, AfcAcquisitionPreset.Sensitive) =>
          new(18000, 48000, 150, 2.0, 30),

        (AfcSignalMode.FM, _) =>
          new(19200, 24000, 125, 1.5, 90),
        (AfcSignalMode.CW, _) =>
          new(18000, 26400, 125, 2.5, 35),
        _ => new(18000, 28800, 150, 2.0, 30)
      };
    }
  }
}
