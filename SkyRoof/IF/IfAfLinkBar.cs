using System;

namespace SkyRoof.IF
{
  /// <summary>Local receive stages only. Green V-MIC means a paired Windows
  /// cable and active render endpoint, NOT that WSJT-X is decoding.</summary>
  public enum IfAfLinkState { Inactive, Ok, Warning, Error }

  public readonly record struct IfAfLinkIndicators(
    IfAfLinkState If, IfAfLinkState Ddc, IfAfLinkState Af,
    IfAfLinkState VirtualMic);

  public static class IfAfLinkBar
  {
    /// <summary>Compute telemetry independently of WinForms painting.
    /// Compare cumulative counters with the preceding visible snapshot to
    /// avoid a permanent warning from an already recovered startup underrun.</summary>
    public static IfAfLinkIndicators Evaluate(bool enabled,
      IfAfAudioRoute route, IfAfStatus? current, IfAfStatus? previous,
      DateTime nowUtc, bool holdRecentClipWarning = false)
    {
      if (!enabled) return new(IfAfLinkState.Inactive,
        IfAfLinkState.Inactive, IfAfLinkState.Inactive, IfAfLinkState.Inactive);

      if (!current.HasValue)
        return new(IfAfLinkState.Warning, IfAfLinkState.Warning,
          IfAfLinkState.Warning, IfAfLinkState.Warning);

      var s = current.Value;
      bool started = s.Running;
      bool fresh = s.LastInputUtc != default &&
        (nowUtc - s.LastInputUtc).TotalSeconds is >= -2 and <= 2;
      bool inputMoving = started && fresh && s.InputSamples > 0;
      // Cumulative counters are historical diagnostics, not latched alarms.
      // The first observation establishes a baseline (it may already
      // contain many clips from an earlier burst).
      bool sameStream = previous.HasValue &&
        s.OutputSamples >= previous.Value.OutputSamples &&
        s.InputSamples >= previous.Value.InputSamples;
      bool dropsIncreased = sameStream &&
        s.DroppedBlocks > previous!.Value.DroppedBlocks;
      bool clipsIncreased = sameStream &&
        s.ClippedSamples > previous!.Value.ClippedSamples;
      bool outputMoving = s.OutputSamples > 0 &&
        (!previous.HasValue ||
          s.OutputSamples < previous.Value.OutputSamples ||
          s.OutputSamples > previous.Value.OutputSamples ||
          s.InputSamples == previous.Value.InputSamples);
      bool processing = inputMoving && outputMoving &&
        s.OutputSamples <= s.InputSamples;
      bool underrunsIncreased = sameStream &&
        s.OutputUnderrunSamples > previous!.Value.OutputUnderrunSamples;
      bool highLag = s.ProcessingQueueLagMs > 200;
      bool paired = s.VirtualDeviceStatus.Contains("endpoints detected",
        StringComparison.OrdinalIgnoreCase);

      IfAfLinkState fail = s.Detail.Contains("failed",
        StringComparison.OrdinalIgnoreCase)
          ? IfAfLinkState.Error : IfAfLinkState.Warning;

      IfAfLinkState input = !started
        ? fail : inputMoving ? IfAfLinkState.Ok : IfAfLinkState.Warning;
      IfAfLinkState ddc = !started
        ? fail : !processing ? IfAfLinkState.Warning :
          dropsIncreased || highLag ? IfAfLinkState.Warning : IfAfLinkState.Ok;
      IfAfLinkState af = !started || !s.RenderRunning ? fail :
        !processing ? IfAfLinkState.Warning :
          underrunsIncreased || clipsIncreased || holdRecentClipWarning
            ? IfAfLinkState.Warning : IfAfLinkState.Ok;
      // In manual mode the application cannot prove that a matching capture
      // endpoint exists; do not use green for an unverified microphone.
      IfAfLinkState mic = !s.RenderRunning ? fail :
        route != IfAfAudioRoute.AutoSignedCable || !paired
          ? IfAfLinkState.Warning :
        af == IfAfLinkState.Ok ? IfAfLinkState.Ok : af;
      return new(input, ddc, af, mic);
    }
  }
}
