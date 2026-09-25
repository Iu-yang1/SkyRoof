using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class IcomLanSpectrumTests
  {
    [Fact]
    public void DirectLanPasscode_UsesIcomCredentialSubstitution()
    {
      byte[] encoded = IcomLanDirectSession.EncodePasscode("abc");

      encoded.Length.Should().Be(16);
      encoded.Take(3).Should().Equal(new byte[] { 0x38, 0x2E, 0x40 });
      encoded.Skip(3).Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void RsBa1SerialPayloadLength_Is16BitLittleEndian()
    {
      byte[] civ = new byte[497];
      civ[0] = 0xFE;
      civ[1] = 0xFE;
      civ[^1] = 0xFD;

      byte[] packet = new byte[21 + civ.Length];
      BitConverter.GetBytes(packet.Length).CopyTo(packet, 0);
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


    [Fact]
    public void DirectLanControlPackets_UseBigEndianInnerSequenceAtOffset16()
    {
      byte[] login = IcomLanDirectSession.BuildLoginPacket(
        0x11223344, 0xAABBCCDD, 0x1234, 0x5678,
        "operator", "secret", "icom-pc");

      login[0x16].Should().Be(0x12);
      login[0x17].Should().Be(0x34);
      login[0x18].Should().Be(0x00);
      login[0x19].Should().Be(0x00);
      login[0x1A].Should().Be(0x78);
      login[0x1B].Should().Be(0x56);

      byte[] authId = { 0x78, 0x56, 0x44, 0x33, 0x22, 0x11 };
      byte[] auth = IcomLanDirectSession.BuildAuthPacket(
        0x11223344, 0xAABBCCDD, 0xABCD, 0x05, authId);

      auth[0x16].Should().Be(0xAB);
      auth[0x17].Should().Be(0xCD);
      auth.Skip(0x1A).Take(6).Should().Equal(authId);
    }

    [Fact]
    public void DirectLanStreamRequest_UsesCapabilitiesAndReceiveOnlyLpcm()
    {
      byte[] authId = { 0x34, 0x12, 0x78, 0x56, 0x34, 0x12 };
      byte[] guid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
      byte[] mac = { 1, 2, 3, 4, 5, 6 };

      byte[] packet = IcomLanDirectSession.BuildStreamRequestPacket(
        0x11223344, 0xAABBCCDD, 0x0102,
        authId, "IC-9700", "operator",
        0x8010, guid, mac, 0x018B, 41002, 41003);

      packet[0x16].Should().Be(0x01);
      packet[0x17].Should().Be(0x02);
      packet.Skip(0x1A).Take(6).Should().Equal(authId);
      packet[0x27].Should().Be(0x80);
      packet[0x28].Should().Be(0x10);
      packet.Skip(0x2A).Take(6).Should().Equal(mac);
      packet[0x70].Should().Be(0x01);
      packet[0x71].Should().Be(0x00);
      packet[0x72].Should().Be(0x04);
      packet[0x73].Should().Be(0x00);
      ReadUInt32BigEndian(packet, 0x74).Should().Be(48000);
      ReadUInt32BigEndian(packet, 0x78).Should().Be(0);
      ReadUInt32BigEndian(packet, 0x7C).Should().Be(41002);
      ReadUInt32BigEndian(packet, 0x80).Should().Be(41003);
      ReadUInt32BigEndian(packet, 0x84).Should().Be(0);
      packet[0x88].Should().Be(0x01);
    }

    [Fact]
    public void DirectLanCorrelation_RejectsWrongSidSequenceAndAuthId()
    {
      const uint localSid = 0x11223344;
      const uint remoteSid = 0xAABBCCDD;
      const ushort sequence = 0x0123;
      byte[] authId = { 0x34, 0x12, 0x78, 0x56, 0x34, 0x12 };

      byte[] status = new byte[0x50];
      BitConverter.GetBytes(status.Length).CopyTo(status, 0);
      WriteUInt32BigEndian(status, 0x08, remoteSid);
      WriteUInt32BigEndian(status, 0x0C, localSid);
      status[0x14] = 0x02;
      status[0x15] = 0x03;
      status[0x16] = 0x01;
      status[0x17] = 0x23;
      authId.CopyTo(status, 0x1A);

      IcomLanDirectSession.IsMatchingStreamStatus(
        status, localSid, remoteSid, sequence, authId).Should().BeTrue();

      byte[] wrongSid = status.ToArray();
      WriteUInt32BigEndian(wrongSid, 0x0C, 0x01020304);
      IcomLanDirectSession.IsMatchingStreamStatus(
        wrongSid, localSid, remoteSid, sequence, authId).Should().BeFalse();

      byte[] wrongSequence = status.ToArray();
      wrongSequence[0x17] = 0x24;
      IcomLanDirectSession.IsMatchingStreamStatus(
        wrongSequence, localSid, remoteSid, sequence, authId).Should().BeFalse();

      byte[] wrongAuth = status.ToArray();
      wrongAuth[0x1F] ^= 0x01;
      IcomLanDirectSession.IsMatchingStreamStatus(
        wrongAuth, localSid, remoteSid, sequence, authId).Should().BeFalse();
    }

    [Fact]
    public void PassiveLanClassifier_AcceptsC1ContinuationWithoutCivPreamble()
    {
      byte[] continuation = { 0x20, 0x21, 0x22, 0xFD };
      byte[] packet = new byte[21 + continuation.Length];
      BitConverter.GetBytes(packet.Length).CopyTo(packet, 0);
      packet[16] = 0xC1;
      packet[17] = (byte)continuation.Length;
      Buffer.BlockCopy(
        continuation, 0, packet, 21, continuation.Length);

      IcomLanSpectrumCapture.LooksLikeIcomCivTransport(packet)
        .Should().BeTrue();
      IcomLanSpectrumCapture.TryGetSerialPayload(
        packet, out ReadOnlySpan<byte> serial).Should().BeTrue();
      serial.ToArray().Should().Equal(continuation);

      byte[] withTrailingGarbage =
        packet.Concat(new byte[] { 0x00 }).ToArray();
      IcomLanSpectrumCapture.TryGetSerialPayload(
        withTrailingGarbage, out _).Should().BeFalse();
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

    private static uint ReadUInt32BigEndian(
      byte[] bytes,
      int offset) =>
      ((uint)bytes[offset] << 24) |
      ((uint)bytes[offset + 1] << 16) |
      ((uint)bytes[offset + 2] << 8) |
      bytes[offset + 3];

    private static void WriteUInt32BigEndian(
      byte[] bytes,
      int offset,
      uint value)
    {
      bytes[offset] = (byte)(value >> 24);
      bytes[offset + 1] = (byte)(value >> 16);
      bytes[offset + 2] = (byte)(value >> 8);
      bytes[offset + 3] = (byte)value;
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
