namespace SkyRoof
{
  /// <summary>
  /// UI-neutral state for the latest MAIN/SUB IC-9700 scope frames. Capture
  /// transports publish frames; the panel only selects which state to render.
  /// </summary>
  internal sealed class IcomScopeState
  {
    private IcomScopeFrame? MainFrameValue;
    private IcomScopeFrame? SubFrameValue;
    private int SelectedBandValue = (int)IcomLanScopeBand.Auto;

    internal IcomLanScopeBand SelectedBand
    {
      get =>
        (IcomLanScopeBand)Volatile.Read(
          ref SelectedBandValue);
      set =>
        Volatile.Write(
          ref SelectedBandValue,
          Math.Clamp((int)value, 0, 2));
    }

    internal void Update(IcomScopeFrame frame)
    {
      if (frame == null)
        throw new ArgumentNullException(nameof(frame));

      if (frame.Scope == 1)
        Volatile.Write(ref SubFrameValue, frame);
      else
        Volatile.Write(ref MainFrameValue, frame);
    }

    internal bool ShouldDisplay(IcomScopeFrame frame)
    {
      if (frame == null)
        return false;

      return SelectedBand switch
      {
        IcomLanScopeBand.Main => frame.Scope == 0,
        IcomLanScopeBand.Sub => frame.Scope == 1,
        _ => true
      };
    }

    internal IcomScopeFrame? LatestSelectedFrame
    {
      get
      {
        IcomScopeFrame? main =
          Volatile.Read(ref MainFrameValue);
        IcomScopeFrame? sub =
          Volatile.Read(ref SubFrameValue);

        return SelectedBand switch
        {
          IcomLanScopeBand.Main => main,
          IcomLanScopeBand.Sub => sub,
          _ => Newer(main, sub)
        };
      }
    }

    internal IcomScopeGeometry? SelectedGeometry =>
      LatestSelectedFrame?.Geometry;

    internal void Clear()
    {
      Volatile.Write(
        ref MainFrameValue,
        null);
      Volatile.Write(
        ref SubFrameValue,
        null);
    }

    private static IcomScopeFrame? Newer(
      IcomScopeFrame? first,
      IcomScopeFrame? second)
    {
      if (first == null)
        return second;
      if (second == null)
        return first;

      return second.TimestampUtc > first.TimestampUtc
        ? second
        : first;
    }
  }
}
