namespace SkyRoof
{
  internal enum IcomScopeMode : byte
  {
    Center = 0,
    Fixed = 1,
    ScrollCenter = 2,
    ScrollFixed = 3
  }

  /// <summary>
  /// Frequency-axis metadata derived from an IC-9700 CI-V 27 00 scope frame.
  /// The waveform transport remains responsible only for decoding raw frame
  /// fields; display and control code can consume this normalized geometry.
  /// </summary>
  internal readonly struct IcomScopeGeometry
  {
    internal byte RawMode { get; }
    internal long FrequencyAHz { get; }
    internal long FrequencyBHz { get; }
    internal long CenterFrequencyHz { get; }
    internal long SpanHz { get; }
    internal long LowerFrequencyHz { get; }
    internal long UpperFrequencyHz { get; }
    internal bool IsValid { get; }

    internal string ModeName => RawMode switch
    {
      (byte)IcomScopeMode.Center => "CENTER",
      (byte)IcomScopeMode.Fixed => "FIXED",
      (byte)IcomScopeMode.ScrollCenter => "SCROLL-C",
      (byte)IcomScopeMode.ScrollFixed => "SCROLL-F",
      _ => $"MODE {RawMode}"
    };

    private IcomScopeGeometry(
      byte rawMode,
      long frequencyAHz,
      long frequencyBHz,
      long centerFrequencyHz,
      long spanHz,
      long lowerFrequencyHz,
      long upperFrequencyHz,
      bool isValid)
    {
      RawMode = rawMode;
      FrequencyAHz = frequencyAHz;
      FrequencyBHz = frequencyBHz;
      CenterFrequencyHz = centerFrequencyHz;
      SpanHz = spanHz;
      LowerFrequencyHz = lowerFrequencyHz;
      UpperFrequencyHz = upperFrequencyHz;
      IsValid = isValid;
    }

    internal static IcomScopeGeometry FromFrame(
      IcomScopeFrame frame)
    {
      if (frame == null)
        throw new ArgumentNullException(nameof(frame));

      if (frame.Mode == (byte)IcomScopeMode.Center)
      {
        long center = frame.FrequencyAHz;
        long span = frame.FrequencyBHz;
        bool valid = center > 0 && span > 0;

        if (!valid)
          return new IcomScopeGeometry(
            frame.Mode,
            frame.FrequencyAHz,
            frame.FrequencyBHz,
            center,
            span,
            0,
            0,
            false);

        long lower = center - span / 2;
        long upper = lower + span;

        return new IcomScopeGeometry(
          frame.Mode,
          frame.FrequencyAHz,
          frame.FrequencyBHz,
          center,
          span,
          lower,
          upper,
          true);
      }

      bool hasEdges =
        frame.FrequencyAHz > 0 &&
        frame.FrequencyBHz > frame.FrequencyAHz;

      if (!hasEdges)
        return new IcomScopeGeometry(
          frame.Mode,
          frame.FrequencyAHz,
          frame.FrequencyBHz,
          0,
          0,
          0,
          0,
          false);

      long edgeSpan =
        frame.FrequencyBHz - frame.FrequencyAHz;
      long edgeCenter =
        frame.FrequencyAHz + edgeSpan / 2;

      return new IcomScopeGeometry(
        frame.Mode,
        frame.FrequencyAHz,
        frame.FrequencyBHz,
        edgeCenter,
        edgeSpan,
        frame.FrequencyAHz,
        frame.FrequencyBHz,
        true);
    }

    internal bool ContainsFrequency(long frequencyHz) =>
      IsValid &&
      frequencyHz >= LowerFrequencyHz &&
      frequencyHz <= UpperFrequencyHz;

    internal long FrequencyAtFraction(double fraction)
    {
      if (!IsValid || SpanHz <= 0)
        return 0;

      double clamped =
        Math.Clamp(fraction, 0.0, 1.0);

      return LowerFrequencyHz +
        checked((long)Math.Round(SpanHz * clamped));
    }

    internal double FractionForFrequency(long frequencyHz)
    {
      if (!IsValid || SpanHz <= 0)
        return double.NaN;

      return
        (frequencyHz - LowerFrequencyHz) /
        (double)SpanHz;
    }
  }
}
