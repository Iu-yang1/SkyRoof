namespace SkyRoof
{
  /// <summary>
  /// Separates queued operator scope requests from radio-confirmed values.
  /// A routed SkyCAT command is not proof that the IC-9700 applied it.
  /// The 27 00 waveform confirms MODE/SPAN; SCOPE_READ_FIELD confirms
  /// other settings. Timeout never falsely acknowledges a write.
  /// This object belongs to the WinForms UI thread.
  /// </summary>
  internal sealed class IcomScopePendingControls
  {
    private readonly record struct Key(byte Scope, IcomScopeControlKind Kind);
    private sealed record Entry(IcomScopeControlRequest Request, DateTime QueuedUtc);
    private readonly Dictionary<Key, Entry> entries = new();
    internal static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(20);
    internal int Count => entries.Count;

    internal void Clear() => entries.Clear();

    internal void Track(IcomScopeControlRequest request, DateTime nowUtc)
    {
      ArgumentNullException.ThrowIfNull(request);
      entries[new Key(request.Scope, request.Kind)] =
        new Entry(request, nowUtc);
    }

    internal bool TryGet(
      byte scope, IcomScopeControlKind kind,
      out IcomScopeControlRequest request)
    {
      if (entries.TryGetValue(new Key(scope, kind), out Entry? entry))
      {
        request = entry.Request;
        return true;
      }
      request = null!;
      return false;
    }

    internal IcomScopeControlRequest[] Expire(DateTime nowUtc)
    {
      var expired = entries
        .Where(item => nowUtc - item.Value.QueuedUtc >= ConfirmationTimeout)
        .Select(item => (item.Key, item.Value.Request))
        .ToArray();
      foreach (var item in expired)
        entries.Remove(item.Key);
      return expired.Select(item => item.Request).ToArray();
    }

    internal void ObserveFrame(IcomScopeFrame frame)
    {
      byte scope = frame.Scope;
      ConfirmIf(scope, IcomScopeControlKind.Mode, x =>
        (byte)x.Mode == frame.Mode);

      // Only CENTER geometry directly represents the configured span.
      // Fixed/scroll geometry contains edge frequencies and cannot be
      // used to infer a radio's center-span setting.
      if (frame.Mode == (byte)IcomScopeMode.Center &&
          frame.Geometry.IsValid)
        ConfirmIf(scope, IcomScopeControlKind.Span,
          x => x.SpanHz == frame.Geometry.SpanHz);
    }

    internal void ObserveReadback(IcomScopeReadbackState state)
    {
      foreach (byte scope in new byte[] { 0, 1 })
      {
        string prefix = scope == 0 ? "MAIN." : "SUB.";
        if (state.HasField(prefix + "MODE"))
          ConfirmIf(scope, IcomScopeControlKind.Mode,
            x => x.Mode == (scope == 0 ? state.MainMode : state.SubMode));
        if (state.HasField(prefix + "SPAN"))
          ConfirmIf(scope, IcomScopeControlKind.Span,
            x => x.SpanHz == (scope == 0 ? state.MainSpanHz : state.SubSpanHz));
        if (state.HasField(prefix + "EDGE"))
          ConfirmIf(scope, IcomScopeControlKind.Edge,
            x => x.EdgeNumber == (scope == 0 ? state.MainEdge : state.SubEdge));
        if (state.HasField(prefix + "REF"))
          ConfirmIf(scope, IcomScopeControlKind.ReferenceLevel,
            x => Math.Abs(x.ReferenceDb -
              (scope == 0 ? state.MainReferenceDb : state.SubReferenceDb)) < 0.05);
        if (state.HasField(prefix + "SPEED"))
          ConfirmIf(scope, IcomScopeControlKind.SweepSpeed,
            x => x.SweepSpeed == (scope == 0 ? state.MainSpeed : state.SubSpeed));
        if (state.HasField(prefix + "VBW"))
          ConfirmIf(scope, IcomScopeControlKind.Vbw,
            x => x.Vbw == (scope == 0 ? state.MainVbw : state.SubVbw));
      }
      if (state.HasField("SELECT"))
        ConfirmIf(state.SelectedScope, IcomScopeControlKind.SelectedScope,
          x => x.Scope == state.SelectedScope);
      if (state.HasField("TX"))
        ConfirmIf(0, IcomScopeControlKind.ScopeDuringTx,
          x => x.Enabled == state.ScopeDuringTx);
      if (state.HasField("CENTER"))
        ConfirmIf(0, IcomScopeControlKind.CenterType,
          x => x.CenterType == state.CenterType);
      if (state.HasField("MARKER"))
        ConfirmIf(0, IcomScopeControlKind.MarkerPosition,
          x => x.MarkerPosition == state.MarkerPosition);
    }

    private void ConfirmIf(
      byte scope, IcomScopeControlKind kind,
      Func<IcomScopeControlRequest, bool> matches)
    {
      var key = new Key(scope, kind);
      if (entries.TryGetValue(key, out Entry? entry) &&
          matches(entry.Request))
        entries.Remove(key);
    }
  }
}
