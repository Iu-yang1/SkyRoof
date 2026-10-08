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
    internal bool SweepComplete { get; init; } = true;
    internal byte[] Samples { get; init; } = Array.Empty<byte>();

    internal string ScopeName => Scope == 1 ? "SUB" : "MAIN";

    internal IcomScopeGeometry Geometry =>
      IcomScopeGeometry.FromFrame(this);

    internal string ModeName => Geometry.ModeName;

    internal long CenterFrequencyHz =>
      Geometry.CenterFrequencyHz;

    internal long SpanHz =>
      Geometry.SpanHz;
  }
}
