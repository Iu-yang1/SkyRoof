using System;
using VE3NEA;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Immutable snapshot of the operator-selected satellite uplink at TX arm.
  /// This is not a substitute for CAT readback or actual RF power measurement.
  /// </summary>
  public readonly record struct SstvTxGuard(
    string SatelliteId, string TransmitterId, long ExpectedCatHz,
    double ExpectedRfHz, Slicer.Mode Mode, DateTime StartedUtc);

  public static class SstvTxPolicy
  {
    public const long FrequencyToleranceHz = 250;
    public const double MaximumAudioDurationSeconds = 540;
    public const int MaximumPttReadbackAgeMs = 1500;
    public const string PttReleaseUnconfirmedMessage =
      "PTT 状态未确认！请立即检查电台是否仍在发射；收到新的 CAT RX 读回前禁止再次发射。";

    public static SstvTxGuard Validate(
      string? satelliteId, string? transmitterId,
      bool isTerrestrial, bool hasUplink, bool hasObservation,
      bool isAboveHorizon, Slicer.Mode mode, double rfHz, double loHz,
      long actualTxCatHz, bool canPtt, bool alreadyTransmitting,
      bool conflictingTxLease, bool operatorApproved, DateTime nowUtc)
    {
      // TX is explicitly initiated by the Send Image button. Satellite
      // selection, mode and computed frequency are informational, not a
      // second authorization gate for an already configured transceiver.
      if (!canPtt || alreadyTransmitting || conflictingTxLease)
        throw new InvalidOperationException("发射机不可用或已有其他发射任务。");
      if (nowUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("A UTC timestamp is required.", nameof(nowUtc));
      long expected = actualTxCatHz > 0 ? actualTxCatHz :
        double.IsFinite(rfHz) && double.IsFinite(loHz) && rfHz > loHz
          ? checked((long)Math.Truncate(rfHz - loHz)) : 0;
      return new SstvTxGuard(satelliteId, transmitterId, expected,
        rfHz, mode, nowUtc);
    }

    /// <summary>
    /// A PTT-OFF command ACK is not sufficient for a safe completion
    /// message. Require an independent hardware-state read after the current
    /// PTT-OFF request, backed by a fresh hardware RX observation.
    /// </summary>
    public static bool HasConfirmedPttOff(
      bool pttState, bool applicationMayOwnPtt,
      DateTime lastHardwareReadUtc, DateTime pttOffRequestedUtc,
      DateTime nowUtc)
    {
      return !pttState && !applicationMayOwnPtt &&
        lastHardwareReadUtc.Kind == DateTimeKind.Utc &&
        pttOffRequestedUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        lastHardwareReadUtc >= pttOffRequestedUtc &&
        lastHardwareReadUtc <= nowUtc &&
        (nowUtc - lastHardwareReadUtc).TotalMilliseconds <= MaximumPttReadbackAgeMs;
    }

    public static bool StillSafe(SstvTxGuard armed,
      string? satelliteId, string? transmitterId, bool hasObservation,
      bool aboveHorizon, double expectedRfHz, double loHz,
      long actualTxCatHz, bool pttReadbackOn,
      DateTime pttReadbackUtc, DateTime nowUtc)
    {
      // A fresh hardware PTT-ON confirmation is the live interlock.
      // Frequency/mode/satellite changes are no longer independent reasons
      // to abort an SSTV picture; the operator controls radio configuration.
      return pttReadbackOn &&
        pttReadbackUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        pttReadbackUtc <= nowUtc &&
        (nowUtc - pttReadbackUtc).TotalMilliseconds <= MaximumPttReadbackAgeMs;
    }
  }
}
