using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IcomRadioCompatibilityTests
  {
    [Theory]
    [InlineData("IC-9700", 0xA2, 2)]
    [InlineData("IC-705", 0xA4, 1)]
    [InlineData("IC-905", 0xAC, 1)]
    public void KnownModelsHaveDistinctDefaultAddresses(
      string model, byte address, int receivers)
    {
      Assert.True(IcomRadioProfiles.TryGetByModel(model,
        out IcomRadioProfile? profile));
      Assert.NotNull(profile);
      Assert.Equal(address, profile.DefaultCivAddress);
      Assert.Equal(receivers, profile.ReceiverCount);
      Assert.Equal(receivers > 1, profile.MayUseMainSubScope);
      Assert.True(IcomRadioProfiles.TryGetCandidateByDefaultAddress(
        address, out IcomRadioProfile? candidate));
      Assert.Same(profile, candidate);
    }

    [Fact]
    public void LegacyDirectLanScopeOnlyAcceptsNamedIc9700()
    {
      Assert.True(IcomRadioProfiles.SupportsExistingDirectLanScope("IC-9700"));
      Assert.True(IcomRadioProfiles.SupportsExistingDirectLanScope(" ic-9700 "));
      Assert.False(IcomRadioProfiles.SupportsExistingDirectLanScope("IC-705"));
      Assert.False(IcomRadioProfiles.SupportsExistingDirectLanScope("IC-905"));
      Assert.False(IcomRadioProfiles.SupportsExistingDirectLanScope(null));
      Assert.False(IcomRadioProfiles.SupportsExistingDirectLanScope(""));
    }

    [Fact]
    public void UnknownModelOrAddressIsNotAssumedToBeAnIcom9700()
    {
      Assert.False(IcomRadioProfiles.TryGetByModel(
        "Unknown radio", out IcomRadioProfile? byName));
      Assert.Null(byName);
      Assert.False(IcomRadioProfiles.TryGetCandidateByDefaultAddress(
        0x42, out IcomRadioProfile? byAddress));
      Assert.Null(byAddress);
    }

    [Fact]
    public void UnverifiedNewModelFeaturesRemainDisabled()
    {
      foreach (IcomRadioProfile radio in new[] {
        IcomRadioProfiles.Ic705, IcomRadioProfiles.Ic905 })
      {
        Assert.False(radio.MayUseMainSubScope);
        Assert.False(radio.CanUseVerifiedFullDuplex);
        Assert.NotEqual(IcomCapabilityStatus.Verified, radio.Split);
        Assert.Equal(IcomCapabilityStatus.Unavailable, radio.CrossBandSplit);
      }
    }

    [Theory]
    [InlineData(437_800_000L, 5)]
    [InlineData(145_987_500L, 5)]
    [InlineData(5_760_000_000L, 5)]
    [InlineData(10_368_100_000L, 6)]
    [InlineData(10_500_000_000L, 6)]
    public void CodecRoundTripsIntegralHertz(
      long frequencyHz, int byteCount)
    {
      Assert.True(IcomCivFrequencyCodec.TryEncode(
        frequencyHz, byteCount, out byte[] encoded));
      Assert.Equal(byteCount, encoded.Length);
      Assert.True(IcomCivFrequencyCodec.TryDecode(encoded,
        out long decodedHz));
      Assert.Equal(frequencyHz, decodedHz);
    }

    [Fact]
    public void GoldenFiveByteIc9700FrequencyIsUnchanged()
    {
      byte[] expected = { 0x00, 0x00, 0x80, 0x37, 0x04 };
      Assert.True(IcomCivFrequencyCodec.TryEncode(
        437_800_000L, 5, out byte[] encoded));
      Assert.Equal(expected, encoded);
      Assert.True(IcomCivFrequencyCodec.TryDecode(expected, out long actual));
      Assert.Equal(437_800_000L, actual);
    }

    [Fact]
    public void Ic905TenGigahertzUsesSixBytesAndPreservesHighDigits()
    {
      byte[] expected = { 0x00, 0x00, 0x10, 0x68, 0x03, 0x01 };
      Assert.Equal(6, IcomRadioProfiles.Ic905.GetFrequencyByteCount(
        10_368_100_000L));
      Assert.Equal(5, IcomRadioProfiles.Ic905.GetFrequencyByteCount(
        5_760_000_000L));
      Assert.True(IcomCivFrequencyCodec.TryEncode(
        10_368_100_000L, 6, out byte[] encoded));
      Assert.Equal(expected, encoded);
      Assert.True(IcomCivFrequencyCodec.TryDecode(expected, out long actual));
      Assert.Equal(10_368_100_000L, actual);
      Assert.False(IcomCivFrequencyCodec.TryEncode(
        10_368_100_000L, 5, out _));
    }

    [Fact]
    public void CodecRejectsIllegalBcdLengthsAndOverflow()
    {
      Assert.False(IcomCivFrequencyCodec.TryDecode(
        new byte[] { 0x00, 0x01, 0x23, 0x4A, 0x05 }, out _));
      Assert.False(IcomCivFrequencyCodec.TryDecode(
        new byte[] { 0x00, 0x01, 0x23, 0xF4, 0x05, 0x00 }, out _));
      Assert.False(IcomCivFrequencyCodec.TryDecode(
        new byte[] { 0x00, 0x01, 0x23, 0x45 }, out _));
      Assert.False(IcomCivFrequencyCodec.TryEncode(-1, 5, out _));
      Assert.False(IcomCivFrequencyCodec.TryEncode(0, 4, out _));
      Assert.False(IcomCivFrequencyCodec.TryEncode(
        100_000_000_000L, 5, out _));
      Assert.True(IcomCivFrequencyCodec.TryEncode(
        99_999_999_999L, 6, out _));
    }

    [Fact]
    public void PassiveProbeOnlyAcceptsWellFormedRadioResponses()
    {
      // Received frequency, not a presumed identification command.
      byte[] reply = {
        0xFE, 0xFE, 0xE0, 0xAC, 0x03,
        0x00, 0x00, 0x10, 0x68, 0x03, 0x01, 0xFD
      };
      Assert.True(IcomCivReadOnlyProbe.TryReadResponderAddress(
        reply, 0xE0, out byte responder));
      Assert.Equal((byte)0xAC, responder);

      Assert.False(IcomCivReadOnlyProbe.TryReadResponderAddress(
        new byte[] { 0xFE, 0xFE, 0xE0, 0xAC, 0xFA, 0xFD },
        0xE0, out _));
      Assert.False(IcomCivReadOnlyProbe.TryReadResponderAddress(
        new byte[] { 0xFE, 0xFE, 0xE0, 0xAC, 0xFB, 0xFD },
        0xE0, out _));
      Assert.False(IcomCivReadOnlyProbe.TryReadResponderAddress(
        new byte[] { 0xFE, 0xFE, 0xE0, 0xAC, 0x03, 0xFD },
        0xE1, out _));
      Assert.False(IcomCivReadOnlyProbe.TryReadResponderAddress(
        new byte[] { 0xFE, 0xFE, 0xE0, 0xAC, 0x03, 0x00 },
        0xE0, out _));
    }
  }
}
