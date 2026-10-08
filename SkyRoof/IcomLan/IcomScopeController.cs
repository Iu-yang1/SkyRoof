namespace SkyRoof
{
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
    private readonly Func<bool>? DirectLanRequestScopeOutput;
    private DateTime LastScopeOutputRequestUtc =
      DateTime.MinValue;

    internal IcomScopeControlPath EffectivePath { get; private set; } =
      IcomScopeControlPath.ReadOnly;

    internal bool LastRequestRouted { get; private set; }

    internal IcomScopeController(
      Func<bool> skyCatRequestScopeOutput,
      Func<bool>? directLanRequestScopeOutput = null)
    {
      SkyCatRequestScopeOutput =
        skyCatRequestScopeOutput ??
        throw new ArgumentNullException(
          nameof(skyCatRequestScopeOutput));
      DirectLanRequestScopeOutput =
        directLanRequestScopeOutput;
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
