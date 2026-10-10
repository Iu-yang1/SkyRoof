namespace SkyRoof
{
  /// <summary>
  /// At most one pending WinForms BeginInvoke callback, retaining one
  /// newest frame per MAIN/SUB receiver. Keeping both matters when AUTO
  /// scope selection prefers MAIN during interleaved CI-V frame bursts.
  /// </summary>
  internal sealed class IcomScopeUiFrameMailbox
  {
    private readonly object gate = new();
    private readonly IcomScopeFrame?[] latest = new IcomScopeFrame?[2];
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
        int receiver = frame.Scope == 1 ? 1 : 0;
        IcomScopeFrame? previous = latest[receiver];
        if (previous != null)
        {
          if (frame.TimestampUtc.Ticks <= previous.TimestampUtc.Ticks)
            return false;
          replacedFrames++;
        }

        latest[receiver] = frame;
        if (callbackScheduled)
          return false;

        callbackScheduled = true;
        return true;
      }
    }

    internal IcomScopeFrame[] TakeAll()
    {
      lock (gate)
      {
        var result = latest.Where(x => x != null)
          .Select(x => x!).OrderBy(x => x.TimestampUtc.Ticks)
          .ToArray();
        latest[0] = null;
        latest[1] = null;
        callbackScheduled = false;
        return result;
      }
    }

    internal void Clear()
    {
      lock (gate)
      {
        latest[0] = null;
        latest[1] = null;
        callbackScheduled = false;
        replacedFrames = 0;
      }
    }
  }
}
