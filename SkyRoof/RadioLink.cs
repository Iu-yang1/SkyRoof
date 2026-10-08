using System.Diagnostics;

namespace SkyRoof
{
  public class RadioLink
  {
    public SatnogsDbSatellite? Sat;
    public SatnogsDbTransmitter? Tx;
    public SatelliteCustomization? SatCust;
    public TransmitterCustomization? TxCust;

    // non-persistent
    public bool IsTerrestrial = true;
    public bool RitEnabled;
    public double RitOffset;
    public double XitOffset;

    // persistent
    public Slicer.Mode DownlinkMode 
    { 
      get => TxCust!.DownlinkMode; 
      set => TxCust!.DownlinkMode = value; 
    }
    public Slicer.Mode UplinkMode 
    { 
      get => TxCust!.UplinkMode; 
      set => TxCust!.UplinkMode = value; 
    }
    public double TransponderOffset
    {
      get => TxCust!.TransponderOffset;
      set => TxCust!.TransponderOffset = value;
    }
    public long DownlinkBaseOffset
    {
      get => TxCust!.DownlinkBaseOffset;
      set => TxCust!.DownlinkBaseOffset = value;
    }
    public long UplinkBaseOffset
    {
      get => TxCust!.UplinkBaseOffset;
      set => TxCust!.UplinkBaseOffset = value;
    }
    public double DatabaseDownlinkBaseFrequency => Tx?.downlink_low ?? 0;
    public double DatabaseUplinkBaseFrequency =>
      Tx?.uplink_low == null ? 0 :
      Tx.invert && Tx.uplink_high.HasValue ? Tx.uplink_high.Value : Tx.uplink_low.Value;
    public double BaseDownlinkFrequency => DatabaseDownlinkBaseFrequency + DownlinkBaseOffset;
    public double BaseUplinkFrequency =>
      DatabaseUplinkBaseFrequency == 0 ? 0 : DatabaseUplinkBaseFrequency + UplinkBaseOffset;

    public double CtcssTone
    {
      get => TxCust!.CtcssTone;
      set => TxCust!.CtcssTone = value;
    }
    public bool CtcssEnabled
    {
      get => TxCust!.CtcssEnabled;
      set => TxCust!.CtcssEnabled = value;
    }
    public double DownlinkManualCorrection
    {
      get => SatCust!.DownlinkManualCorrection;
      set => SatCust!.DownlinkManualCorrection = (int)value;
    }
    public double UplinkManualCorrection
    {
      get => SatCust!.UplinkManualCorrection;
      set => SatCust!.UplinkManualCorrection = (int)value;
    }

    public bool DownlinkDopplerCorrectionEnabled 
    { 
      get => SatCust!.DownlinkDopplerCorrectionEnabled; 
      set => SatCust!.DownlinkDopplerCorrectionEnabled = value; 
    }
    public bool UplinkDopplerCorrectionEnabled
    {
      get => SatCust!.UplinkDopplerCorrectionEnabled;
      set => SatCust!.UplinkDopplerCorrectionEnabled = value;
    }
    public bool DownlinkManualCorrectionEnabled
    {
      get => SatCust!.DownlinkManualCorrectionEnabled;
      set => SatCust!.DownlinkManualCorrectionEnabled = value;
    }
    public bool UplinkManualCorrectionEnabled
    {
      get => SatCust!.UplinkManualCorrectionEnabled;
      set => SatCust!.UplinkManualCorrectionEnabled = value;
    }

    // computed
    public double DownlinkFrequency, CorrectedDownlinkFrequency;
    public double UplinkFrequency, CorrectedUplinkFrequency;

    // What the radio frequency would be if Doppler correction alone were disabled.
    // Base/transponder position and operator corrections remain included.
    public double DownlinkFrequencyWithoutDoppler =>
      !IsTerrestrial && !HasDownlink
        ? 0
        : DownlinkFrequency +
          (RitEnabled ? RitOffset : 0) +
          (!IsTerrestrial && SatCust != null && DownlinkManualCorrectionEnabled
            ? DownlinkManualCorrection : 0);

    public double UplinkFrequencyWithoutDoppler =>
      IsTerrestrial || UplinkFrequency <= 0 ? 0 :
      UplinkFrequency +
      (SatCust != null && UplinkManualCorrectionEnabled ? UplinkManualCorrection : 0) +
      XitOffset;

    public double DopplerFactor = 0;
    public bool IsAboveHorizon;
    // true when the propagator returned a valid observation this tick (independent of elevation);
    // false means DopplerFactor was forced to 0, a discontinuity the rate estimator must not ramp
    public bool HasObservation;

    // estimates the doppler rate for the Slicer's continuous correction (see DopplerRateEstimator)
    private readonly DopplerRateEstimator DopplerEstimator = new();

