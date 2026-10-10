namespace SkyRoof.CW
{
  public interface ICwTransmitApplicationGuard
  {
    string CaptureArmToken();
    void ValidateSend(string armToken);
  }

  public readonly record struct CwApplicationTxState(
    bool IsTerrestrial,
    string? SatelliteId,
    string? TransmitterId,
    bool HasUplink,
    double CorrectedUplinkHz,
    double UplinkReferenceWithoutDopplerHz,
    string UplinkMode,
    bool TxCatTransverterOutOfBand);

  /// <summary>
  /// Binds one CW Arm operation to the current SkyRoof satellite/transmitter
  /// identity and to the non-Doppler part of the uplink model. Real-time
  /// satellite Doppler is deliberately excluded so normal ClockTick CAT
  /// corrections remain possible while Command 17 is sending.
  /// </summary>
  public sealed class CwSatelliteTransmitGuard :
    ICwTransmitApplicationGuard
  {
    private readonly Func<CwApplicationTxState>
      readState;

    public CwSatelliteTransmitGuard(
      Context ctx)
      : this(
          () => ReadContext(ctx))
    {
    }

    public CwSatelliteTransmitGuard(
      Func<CwApplicationTxState> readState)
    {
      this.readState =
        readState ??
        throw new ArgumentNullException(
          nameof(readState));
    }

    public string CaptureArmToken()
    {
      CwApplicationTxState state =
        readState();

      ValidateState(state);
      return Fingerprint(state);
    }

    public void ValidateSend(
      string armToken)
    {
      if (string.IsNullOrWhiteSpace(
            armToken))
        throw new InvalidOperationException(
          "CW TX has no valid Arm context.");

      CwApplicationTxState state =
        readState();

      ValidateState(state);

      string current =
        Fingerprint(state);

      if (!string.Equals(
            current,
            armToken,
            StringComparison.Ordinal))
        throw new InvalidOperationException(
          "Satellite/transmitter/uplink context changed after CW TX was armed. Disarm and arm again before sending.");
    }

    private static CwApplicationTxState
      ReadContext(Context ctx)
    {
      ArgumentNullException.ThrowIfNull(ctx);

      RadioLink link =
        ctx.FrequencyControl.RadioLink;

      if (link.IsTerrestrial)
      {
        return new(
          IsTerrestrial: true,
          SatelliteId: null,
          TransmitterId: null,
          HasUplink: true,
          CorrectedUplinkHz: 0,
          UplinkReferenceWithoutDopplerHz: 0,
          UplinkMode: "RADIO",
          TxCatTransverterOutOfBand: false);
      }

      return new(
        IsTerrestrial: false,
        SatelliteId:
          link.Sat?.sat_id,
        TransmitterId:
          link.Tx?.uuid,
        HasUplink:
          link.HasUplink,
        CorrectedUplinkHz:
          link.CorrectedUplinkFrequency,
        UplinkReferenceWithoutDopplerHz:
          link.UplinkFrequencyWithoutDoppler,
        UplinkMode:
          link.UplinkMode.ToString(),
        TxCatTransverterOutOfBand:
          ctx.FrequencyControl
            .IsTxCatTransverterOutOfBand);
    }

    private static void ValidateState(
      CwApplicationTxState state)
    {
      if (state.IsTerrestrial)
        return;

      if (string.IsNullOrWhiteSpace(
            state.SatelliteId))
        throw new InvalidOperationException(
          "No satellite is bound to the CW transmitter.");

      if (string.IsNullOrWhiteSpace(
            state.TransmitterId))
        throw new InvalidOperationException(
          "The selected satellite has no active transmitter.");

      if (!state.HasUplink ||
          !double.IsFinite(
            state.CorrectedUplinkHz) ||
          state.CorrectedUplinkHz <= 0)
        throw new InvalidOperationException(
          "The selected transmitter has no valid uplink.");

      if (state.TxCatTransverterOutOfBand)
        throw new InvalidOperationException(
          "The uplink RF is outside every configured TX CAT transverter band.");

      if (!IsCwMode(
            state.UplinkMode))
        throw new InvalidOperationException(
          "The selected satellite uplink mode must be CW or CW-R before arming the CW keyer.");
    }

    private static bool IsCwMode(
      string mode)
    {
      string normalized =
        mode
          .Trim()
          .ToUpperInvariant()
          .Replace('_', '-')
          .Replace(" ", "");

      return normalized is
        "CW" or
        "CW-R" or
        "CWR";
    }

    private static string Fingerprint(
      CwApplicationTxState state)
    {
      if (state.IsTerrestrial)
        return "TERRESTRIAL";

      long referenceHz =
        checked((long)Math.Round(
          state.UplinkReferenceWithoutDopplerHz,
          MidpointRounding.AwayFromZero));

      string mode =
        state.UplinkMode
          .Trim()
          .ToUpperInvariant()
          .Replace('_', '-')
          .Replace(" ", "");

      return string.Join(
        "|",
        "SAT=" + state.SatelliteId,
        "TX=" + state.TransmitterId,
        "MODE=" + mode,
        "REFHZ=" + referenceHz);
    }
  }
}
