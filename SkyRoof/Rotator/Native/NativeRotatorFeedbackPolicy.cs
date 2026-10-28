using System;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Separates a silent controller from a dead transport. RF transmissions can
/// briefly inhibit GS-232B or TCP-to-serial position replies while the socket
/// itself stays valid. Never treat silence as permission to keep moving.
/// </summary>
internal static class NativeRotatorFeedbackPolicy
{
  // Initial sync may fail on a powered-off controller. Established idle
  // sessions deserve a longer receive grace to survive transient TX RFI.
  internal const int InitialMinimumMisses = 4;
  internal const int EstablishedMinimumMisses = 6;
  internal const int InitialSilenceMs = 4000;
  internal const int EstablishedSilenceMs = 7000;

  internal static bool ShouldReconnect(
    int consecutiveMisses, long elapsedWithoutReplyMs,
    bool hadValidSessionFeedback) =>
    consecutiveMisses >= (hadValidSessionFeedback
      ? EstablishedMinimumMisses : InitialMinimumMisses) &&
    elapsedWithoutReplyMs >= (hadValidSessionFeedback
      ? EstablishedSilenceMs : InitialSilenceMs);

  // A confirmed target / continuous jog may still be physically active.
  // A few noisy polls should not force a TCP reconnect, but the first
  // sustained feedback outage must request a STOP and latch further motion.
  internal static bool MustStopPossibleMotion(
    int consecutiveMisses, long ageOfLastFeedbackMs, int pollMs,
    bool potentiallyMoving) =>
    potentiallyMoving && consecutiveMisses >= 2 &&
    ageOfLastFeedbackMs >= Math.Max(2500L,
      3L * Math.Clamp(pollMs, 200, 30000));
}
