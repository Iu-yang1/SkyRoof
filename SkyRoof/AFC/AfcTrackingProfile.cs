using System;

namespace SkyRoof.AFC
{
  /// <summary>Mode-specific IF observation windows and drop-out tolerances.
  /// Reference-free SSB never becomes absolute AFC, regardless of quality.</summary>
  public readonly record struct AfcTrackingProfile(
    int MinimumSamples, int WindowSamples, int CadenceMilliseconds,
    double CoastSeconds, double MaximumRateHzPerSecond)
  {
    public static AfcTrackingProfile For(AfcSignalMode mode, bool ssbPilot) =>
      mode switch
      {
        AfcSignalMode.FM => new(19200, 24000, 125, 1.5, 90),
        AfcSignalMode.CW => new(18000, 26400, 125, 2.5, 35),
        AfcSignalMode.SSB when ssbPilot =>
          new(18000, 28800, 150, 2.0, 30),
        _ => new(36000, 48000, 250, 0.0, 0.0)
      };
  }
}
