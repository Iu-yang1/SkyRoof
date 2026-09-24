namespace SkyRoof
{
  internal sealed class IcomScopeFrame
  {
    internal DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    internal byte Scope { get; init; }
    internal byte DivisionCurrent { get; init; }
    internal byte DivisionMaximum { get; init; }
    internal byte Mode { get; init; }
    internal long FrequencyAHz { get; init; }
    internal long FrequencyBHz { get; init; }
    internal bool OutOfRange { get; init; }
    internal byte[] Samples { get; init; } = Array.Empty<byte>();

    internal string ScopeName => Scope == 1 ? "SUB" : "MAIN";

    internal string ModeName => Mode switch
    {
      0 => "CENTER",
      1 => "FIXED",
      2 => "SCROLL-C",
      3 => "SCROLL-F",
      _ => $"MODE {Mode}"
    };

    internal long CenterFrequencyHz =>
      Mode is 0 or 2
        ? FrequencyAHz
        : FrequencyAHz > 0 && FrequencyBHz > FrequencyAHz
          ? FrequencyAHz + (FrequencyBHz - FrequencyAHz) / 2
          : 0;

    internal long SpanHz =>
      Mode is 0 or 2
        ? FrequencyBHz
        : FrequencyAHz > 0 && FrequencyBHz > FrequencyAHz
          ? FrequencyBHz - FrequencyAHz
          : 0;
  }
}
