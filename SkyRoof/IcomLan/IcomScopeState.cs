namespace SkyRoof
{
  /// <summary>
  /// UI-neutral state for the latest MAIN/SUB IC-9700 scope frames. Capture
  /// transports publish frames; the panel only selects which state to render.
  /// </summary>
  internal sealed class IcomScopeState
  {
    private static readonly TimeSpan AutoFailoverDelay =
      TimeSpan.FromMilliseconds(750);

    private IcomScopeFrame? MainFrameValue;
    private IcomScopeFrame? SubFrameValue;
    private int SelectedBandValue = (int)IcomLanScopeBand.Auto;
    private int AutoScopeValue = -1;

    internal IcomLanScopeBand SelectedBand
    {
      get =>
        (IcomLanScopeBand)Volatile.Read(
          ref SelectedBandValue);
      set
      {
        IcomLanScopeBand normalized =
          (IcomLanScopeBand)Math.Clamp(
            (int)value,
            0,
            2);

        Volatile.Write(
          ref SelectedBandValue,
          (int)normalized);

        if (normalized ==
            IcomLanScopeBand.Auto)
          RefreshAutoScopeFromCache();
      }
    }

    internal void Update(IcomScopeFrame frame)
    {
      if (frame == null)
        throw new ArgumentNullException(nameof(frame));

      if (frame.Scope == 1)
        Volatile.Write(ref SubFrameValue, frame);
      else
        Volatile.Write(ref MainFrameValue, frame);

      if (SelectedBand !=
          IcomLanScopeBand.Auto)
        return;

      // AUTO is intentionally sticky. Prefer MAIN whenever it is active, and
      // fail over to SUB only after MAIN has gone stale. This prevents passive
      // captures that contain both receivers from alternating the entire view
      // and invalidating the waterfall mapping on every frame.
      if (frame.Scope == 0)
      {
        Volatile.Write(
          ref AutoScopeValue,
          0);
        return;
      }

      IcomScopeFrame? main =
        Volatile.Read(
          ref MainFrameValue);

      if (main == null ||
          frame.TimestampUtc -
            main.TimestampUtc >
            AutoFailoverDelay)
        Volatile.Write(
          ref AutoScopeValue,
          1);
    }

    internal bool ShouldDisplay(IcomScopeFrame frame)
    {
      if (frame == null)
        return false;

      return SelectedBand switch
      {
        IcomLanScopeBand.Main =>
          frame.Scope == 0,
        IcomLanScopeBand.Sub =>
          frame.Scope == 1,
        _ =>
          Volatile.Read(
            ref AutoScopeValue) < 0 ||
          frame.Scope ==
            Volatile.Read(
              ref AutoScopeValue)
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
          IcomLanScopeBand.Main =>
            main,
          IcomLanScopeBand.Sub =>
            sub,
          _ =>
            Volatile.Read(
              ref AutoScopeValue) switch
            {
              0 => main ?? sub,
              1 => sub ?? main,
              _ => Newer(main, sub)
            }
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
      Volatile.Write(
        ref AutoScopeValue,
        -1);
    }

    private void RefreshAutoScopeFromCache()
    {
      IcomScopeFrame? main =
        Volatile.Read(
          ref MainFrameValue);
      IcomScopeFrame? sub =
        Volatile.Read(
          ref SubFrameValue);

      if (main == null)
      {
        Volatile.Write(
          ref AutoScopeValue,
          sub == null ? -1 : 1);
        return;
      }

      if (sub == null ||
          sub.TimestampUtc -
            main.TimestampUtc <=
            AutoFailoverDelay)
      {
        Volatile.Write(
          ref AutoScopeValue,
          0);
        return;
      }

      Volatile.Write(
        ref AutoScopeValue,
        1);
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
