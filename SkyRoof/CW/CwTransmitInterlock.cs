namespace SkyRoof.CW
{
  public readonly record struct CwTransmitInterlockSnapshot(
    bool IsSatellite,
    string? SatelliteId,
    string? TransmitterId,
    double UplinkWithoutDopplerHz,
    double CorrectedUplinkHz,
    long ExpectedCatTxHz,
    double CatLoOffsetHz,
    int FrequencyToleranceHz,
    Slicer.Mode UplinkMode)
  {
    public bool RequiresHardwareFrequencyGuard =>
      IsSatellite;
  }

  public interface ICwTransmitInterlock
  {
    bool TxWritesFrozen { get; }

    CwTransmitInterlockSnapshot CaptureForArm();

    CwTransmitInterlockSnapshot PrepareForSend(
      CwTransmitInterlockSnapshot armed);

    void ValidateDuringSend(
      CwTransmitInterlockSnapshot active);

    void ValidateHardware(
      CwTransmitInterlockSnapshot active,
      CwKeyerStatus status);

    void SetTxWritesFrozen(bool frozen);
  }

  internal sealed class NullCwTransmitInterlock :
    ICwTransmitInterlock
  {
    internal static readonly NullCwTransmitInterlock
      Instance = new();

    public bool TxWritesFrozen => false;

    public CwTransmitInterlockSnapshot
      CaptureForArm() =>
      new(
        IsSatellite: false,
        SatelliteId: null,
        TransmitterId: null,
        UplinkWithoutDopplerHz: 0,
        CorrectedUplinkHz: 0,
        ExpectedCatTxHz: 0,
        CatLoOffsetHz: 0,
        FrequencyToleranceHz: 0,
        UplinkMode: Slicer.Mode.CW);

    public CwTransmitInterlockSnapshot
      PrepareForSend(
        CwTransmitInterlockSnapshot armed) =>
      armed;

    public void ValidateDuringSend(
      CwTransmitInterlockSnapshot active)
    {
    }

    public void ValidateHardware(
      CwTransmitInterlockSnapshot active,
      CwKeyerStatus status)
    {
    }

    public void SetTxWritesFrozen(bool frozen)
    {
    }
  }

  /// <summary>
  /// Satellite-specific local safety guard for the Command-17 keyer.
  ///
  /// The raw 48 kHz receive/tracker path is unrelated to this guard. It only
  /// validates the currently selected satellite uplink and temporarily blocks
  /// SkyRoof's own TX CAT frequency/mode/CTCSS writes while an IC-9700 CW text
  /// message is active. Doppler computation continues in RadioLink, so the
  /// next normal CAT tick catches up immediately after STOP.
  /// </summary>
  public sealed class CwSatelliteTransmitInterlock :
    ICwTransmitInterlock
  {
    private const int HardwareFrequencyToleranceHz = 100;
    private const double ContextFrequencyToleranceHz = 0.5;
    private const double PassbandToleranceHz = 100;
    private const double SingleFrequencyToleranceHz = 5000;

    private readonly Context ctx;
    private int txWritesFrozen;

    public CwSatelliteTransmitInterlock(
      Context ctx)
    {
      this.ctx =
        ctx ??
        throw new ArgumentNullException(
          nameof(ctx));
    }

    public bool TxWritesFrozen =>
      Volatile.Read(
        ref txWritesFrozen) != 0;

    public CwTransmitInterlockSnapshot
      CaptureForArm() =>
      CaptureValidated();

    public CwTransmitInterlockSnapshot
      PrepareForSend(
        CwTransmitInterlockSnapshot armed)
    {
      CwTransmitInterlockSnapshot current =
        CaptureValidated();

      EnsureSameOperatorContext(
        armed,
        current);

      return current;
    }

    public void ValidateDuringSend(
      CwTransmitInterlockSnapshot active)
    {
      CwTransmitInterlockSnapshot current =
        CaptureValidated();

      EnsureSameOperatorContext(
        active,
        current);
    }

    public void ValidateHardware(
      CwTransmitInterlockSnapshot active,
      CwKeyerStatus status)
    {
      if (!active.IsSatellite)
        return;

      if (!status.ActualTxFrequencyHz.HasValue)
        throw new InvalidOperationException(
          "SkyCAT STATUS does not report TXHZ. " +
          "Satellite CW transmit requires SkyCAT with the SENDHZ frequency guard.");

      long delta =
        Math.Abs(
          status.ActualTxFrequencyHz.Value -
          active.ExpectedCatTxHz);

      if (delta >
          active.FrequencyToleranceHz)
        throw new InvalidOperationException(
          $"Actual radio TX VFO is {status.ActualTxFrequencyHz.Value:n0} Hz, " +
          $"expected {active.ExpectedCatTxHz:n0} Hz " +
          $"(±{active.FrequencyToleranceHz} Hz).");
    }

    public void SetTxWritesFrozen(
      bool frozen) =>
      Interlocked.Exchange(
        ref txWritesFrozen,
        frozen ? 1 : 0);

    private CwTransmitInterlockSnapshot
      CaptureValidated()
    {
      RadioLink link =
        ctx.FrequencyControl.RadioLink;

      // Preserve the generic terrestrial text-keyer behavior introduced by
      // PR #51. Satellite-specific TXHZ/SENDHZ gating only applies when the
      // active RadioLink is a satellite link.
      if (link.IsTerrestrial)
      {
        return new(
          IsSatellite: false,
          SatelliteId: null,
          TransmitterId: null,
          UplinkWithoutDopplerHz: 0,
          CorrectedUplinkHz: 0,
          ExpectedCatTxHz: 0,
          CatLoOffsetHz: 0,
          FrequencyToleranceHz: 0,
          UplinkMode: Slicer.Mode.CW);
      }

      SatnogsDbSatellite? sat =
        link.Sat;
      SatnogsDbTransmitter? tx =
        link.Tx;

      if (sat == null ||
          tx == null)
        throw new InvalidOperationException(
          "No satellite transmitter is selected.");

      if (!link.HasUplink ||
          link.CorrectedUplinkFrequency <= 0)
        throw new InvalidOperationException(
          "The selected satellite transmitter has no valid amateur uplink.");

      if (link.UplinkMode !=
          Slicer.Mode.CW)
        throw new InvalidOperationException(
          "The selected satellite uplink mode must be CW before arming the CW keyer.");

      if (!link.HasObservation ||
          !link.IsAboveHorizon)
        throw new InvalidOperationException(
          "The selected satellite does not currently have a valid above-horizon observation.");

      if (!SatnogsDbTransmitter.IsHamFrequency(
            link.CorrectedUplinkFrequency))
        throw new InvalidOperationException(
          $"The corrected uplink {link.CorrectedUplinkFrequency:n0} Hz is outside SkyRoof's supported amateur 2 m / 70 cm ranges.");

      if (ctx.CatControl.Tx == null)
        throw new InvalidOperationException(
          "Satellite CW transmit requires an active TX CAT backend.");

      if (!TryMapTxCatFrequency(
            link.CorrectedUplinkFrequency,
            out long catTxHz,
            out double loOffsetHz))
        throw new InvalidOperationException(
          ctx.FrequencyControl
            .GetTxCatTransverterOutOfBandMessage());

      double noDoppler =
        link.UplinkFrequencyWithoutDoppler;

      ValidatePublishedUplinkPassband(
        link,
        tx,
        noDoppler);

      return new(
        IsSatellite: true,
        SatelliteId: sat.sat_id,
        TransmitterId: tx.uuid,
        UplinkWithoutDopplerHz: noDoppler,
        CorrectedUplinkHz:
          link.CorrectedUplinkFrequency,
        ExpectedCatTxHz: catTxHz,
        CatLoOffsetHz: loOffsetHz,
        FrequencyToleranceHz:
          HardwareFrequencyToleranceHz,
        UplinkMode: link.UplinkMode);
    }

    private bool TryMapTxCatFrequency(
      double txRf,
      out long catTxHz,
      out double loOffsetHz)
    {
      loOffsetHz = 0;

      TransverterSettings transverter =
        ctx.Settings.Transverter;

      if (transverter.TxCatOffsetEnabled)
      {
        TransverterBand? band =
          transverter.GetCatBand(txRf);

        if (band == null)
        {
          catTxHz = 0;
          return false;
        }

        loOffsetHz =
          band.LoOffset;
      }

      catTxHz =
        checked((long)Math.Truncate(
          txRf - loOffsetHz));

      return catTxHz > 0;
    }

    private static void
      ValidatePublishedUplinkPassband(
        RadioLink link,
        SatnogsDbTransmitter tx,
        double noDopplerHz)
    {
      if (!tx.uplink_low.HasValue)
        return;

      if (!tx.uplink_high.HasValue ||
          tx.uplink_high.Value ==
            tx.uplink_low.Value)
      {
        double center =
          tx.uplink_low.Value +
          link.UplinkBaseOffset;

        if (Math.Abs(
              noDopplerHz -
              center) >
            SingleFrequencyToleranceHz)
          throw new InvalidOperationException(
            $"The logical uplink {noDopplerHz:n0} Hz is more than " +
            $"±{SingleFrequencyToleranceHz:n0} Hz from the selected single-frequency uplink " +
            $"{center:n0} Hz.");

        return;
      }

      double a =
        tx.uplink_low.Value +
        link.UplinkBaseOffset;
      double b =
        tx.uplink_high.Value +
        link.UplinkBaseOffset;

      double low =
        Math.Min(a, b) -
        PassbandToleranceHz;
      double high =
        Math.Max(a, b) +
        PassbandToleranceHz;

      if (noDopplerHz < low ||
          noDopplerHz > high)
        throw new InvalidOperationException(
          $"The logical uplink {noDopplerHz:n0} Hz is outside the selected transmitter passband " +
          $"{Math.Min(a, b):n0}–{Math.Max(a, b):n0} Hz.");
    }

    internal static void
      EnsureSameOperatorContext(
        CwTransmitInterlockSnapshot baseline,
        CwTransmitInterlockSnapshot current)
    {
      if (baseline.IsSatellite !=
          current.IsSatellite)
        throw new InvalidOperationException(
          "CW TX context changed between terrestrial and satellite operation.");

      if (!baseline.IsSatellite)
        return;

      if (!string.Equals(
            baseline.SatelliteId,
            current.SatelliteId,
            StringComparison.Ordinal) ||
          !string.Equals(
            baseline.TransmitterId,
            current.TransmitterId,
            StringComparison.Ordinal))
        throw new InvalidOperationException(
          "The selected satellite or transmitter changed after CW TX was armed.");

      if (baseline.UplinkMode !=
          current.UplinkMode)
        throw new InvalidOperationException(
          "The satellite uplink mode changed after CW TX was armed.");

      if (Math.Abs(
            baseline.UplinkWithoutDopplerHz -
            current.UplinkWithoutDopplerHz) >
          ContextFrequencyToleranceHz)
        throw new InvalidOperationException(
          "The operator uplink tuning position changed after CW TX was armed.");

      if (Math.Abs(
            baseline.CatLoOffsetHz -
            current.CatLoOffsetHz) >
          ContextFrequencyToleranceHz)
        throw new InvalidOperationException(
          "The TX transverter CAT mapping changed after CW TX was armed.");
    }
  }
}
