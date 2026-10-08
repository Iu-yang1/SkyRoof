namespace SkyRoof
{
  internal enum IcomScopeControlKind
  {
    Mode,
    Span,
    Edge,
    ReferenceLevel,
    SweepSpeed,
    FixedEdge
  }

  public enum IcomScopeSweepSpeed : byte
  {
    Fast = 0,
    Mid = 1,
    Slow = 2
  }

  internal sealed class IcomScopeControlRequest
  {
    internal IcomScopeControlKind Kind { get; init; }
    internal byte Scope { get; init; }
    internal IcomScopeMode Mode { get; init; }
    internal long SpanHz { get; init; }
    internal int EdgeNumber { get; init; }
    internal double ReferenceDb { get; init; }
    internal IcomScopeSweepSpeed SweepSpeed { get; init; }
    internal int FrequencyRange { get; init; }
    internal long LowerFrequencyHz { get; init; }
    internal long UpperFrequencyHz { get; init; }

    internal static IcomScopeControlRequest ForMode(
      byte scope,
      IcomScopeMode mode) =>
      new()
      {
        Kind = IcomScopeControlKind.Mode,
        Scope = scope,
        Mode = mode
      };

    internal static IcomScopeControlRequest ForSpan(
      byte scope,
      long spanHz) =>
      new()
      {
        Kind = IcomScopeControlKind.Span,
        Scope = scope,
        SpanHz = spanHz
      };

    internal static IcomScopeControlRequest ForEdge(
      byte scope,
      int edgeNumber) =>
      new()
      {
        Kind = IcomScopeControlKind.Edge,
        Scope = scope,
        EdgeNumber = edgeNumber
      };

    internal static IcomScopeControlRequest ForReferenceLevel(
      byte scope,
      double referenceDb) =>
      new()
      {
        Kind = IcomScopeControlKind.ReferenceLevel,
        Scope = scope,
        ReferenceDb = referenceDb
      };

    internal static IcomScopeControlRequest ForSweepSpeed(
      byte scope,
      IcomScopeSweepSpeed speed) =>
      new()
      {
        Kind = IcomScopeControlKind.SweepSpeed,
        Scope = scope,
        SweepSpeed = speed
      };

    internal static IcomScopeControlRequest ForFixedEdge(
      int frequencyRange,
      int edgeNumber,
      long lowerHz,
      long upperHz) =>
      new()
      {
        Kind = IcomScopeControlKind.FixedEdge,
        FrequencyRange = frequencyRange,
        EdgeNumber = edgeNumber,
        LowerFrequencyHz = lowerHz,
        UpperFrequencyHz = upperHz
      };
  }

  /// <summary>
  /// Routes IC-9700 scope-control requests independently from the waveform
  /// source. Direct LAN control remains optional/experimental; with no direct
  /// callback configured it is intentionally a no-op.
  /// </summary>
  internal sealed class IcomScopeController
  {
    private static readonly TimeSpan ReassertInterval =
      TimeSpan.FromSeconds(2);

    private readonly Func<bool> SkyCatRequestScopeOutput;
    private readonly Func<IcomScopeControlRequest, bool>? SkyCatControlRequest;
    private readonly Func<bool>? DirectLanRequestScopeOutput;
    private readonly Func<IcomScopeControlRequest, bool>? DirectLanControlRequest;

    private DateTime LastScopeOutputRequestUtc =
      DateTime.MinValue;

    internal IcomScopeControlPath EffectivePath { get; private set; } =
      IcomScopeControlPath.ReadOnly;

    internal bool LastRequestRouted { get; private set; }

    internal IcomScopeController(
      Func<bool> skyCatRequestScopeOutput,
      Func<IcomScopeControlRequest, bool>? skyCatControlRequest = null,
      Func<bool>? directLanRequestScopeOutput = null,
      Func<IcomScopeControlRequest, bool>? directLanControlRequest = null)
    {
      SkyCatRequestScopeOutput =
        skyCatRequestScopeOutput ??
        throw new ArgumentNullException(
          nameof(skyCatRequestScopeOutput));

      SkyCatControlRequest =
        skyCatControlRequest;
      DirectLanRequestScopeOutput =
        directLanRequestScopeOutput;
      DirectLanControlRequest =
        directLanControlRequest;
    }

    internal static IcomScopeControlPath ResolveControlPath(
      IcomLanSpectrumSource dataSource,
      IcomScopeControlPath configuredPath)
    {
      if (configuredPath != IcomScopeControlPath.Auto)
        return configuredPath;

      return dataSource switch
      {
        IcomLanSpectrumSource.SkyCat =>
          IcomScopeControlPath.SkyCat,
        IcomLanSpectrumSource.DirectLan =>
          IcomScopeControlPath.DirectLan,
        _ =>
          IcomScopeControlPath.ReadOnly
      };
    }

    internal bool RequestOutputIfDue(
      IcomLanSpectrumSource dataSource,
      IcomScopeControlPath configuredPath,
      bool force)
    {
      return RequestOutputIfDue(
        dataSource,
        configuredPath,
        force,
        DateTime.UtcNow);
    }

    internal bool RequestOutputIfDue(
      IcomLanSpectrumSource dataSource,
      IcomScopeControlPath configuredPath,
      bool force,
      DateTime nowUtc)
    {
      EffectivePath =
        ResolveControlPath(
          dataSource,
          configuredPath);

      Func<bool>? request =
        EffectivePath switch
        {
          IcomScopeControlPath.SkyCat =>
            SkyCatRequestScopeOutput,
          IcomScopeControlPath.DirectLan =>
            DirectLanRequestScopeOutput,
          _ =>
            null
        };

      if (request == null)
      {
        LastRequestRouted = false;
        return false;
      }

      if (!force &&
          nowUtc - LastScopeOutputRequestUtc <
            ReassertInterval)
        return false;

      LastScopeOutputRequestUtc = nowUtc;
      LastRequestRouted = request();
      return LastRequestRouted;
    }

    internal bool RequestControl(
      IcomLanSpectrumSource dataSource,
      IcomScopeControlPath configuredPath,
      IcomScopeControlRequest request)
    {
      if (request == null)
        throw new ArgumentNullException(
          nameof(request));

      EffectivePath =
        ResolveControlPath(
          dataSource,
          configuredPath);

      Func<IcomScopeControlRequest, bool>? send =
        EffectivePath switch
        {
          IcomScopeControlPath.SkyCat =>
            SkyCatControlRequest,
          IcomScopeControlPath.DirectLan =>
            DirectLanControlRequest,
          _ =>
            null
        };

      LastRequestRouted =
        send?.Invoke(request) ?? false;

      return LastRequestRouted;
    }

    internal void Reset()
    {
      LastScopeOutputRequestUtc =
        DateTime.MinValue;
      LastRequestRouted = false;
      EffectivePath =
        IcomScopeControlPath.ReadOnly;
    }
  }
}