    // downlink offset doppler rate (Hz/s) for the Slicer's continuous correction; 0 when correction
    // is off. CorrectedDownlinkFrequency *= (1 - DopplerFactor), so the offset's doppler term is
    // -DownlinkFrequency * DopplerFactor and its time derivative is -DownlinkFrequency * factorRate.
    public double DownlinkDopplerRate =>
      DownlinkDopplerCorrectionEnabled ? -DownlinkFrequency * DopplerEstimator.FactorRate : 0;
    public bool HasDownlink =>
      IsTerrestrial ||
      Tx?.downlink_low.HasValue == true;
    public bool HasUplink =>
      !IsTerrestrial &&
      UplinkFrequency > 0 &&
      SatnogsDbTransmitter.IsHamFrequency(UplinkFrequency);
    public bool IsTransponder => Tx != null &&
      Tx.downlink_high.HasValue && Tx.downlink_high != Tx.downlink_low &&
      Tx.uplink_low.HasValue && Tx.uplink_high.HasValue;
    public bool IsCrossBand => HasDownlink && HasUplink &&
      ((SatnogsDbTransmitter.IsUhfFrequency(UplinkFrequency) != SatnogsDbTransmitter.IsUhfFrequency(DownlinkFrequency))
      ||
      (SatnogsDbTransmitter.IsVhfFrequency(UplinkFrequency) != SatnogsDbTransmitter.IsVhfFrequency(DownlinkFrequency)));


    public void ObserveSatellite(SatellitePasses engine)
    {
      var now = DateTime.UtcNow;
      var observation = engine.ObserveSatellite(Sat, now);

      if (observation == null)
      {
        DopplerFactor = 0;
        IsAboveHorizon = false;
        HasObservation = false;
      }
      else
      {
        DopplerFactor = observation.RangeRate / 3e5;
        IsAboveHorizon = observation.Elevation > 0;
        HasObservation = true;
      }

      // runs only here, the one place DopplerFactor is refreshed, so the estimate cannot be
      // desynced by the many other callers of ComputeFrequencies
      DopplerEstimator.Update(DopplerFactor, HasObservation, Sat, now);
    }

    internal void ComputeFrequencies()
    {
      if (IsTerrestrial)
      {
        CorrectedDownlinkFrequency = DownlinkFrequency;
        if (RitEnabled) CorrectedDownlinkFrequency += RitOffset;
        CorrectedUplinkFrequency = UplinkFrequency = 0;
        DopplerFactor = 0;
      }

      else
      {
        // A local record may intentionally be uplink-only. Do not synthesize a
        // 0-Hz downlink or disturb RX/SDR state for such a record.
        if (Tx?.downlink_low.HasValue == true)
        {
          double downlinkLow =
            Tx.downlink_low.Value +
            DownlinkBaseOffset;
          DownlinkFrequency = downlinkLow;
          if (IsTransponder)
            DownlinkFrequency += TransponderOffset;

          CorrectedDownlinkFrequency =
            DownlinkFrequency;
          if (RitEnabled)
            CorrectedDownlinkFrequency += RitOffset;
          if (DownlinkDopplerCorrectionEnabled)
            CorrectedDownlinkFrequency *= 1 - DopplerFactor;
          if (DownlinkManualCorrectionEnabled)
            CorrectedDownlinkFrequency += DownlinkManualCorrection;
        }
        else
        {
          DownlinkFrequency = 0;
          CorrectedDownlinkFrequency = 0;
          RitEnabled = false;
          RitOffset = 0;
        }

        // uplink nominal. Apply the same base offset to both passband edges so its width is unchanged.
        if (IsTransponder)
        {
          double uplinkLow = (double)Tx!.uplink_low! + UplinkBaseOffset;
          double uplinkHigh = (double)Tx.uplink_high! + UplinkBaseOffset;
          UplinkFrequency = Tx.invert ? uplinkHigh - TransponderOffset : uplinkLow + TransponderOffset;
        }
        else if (Tx?.uplink_low.HasValue == true)
          UplinkFrequency = (double)Tx.uplink_low + UplinkBaseOffset;
        else
          UplinkFrequency = 0;

        // uplink corrected
        CorrectedUplinkFrequency = UplinkFrequency;
        if (UplinkFrequency > 0)
        {
          if (UplinkDopplerCorrectionEnabled)
            CorrectedUplinkFrequency *= 1 + DopplerFactor;
          if (UplinkManualCorrectionEnabled)
            CorrectedUplinkFrequency += UplinkManualCorrection;
          CorrectedUplinkFrequency += XitOffset;
        }
      }
    }

    public void SetDownlinkBaseFrequency(double frequency)
    {
      if (IsTerrestrial ||
          !HasDownlink ||
          Tx == null ||
          TxCust == null)
        return;
      DownlinkBaseOffset = checked((long)Math.Round(frequency - DatabaseDownlinkBaseFrequency));
      ComputeFrequencies();
    }

    public void SetUplinkBaseFrequency(double frequency)
    {
      if (IsTerrestrial || Tx == null || TxCust == null || DatabaseUplinkBaseFrequency == 0) return;
      UplinkBaseOffset = checked((long)Math.Round(frequency - DatabaseUplinkBaseFrequency));
      ComputeFrequencies();
    }

    public void ResetDownlinkBaseFrequency()
    {
      if (TxCust == null) return;
      DownlinkBaseOffset = 0;
      ComputeFrequencies();
    }

