namespace SkyRoof
{
  /// <summary>
  /// Estimated IC-9700 receive IF passband, in displayed RF Hertz.
  /// Never interpret SkyRoof's SDR Slicer filter as a radio IF filter.
  /// CI-V 27 00 reports scope geometry, not actual FIL/BW/Twin PBT.
  /// </summary>
  internal readonly record struct IcomScopeRxPassband(
    long LowerHz, long UpperHz)
  {
    internal long WidthHz => UpperHz - LowerHz;
  }

  internal static class IcomScopeRxPassbandEstimator
  {
    // Nominal IF bandwidths follow the IC-9700 FIL2 examples. The
    // actual radio may be using FIL1/FIL3, a custom BW or Twin PBT.
    // SkyRoof's current Slicer.Mode only covers these seven modes;
    // don't fabricate an AM/RTTY/DV mode from its local SDR state.
    internal static bool TryEstimate(
      Slicer.Mode? mode,
      long dialFrequencyHz,
      IcomLanSpectrumSettings settings,
      out IcomScopeRxPassband passband)
    {
      passband = default;
      if (mode == null || dialFrequencyHz <= 0)
        return false;

      int width;
      long offset;
      switch (mode.Value)
      {
        case Slicer.Mode.USB:
          width = settings.RxSsbEstimatedBandwidthHz;
          offset = +1_500;
          break;
        case Slicer.Mode.LSB:
          width = settings.RxSsbEstimatedBandwidthHz;
          offset = -1_500;
          break;
        case Slicer.Mode.USB_D:
          width = settings.RxDataEstimatedBandwidthHz;
          offset = +1_500;
          break;
        case Slicer.Mode.LSB_D:
          width = settings.RxDataEstimatedBandwidthHz;
          offset = -1_500;
          break;
        case Slicer.Mode.CW:
          width = settings.RxCwEstimatedBandwidthHz;
          offset = 0;
          break;
        case Slicer.Mode.FM:
        case Slicer.Mode.FM_D:
          width = settings.RxFmEstimatedBandwidthHz;
          offset = 0;
          break;
        default:
          return false;
      }

      // Reject invalid persistent settings rather than painting a
      // misleading full-span region or overflowing center arithmetic.
      if (width < 50 || width > 100_000 ||
          dialFrequencyHz > long.MaxValue - 100_000)
        return false;

      long center = dialFrequencyHz + offset;
      long low = center - width / 2;
      long high = low + width;
      if (low <= 0 || high <= low)
        return false;
      passband = new IcomScopeRxPassband(low, high);
      return true;
    }
  }
}
