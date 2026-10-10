namespace SkyRoof
{
  /// <summary>
  /// One pending WinForms callback, but keep complete 475-bin sweep frames
  /// separately from partial serial waveform updates for each receiver.
  /// If a new partial update overwrites the just-completed sweep, the
  /// spectrum trace still works while the waterfall silently misses rows.
  /// </summary>
  internal sealed class IcomScopeUiFrameMailbox
  {
    private readonly object gate = new();
    private readonly IcomScopeFrame?[] completed = new IcomScopeFrame?[2];
    private readonly IcomScopeFrame?[] partial = new IcomScopeFrame?[2];
    private bool callbackScheduled;
    private long replacedFrames;
    private long replacedCompleteSweeps;

    internal long ReplacedFrames
    {
      get { lock (gate) return replacedFrames; }
    }

    internal long ReplacedCompleteSweeps
    {
      get { lock (gate) return replacedCompleteSweeps; }
    }

    internal bool Offer(IcomScopeFrame frame)
    {
      ArgumentNullException.ThrowIfNull(frame);
      lock (gate)
      {
        int receiver = frame.Scope == 1 ? 1 : 0;
        IcomScopeFrame?[] target = frame.SweepComplete
          ? completed : partial;

        // Reject an out-of-order frame rather than rolling back its
        // receiver's newest plot or time axis.
        IcomScopeFrame? newest = partial[receiver];
        if (completed[receiver] is IcomScopeFrame complete &&
            (newest == null || newest.TimestampUtc < complete.TimestampUtc))
          newest = complete;
        if (newest != null && frame.TimestampUtc <= newest.TimestampUtc)
          return false;

        if (target[receiver] != null)
        {
          replacedFrames++;
          if (frame.SweepComplete)
            replacedCompleteSweeps++;
        }

        target[receiver] = frame;
        if (frame.SweepComplete && partial[receiver] != null)
        {
          // An older partial is superseded by the completed sweep.
          partial[receiver] = null;
          replacedFrames++;
        }

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
        // At most four items: one completed sweep and one newer partial
        // for MAIN and SUB. Sort for the existing timestamp gate.
        var result = completed.Concat(partial)
          .Where(x => x != null)
          .Select(x => x!)
          .OrderBy(x => x.TimestampUtc.Ticks)
          .ToArray();
        Array.Clear(completed);
        Array.Clear(partial);
        callbackScheduled = false;
        return result;
      }
    }

    internal void Clear()
    {
      lock (gate)
      {
        Array.Clear(completed);
        Array.Clear(partial);
        callbackScheduled = false;
        replacedFrames = 0;
        replacedCompleteSweeps = 0;
      }
    }
  }
}