    public void ResetUplinkBaseFrequency()
    {
      if (TxCust == null) return;
      UplinkBaseOffset = 0;
      ComputeFrequencies();
    }

    // dragging changes either the absolute frequency (terrestrial),
    // or the transponder offset (transponder),
    // or the manual correction (transmitter)
    internal double GetDraggableFrequency()
    {
      if (!HasDownlink)
        return 0;
      if (IsTerrestrial)
        return DownlinkFrequency;
      if (IsTransponder)
        return TransponderOffset;
      return DownlinkManualCorrection;
    }

    internal void SetDraggableFrequency(double freq)
    {
      if (!HasDownlink)
        return;

      if (IsTerrestrial)
        DownlinkFrequency = freq;

      else if (IsTransponder)
      {
        // Do not clamp to the SatNOGS passband. Published transponder/IF edges can be
        // approximate, and operators may need to tune beyond them.
        TransponderOffset = freq;
      }

      else
      {
        DownlinkManualCorrection = freq;
      }

      ComputeFrequencies();
    }

    internal void IncrementDownlinkFrequency(int delta)
    {
      if (!HasDownlink)
        return;

      // RIT
      if (RitEnabled)
      {
        RitOffset += delta;
      }

      // terrestrial
      else if (IsTerrestrial) 
        DownlinkFrequency += delta;

      // transponder
      else if (IsTransponder)
      {
        // The tuning position is intentionally unbounded by the database passband edges.
        TransponderOffset += delta;
      }

      // transmitter
      else
      {
        DownlinkManualCorrection += delta;
      }

      ComputeFrequencies();
    }

    /// <summary>
    /// Move the logical receive point to an absolute corrected RF frequency.
    /// This solves the same frequency model used by ComputeFrequencies instead
    /// of treating the requested corrected-frequency delta as a raw model delta.
    /// </summary>
    internal bool SetCorrectedDownlinkFrequency(
      double targetFrequency,
      bool useRit)
    {
      if (!HasDownlink ||
          !double.IsFinite(
            targetFrequency))
        return false;

      if (!IsTerrestrial)
      {
        if (SatCust == null ||
            Tx == null ||
            !Tx.downlink_low.HasValue)
          return false;

        if (IsTransponder &&
            TxCust == null)
          return false;

        if (!useRit &&
            !IsTransponder &&
            !DownlinkManualCorrectionEnabled)
          return false;
      }

      bool ritModeChanged =
        RitEnabled != useRit;
      double previousCorrected =
        CorrectedDownlinkFrequency;

      RitEnabled =
        useRit;

      if (IsTerrestrial)
      {
        if (useRit)
          RitOffset =
            targetFrequency -
            DownlinkFrequency;
        else
          DownlinkFrequency =
            targetFrequency;

        ComputeFrequencies();

        return
          ritModeChanged ||
          Math.Abs(
            CorrectedDownlinkFrequency -
            previousCorrected) >= 0.5;
      }

      double dopplerScale =
        DownlinkDopplerCorrectionEnabled
          ? 1.0 - DopplerFactor
          : 1.0;

      if (!double.IsFinite(
            dopplerScale) ||
          Math.Abs(dopplerScale) <
            1e-9)
        return false;

      double manualCorrection =
        DownlinkManualCorrectionEnabled
          ? DownlinkManualCorrection
          : 0.0;

      double desiredPreDoppler =
        (targetFrequency -
         manualCorrection) /
        dopplerScale;

      if (useRit)
      {
        double nominalDownlink =
          BaseDownlinkFrequency +
          (IsTransponder
            ? TransponderOffset
            : 0.0);

        RitOffset =
          desiredPreDoppler -
          nominalDownlink;
      }
      else if (IsTransponder)
      {
        TransponderOffset =
          desiredPreDoppler -
          BaseDownlinkFrequency;
      }
      else
      {
        double desiredManualCorrection =
          targetFrequency -
          BaseDownlinkFrequency *
          dopplerScale;

        DownlinkManualCorrection =
          Math.Round(
            desiredManualCorrection,
            MidpointRounding.AwayFromZero);
      }

      ComputeFrequencies();

      return
        ritModeChanged ||
        Math.Abs(
          CorrectedDownlinkFrequency -
          previousCorrected) >= 0.5;
    }


    /// <summary>
    /// Reset every operator tuning offset so the no-Doppler downlink/uplink return exactly
    /// to their saved Base frequencies.
    /// </summary>
    public void ReturnToBaseTuningPosition()
    {
      if (IsTerrestrial) return;

      RitEnabled = false;
      RitOffset = 0;
      XitOffset = 0;

      if (TxCust != null)
        TransponderOffset = 0;

      if (SatCust != null)
      {
        DownlinkManualCorrection = 0;
        UplinkManualCorrection = 0;
      }

      ComputeFrequencies();
    }

    public void IncrementUplinkFrequency(int delta)
    {
      if (IsTerrestrial)
        UplinkFrequency += delta;
      else
      {
        UplinkManualCorrection += delta;
      }

      ComputeFrequencies();
    }
  }
}
