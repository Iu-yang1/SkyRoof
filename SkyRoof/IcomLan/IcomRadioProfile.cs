namespace SkyRoof
{
  [Flags]
  internal enum IcomRadioTransport
  {
    None = 0,
    Usb = 1,
    Ethernet = 2,
    Wlan = 4
  }

  // Candidate means a published command or transport is known, not that this
  // SkyRoof implementation has completed an end-to-end hardware acceptance test.
  internal enum IcomCapabilityStatus
  {
    Unavailable = 0,
    Candidate = 1,
    Verified = 2
  }

  internal sealed record IcomRadioProfile(
    string Model,
    byte DefaultCivAddress,
    int ReceiverCount,
    IcomRadioTransport Transports,
    IcomCapabilityStatus Split,
    IcomCapabilityStatus CrossBandSplit,
    IcomCapabilityStatus FullDuplex,
    IcomCapabilityStatus Scope,
    IcomCapabilityStatus UsbIfOutput)
  {
    internal int GetFrequencyByteCount(long frequencyHz) =>
      Model == "IC-905" && frequencyHz >= 10_000_000_000L
        ? IcomCivFrequencyCodec.ExtendedByteCount
        : IcomCivFrequencyCodec.StandardByteCount;

    internal bool MayUseMainSubScope => ReceiverCount > 1;

    internal bool CanUseVerifiedFullDuplex =>
      FullDuplex == IcomCapabilityStatus.Verified;
  }

  /// <summary>
  /// Describes known Icom protocol candidates. This is not a hardware probe:
  /// CI-V addresses are user-configurable and do not uniquely identify a model.
  /// Unknown capabilities must not trigger radio write operations.
  /// </summary>
  internal static class IcomRadioProfiles
  {
    internal static readonly IcomRadioProfile Ic9700 = new(
      "IC-9700", 0xA2, 2,
      IcomRadioTransport.Usb | IcomRadioTransport.Ethernet,
      IcomCapabilityStatus.Verified,
      IcomCapabilityStatus.Verified,
      IcomCapabilityStatus.Verified,
      IcomCapabilityStatus.Verified,
      IcomCapabilityStatus.Candidate);

    internal static readonly IcomRadioProfile Ic705 = new(
      "IC-705", 0xA4, 1,
      IcomRadioTransport.Usb | IcomRadioTransport.Wlan,
      IcomCapabilityStatus.Candidate,
      IcomCapabilityStatus.Unavailable,
      IcomCapabilityStatus.Unavailable,
      IcomCapabilityStatus.Candidate,
      IcomCapabilityStatus.Candidate);

    internal static readonly IcomRadioProfile Ic905 = new(
      "IC-905", 0xAC, 1,
      IcomRadioTransport.Usb | IcomRadioTransport.Ethernet,
      IcomCapabilityStatus.Candidate,
      IcomCapabilityStatus.Unavailable,
      IcomCapabilityStatus.Unavailable,
      IcomCapabilityStatus.Candidate,
      IcomCapabilityStatus.Candidate);

    private static readonly IReadOnlyList<IcomRadioProfile> Known =
      Array.AsReadOnly(new[] { Ic9700, Ic705, Ic905 });

    internal static IReadOnlyList<IcomRadioProfile> All => Known;

    internal static bool TryGetByModel(
      string? model, out IcomRadioProfile? profile)
    {
      foreach (IcomRadioProfile candidate in Known)
      {
        if (string.Equals(candidate.Model, model?.Trim(),
              StringComparison.OrdinalIgnoreCase))
        {
          profile = candidate;
          return true;
        }
      }

      profile = null;
      return false;
    }

    // The existing Direct LAN scope writer is IC-9700-specific. New models
    // may be identified read-only but must not reach this legacy writer.
    internal static bool SupportsExistingDirectLanScope(string? model) =>
      TryGetByModel(model, out IcomRadioProfile? profile) &&
      ReferenceEquals(profile, Ic9700);

    // Returns only a candidate based on an observed *default* address.
    // Callers must not use this as proof of identity or enable TX/Split.
    internal static bool TryGetCandidateByDefaultAddress(
      byte address, out IcomRadioProfile? profile)
    {
      foreach (IcomRadioProfile candidate in Known)
      {
        if (candidate.DefaultCivAddress == address)
        {
          profile = candidate;
          return true;
        }
      }

      profile = null;
      return false;
    }
  }
}
