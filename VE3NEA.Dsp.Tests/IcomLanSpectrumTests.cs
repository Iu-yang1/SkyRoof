using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class IcomLanSpectrumTests
  {
    [Fact]
    public void RsBa1SerialPayloadLength_Is16BitLittleEndian()
    {
      byte[] civ = new byte[497];
      civ[0] = 0xFE;
      civ[1] = 0xFE;
      civ[^1] = 0xFD;

      byte[] packet = new byte[21 + civ.Length];
      packet[16] = 0xC1;
      packet[17] = (byte)(civ.Length & 0xFF);
      packet[18] = (byte)(civ.Length >> 8);
      Buffer.BlockCopy(civ, 0, packet, 21, civ.Length);

      IcomLanSpectrumCapture.TryGetSerialPayload(packet, out ReadOnlySpan<byte> serial)
        .Should().BeTrue();

      serial.Length.Should().Be(497);
      serial[0].Should().Be(0xFE);
      serial[1].Should().Be(0xFE);
      serial[^1].Should().Be(0xFD);
    }

    [Fact]
    public void ScopeAssembler_DecodesSingleFrameSweep()
    {
      byte[] samples = Enumerable.Range(0, IcomScopeAssembler.ScopePointCount)
        .Select(i => (byte)(i % 161))
        .ToArray();

      byte[] frame = BuildScopeHeaderFrame(
        receiver: 0,
        sequence: 1,
        sequenceMaximum: 1,
        mode: 0,
        frequencyAHz: 437_800_000,
        frequencyBHz: 500_000,
        outOfRange: false,
        samples);

      var assembler = new IcomScopeAssembler();

      assembler.TryFeed(frame, out IcomScopeFrame? result).Should().BeTrue();
      result.Should().NotBeNull();
      result!.SweepComplete.Should().BeTrue();
      result.Scope.Should().Be(0);
      result.Mode.Should().Be(0);
      result.FrequencyAHz.Should().Be(437_800_000);
      result.FrequencyBHz.Should().Be(500_000);
      result.Samples.Should().Equal(samples);
    }

    [Fact]
    public void ScopeAssembler_ReassemblesElevenFrameSerialSweep()
    {
      byte[] samples = Enumerable.Range(0, IcomScopeAssembler.ScopePointCount)
        .Select(i => (byte)((i * 7) % 161))
        .ToArray();

      var assembler = new IcomScopeAssembler();

      byte[] header = BuildScopeHeaderFrame(
        receiver: 1,
        sequence: 1,
        sequenceMaximum: 11,
        mode: 0,
        frequencyAHz: 145_987_500,
        frequencyBHz: 100_000,
        outOfRange: false,
        Array.Empty<byte>());

      assembler.TryFeed(header, out IcomScopeFrame? result).Should().BeTrue();
      result.Should().BeNull();

      int offset = 0;

      for (int sequence = 2; sequence <= 10; sequence++)
      {
        byte[] chunk = samples.Skip(offset).Take(50).ToArray();
        offset += chunk.Length;

        assembler.TryFeed(
          BuildScopeChunk(receiver: 1, sequence, sequenceMaximum: 11, chunk),
          out result).Should().BeTrue();

        result.Should().NotBeNull();
        result!.SweepComplete.Should().BeFalse();
        result.DivisionCurrent.Should().Be((byte)sequence);
        result.Samples.Take(offset).Should().Equal(samples.Take(offset));
      }

      byte[] finalChunk = samples.Skip(offset).ToArray();
      finalChunk.Length.Should().Be(25);

      assembler.TryFeed(
        BuildScopeChunk(receiver: 1, sequence: 11, sequenceMaximum: 11, finalChunk),
        out result).Should().BeTrue();

      result.Should().NotBeNull();
      result!.SweepComplete.Should().BeTrue();
      result.Scope.Should().Be(1);
      result.Mode.Should().Be(0);
      result.FrequencyAHz.Should().Be(145_987_500);
      result.FrequencyBHz.Should().Be(100_000);
      result.Samples.Should().Equal(samples);
    }

    [Fact]
    public void ScopeAssembler_DecodesBcdSequenceTenAndEleven()
    {
      var assembler = new IcomScopeAssembler();

      byte[] samples = Enumerable.Range(0, IcomScopeAssembler.ScopePointCount)
        .Select(i => (byte)(i % 161))
        .ToArray();

      assembler.TryFeed(
        BuildScopeHeaderFrame(0, 1, 11, 1, 430_000_000, 440_000_000, false, Array.Empty<byte>()),
        out _).Should().BeTrue();

      int offset = 0;
      IcomScopeFrame? result = null;

      for (int sequence = 2; sequence <= 11; sequence++)
      {
        int count = sequence < 11 ? 50 : 25;
        byte[] chunk = samples.Skip(offset).Take(count).ToArray();
        offset += chunk.Length;

        assembler.TryFeed(
          BuildScopeChunk(0, sequence, 11, chunk),
          out result).Should().BeTrue();
      }

      result.Should().NotBeNull();
      result!.SweepComplete.Should().BeTrue();
      result.Samples.Should().Equal(samples);
    }

    private static byte[] BuildScopeHeaderFrame(
      byte receiver,
      int sequence,
      int sequenceMaximum,
      byte mode,
      long frequencyAHz,
      long frequencyBHz,
      bool outOfRange,
      byte[] samples)
    {
      var frame = new List<byte>
      {
        0xFE, 0xFE, 0xE0, 0xA2, 0x27, 0x00,
        receiver,
        EncodeBcdByte(sequence),
        EncodeBcdByte(sequenceMaximum),
        mode
      };

      frame.AddRange(EncodeFrequency(frequencyAHz));
      frame.AddRange(EncodeFrequency(frequencyBHz));
      frame.Add(outOfRange ? (byte)1 : (byte)0);
      frame.AddRange(samples);
      frame.Add(0xFD);
      return frame.ToArray();
    }

    private static byte[] BuildScopeChunk(
      byte receiver,
      int sequence,
      int sequenceMaximum,
      byte[] samples)
    {
      var frame = new List<byte>
      {
        0xFE, 0xFE, 0xE0, 0xA2, 0x27, 0x00,
        receiver,
        EncodeBcdByte(sequence),
        EncodeBcdByte(sequenceMaximum)
      };

      frame.AddRange(samples);
      frame.Add(0xFD);
      return frame.ToArray();
    }

    private static byte EncodeBcdByte(int value) =>
      (byte)(((value / 10) << 4) | (value % 10));

    private static byte[] EncodeFrequency(long hz)
    {
      byte[] result = new byte[5];

      for (int i = 0; i < result.Length; i++)
      {
        int pair = (int)(hz % 100);
        hz /= 100;
        result[i] = (byte)(((pair / 10) << 4) | (pair % 10));
      }

      return result;
    }
  }
}
