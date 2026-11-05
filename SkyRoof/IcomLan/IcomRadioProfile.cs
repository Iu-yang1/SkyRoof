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

  internal sealed record IcomRadioProfile
  {
    internal required string Model { get; init; }
    internal required byte DefaultCivAddress { get; init; }
    internal required int ReceiverCount { get; init; }
    internal required IcomRadioTransport Transports { get; init; }
    internal required IcomCapabilityStatus Split { get; init; }
    internal required IcomCapabilityStatus CrossBandSplit { get; init; }
    internal required IcomCapabilityStatus FullDuplex { get; init; }
    internal required IcomCapabilityStatus Scope { get; init; }
    internal required IcomCapabilityStatus UsbIfOutput { get; init; }
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
    internal static readonly IcomRadioProfile Ic9700 = new()
    {
      Model = "IC-9700",
      DefaultCivAddress = 0xA2,
      ReceiverCount = 2,
      Transports = IcomRadioTransport.Usb | IcomRadioTransport.Ethernet,
      Split = IcomCapabilityStatus.Verified,
      CrossBandSplit = IcomCapabilityStatus.Verified,
      FullDuplex = IcomCapabilityStatus.Verified,
      Scope = IcomCapabilityStatus.Verified,
      UsbIfOutput = IcomCapabilityStatus.Candidate
    };

    internal static readonly IcomRadioProfile Ic705 = new()
    {
      Model = "IC-705",
      DefaultCivAddress = 0xA4,
      ReceiverCount = 1,
      Transports = IcomRadioTransport.Usb | IcomRadioTransport.Wlan,
      Split = IcomCapabilityStatus.Candidate,
      CrossBandSplit = IcomCapabilityStatus.Unavailable,
      FullDuplex = IcomCapabilityStatus.Unavailable,
      Scope = IcomCapabilityStatus.Candidate,
      UsbIfOutput = IcomCapabilityStatus.Candidate
    };

    internal static readonly IcomRadioProfile Ic905 = new()
    {
      Model = "IC-905",
      DefaultCivAddress = 0xAC,
      ReceiverCount = 1,
      Transports = IcomRadioTransport.Usb | IcomRadioTransport.Ethernet,
      Split = IcomCapabilityStatus.Candidate,
      CrossBandSplit = IcomCapabilityStatus.Unavailable,
      FullDuplex = IcomCapabilityStatus.Unavailable,
      Scope = IcomCapabilityStatus.Candidate,
      UsbIfOutput = IcomCapabilityStatus.Candidate
    };

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
