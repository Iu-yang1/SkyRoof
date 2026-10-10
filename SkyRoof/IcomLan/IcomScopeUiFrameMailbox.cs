namespace SkyRoof
{
  /// <summary>
  /// Collapses high-rate scope frames to a single pending WinForms UI
  /// callback. The newest frame wins; delayed UI layouts never accumulate
  /// an unbounded BeginInvoke queue of outdated waveform frames.
  /// </summary>
  internal sealed class IcomScopeUiFrameMailbox
  {
    private readonly object gate = new();
    private IcomScopeFrame? latest;
    private bool callbackScheduled;
    private long replacedFrames;

    internal long ReplacedFrames
    {
      get { lock (gate) return replacedFrames; }
    }

    internal bool Offer(IcomScopeFrame frame)
    {
      ArgumentNullException.ThrowIfNull(frame);
      lock (gate)
      {
        if (latest != null)
        {
          if (frame.TimestampUtc.Ticks <= latest.TimestampUtc.Ticks)
            return false;
          replacedFrames++;
        }

        latest = frame;
        if (callbackScheduled)
          return false;

        callbackScheduled = true;
        return true;
      }
    }

    internal IcomScopeFrame? Take()
    {
      lock (gate)
      {
        IcomScopeFrame? result = latest;
        latest = null;
        callbackScheduled = false;
        return result;
      }
    }

    internal void Clear()
    {
      lock (gate)
      {
        latest = null;
        callbackScheduled = false;
        replacedFrames = 0;
      }
    }
  }
}
