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
      if (!operatorApproved)
        throw new InvalidOperationException("请先确认发射频率、许可和功率。");
      if (string.IsNullOrWhiteSpace(satelliteId) ||
          string.IsNullOrWhiteSpace(transmitterId) || isTerrestrial ||
          !hasUplink || !hasObservation || !isAboveHorizon)
        throw new InvalidOperationException("请选择当前可见并有有效上行频率的卫星。");
      if (mode is not (Slicer.Mode.USB or Slicer.Mode.USB_D or
        Slicer.Mode.FM or Slicer.Mode.FM_D))
        throw new InvalidOperationException("SSTV 发射需要 USB 或 FM 上行模式。");
      if (!double.IsFinite(rfHz) || !double.IsFinite(loHz) ||
          rfHz <= 0 || !SatnogsDbTransmitter.IsHamFrequency(rfHz))
        throw new InvalidOperationException("所选上行频率不在支持的业余卫星频段。");
      if (!canPtt || alreadyTransmitting || conflictingTxLease)
        throw new InvalidOperationException("发射机不可用或已有其他发射任务。");

      long expected = checked((long)Math.Truncate(rfHz - loHz));
      if (expected <= 0 || actualTxCatHz <= 0 ||
          Math.Abs((double)actualTxCatHz - expected) > FrequencyToleranceHz)
        throw new InvalidOperationException("未能确认发射机实际上行频率，请检查 CAT 读回。");
      if (nowUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("A UTC timestamp is required.", nameof(nowUtc));

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
      if (!hasObservation || !aboveHorizon ||
          satelliteId != armed.SatelliteId ||
          transmitterId != armed.TransmitterId ||
          !double.IsFinite(expectedRfHz) || !double.IsFinite(loHz) ||
          !SatnogsDbTransmitter.IsHamFrequency(expectedRfHz) ||
          !pttReadbackOn || nowUtc.Kind != DateTimeKind.Utc ||
          pttReadbackUtc.Kind != DateTimeKind.Utc ||
          pttReadbackUtc > nowUtc ||
          (nowUtc - pttReadbackUtc).TotalMilliseconds > MaximumPttReadbackAgeMs)
        return false;

      // Satellite Doppler is allowed to move during a multi-minute picture;
      // compare to the *current* model, never to the original static CAT Hz.
      double expectedCatHz = expectedRfHz - loHz;
      return actualTxCatHz > 0 &&
        Math.Abs(actualTxCatHz - expectedCatHz) <= FrequencyToleranceHz;
    }
  }
}
