using System.Net;
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


    [Fact]
    public void ScopeGeometry_CenterModeDerivesFrequencyEdges()
    {
      var frame = new IcomScopeFrame
      {
        Mode = (byte)IcomScopeMode.Center,
        FrequencyAHz = 435_600_000,
        FrequencyBHz = 200_000
      };

      IcomScopeGeometry geometry = frame.Geometry;

      geometry.IsValid.Should().BeTrue();
      geometry.ModeName.Should().Be("CENTER");
      geometry.CenterFrequencyHz.Should().Be(435_600_000);
      geometry.SpanHz.Should().Be(200_000);
      geometry.LowerFrequencyHz.Should().Be(435_500_000);
      geometry.UpperFrequencyHz.Should().Be(435_700_000);
    }

    [Fact]
    public void ScopeGeometry_FixedModeDerivesCenterAndSpan()
    {
      var frame = new IcomScopeFrame
      {
        Mode = (byte)IcomScopeMode.Fixed,
        FrequencyAHz = 430_000_000,
        FrequencyBHz = 440_000_000
      };

      IcomScopeGeometry geometry = frame.Geometry;

      geometry.IsValid.Should().BeTrue();
      geometry.ModeName.Should().Be("FIXED");
      geometry.CenterFrequencyHz.Should().Be(435_000_000);
      geometry.SpanHz.Should().Be(10_000_000);
      geometry.LowerFrequencyHz.Should().Be(430_000_000);
      geometry.UpperFrequencyHz.Should().Be(440_000_000);
    }

    [Fact]
    public void ScopeState_BandSelectionIsIndependentFromCapture()
    {
      var state = new IcomScopeState();
      var main = new IcomScopeFrame
      {
        Scope = 0,
        TimestampUtc = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc)
      };
      var sub = new IcomScopeFrame
      {
        Scope = 1,
        TimestampUtc = new DateTime(2026, 10, 8, 1, 0, 1, DateTimeKind.Utc)
      };

      state.Update(main);
      state.Update(sub);

      state.SelectedBand = IcomLanScopeBand.Main;
      state.ShouldDisplay(main).Should().BeTrue();
      state.ShouldDisplay(sub).Should().BeFalse();
      state.LatestSelectedFrame.Should().BeSameAs(main);

      state.SelectedBand = IcomLanScopeBand.Sub;
      state.ShouldDisplay(main).Should().BeFalse();
      state.ShouldDisplay(sub).Should().BeTrue();
      state.LatestSelectedFrame.Should().BeSameAs(sub);

      state.SelectedBand = IcomLanScopeBand.Auto;
      state.LatestSelectedFrame.Should().BeSameAs(sub);
    }

    [Fact]
    public void ScopeState_AutoPrefersFreshMainOverInterleavedSub()
    {
      var state =
        new IcomScopeState();

      var main =
        new IcomScopeFrame
        {
          Scope = 0,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 0, 500,
              DateTimeKind.Utc)
        };

      var sub =
        new IcomScopeFrame
        {
          Scope = 1,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 0, 700,
              DateTimeKind.Utc)
        };

      state.Update(main);
      state.Update(sub);

      state.ShouldDisplay(main).Should().BeTrue();
      state.ShouldDisplay(sub).Should().BeFalse();
      state.LatestSelectedFrame.Should().BeSameAs(main);
    }

    [Fact]
    public void ScopeState_AutoFailsOverToSubAfterMainGoesStale()
    {
      var state =
        new IcomScopeState();

      var main =
        new IcomScopeFrame
        {
          Scope = 0,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 0,
              DateTimeKind.Utc)
        };

      var sub =
        new IcomScopeFrame
        {
          Scope = 1,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 1,
              DateTimeKind.Utc)
        };

      state.Update(main);
      state.Update(sub);

      state.ShouldDisplay(main).Should().BeFalse();
      state.ShouldDisplay(sub).Should().BeTrue();
      state.LatestSelectedFrame.Should().BeSameAs(sub);
    }

    [Fact]
    public void ScopeState_ReturningToAutoReevaluatesCachedFrames()
    {
      var state =
        new IcomScopeState
        {
          SelectedBand =
            IcomLanScopeBand.Sub
        };

      var main =
        new IcomScopeFrame
        {
          Scope = 0,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 0, 500,
              DateTimeKind.Utc)
        };

      var sub =
        new IcomScopeFrame
        {
          Scope = 1,
          TimestampUtc =
            new DateTime(
              2026, 10, 8,
              1, 0, 0, 700,
              DateTimeKind.Utc)
        };

      state.Update(main);
      state.Update(sub);

      state.SelectedBand =
        IcomLanScopeBand.Auto;

      state.LatestSelectedFrame.Should().BeSameAs(main);
    }


    [Fact]
    public void ScopeController_AutoPreservesLegacySourceRouting()
    {
      IcomScopeController.ResolveControlPath(
        IcomLanSpectrumSource.SkyCat,
        IcomScopeControlPath.Auto).Should().Be(
          IcomScopeControlPath.SkyCat);

      IcomScopeController.ResolveControlPath(
        IcomLanSpectrumSource.RsBa1,
        IcomScopeControlPath.Auto).Should().Be(
          IcomScopeControlPath.ReadOnly);

      IcomScopeController.ResolveControlPath(
        IcomLanSpectrumSource.DirectLan,
        IcomScopeControlPath.Auto).Should().Be(
          IcomScopeControlPath.DirectLan);
    }

    [Fact]
    public void ScopeController_CanUseSkyCatControlWithRsBa1Waveform()
    {
      int requestCount = 0;
      var controller =
        new IcomScopeController(
          () =>
          {
            requestCount++;
            return true;
          });

      bool routed =
        controller.RequestOutputIfDue(
          IcomLanSpectrumSource.RsBa1,
          IcomScopeControlPath.SkyCat,
          force: true,
          new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc));

      routed.Should().BeTrue();
      requestCount.Should().Be(1);
      controller.EffectivePath.Should().Be(
        IcomScopeControlPath.SkyCat);
    }

    [Fact]
    public void ScopeController_RateLimitsScopeReasserts()
    {
      int requestCount = 0;
      var controller =
        new IcomScopeController(
          () =>
          {
            requestCount++;
            return true;
          });

      DateTime start =
        new(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc);

      controller.RequestOutputIfDue(
        IcomLanSpectrumSource.SkyCat,
        IcomScopeControlPath.Auto,
        force: true,
        start).Should().BeTrue();

      controller.RequestOutputIfDue(
        IcomLanSpectrumSource.SkyCat,
        IcomScopeControlPath.Auto,
        force: false,
        start.AddSeconds(1)).Should().BeFalse();

      controller.RequestOutputIfDue(
        IcomLanSpectrumSource.SkyCat,
        IcomScopeControlPath.Auto,
        force: false,
        start.AddSeconds(2)).Should().BeTrue();

      requestCount.Should().Be(2);
    }


    [Fact]
    public void ScopeGeometry_MapsFractionsToAbsoluteFrequency()
    {
      var frame = new IcomScopeFrame
      {
        Mode = (byte)IcomScopeMode.Center,
        FrequencyAHz = 435_600_000,
        FrequencyBHz = 200_000
      };

      IcomScopeGeometry geometry =
        frame.Geometry;

      geometry.FrequencyAtFraction(0).Should().Be(435_500_000);
      geometry.FrequencyAtFraction(0.5).Should().Be(435_600_000);
      geometry.FrequencyAtFraction(1).Should().Be(435_700_000);
      geometry.FractionForFrequency(435_650_000).Should().BeApproximately(0.75, 1e-9);
      geometry.ContainsFrequency(435_600_000).Should().BeTrue();
      geometry.ContainsFrequency(435_800_000).Should().BeFalse();
    }


    [Fact]
    public void CorrectedTuning_TerrestrialSetsAbsoluteReceiveFrequency()
    {
      var link =
        new RadioLink
        {
          IsTerrestrial = true,
          DownlinkFrequency = 145_900_000
        };

      link.ComputeFrequencies();

      link.SetCorrectedDownlinkFrequency(
        145_925_000,
        useRit: false).Should().BeTrue();

      link.DownlinkFrequency.Should().Be(
        145_925_000);
      link.CorrectedDownlinkFrequency.Should().Be(
        145_925_000);
      link.RitEnabled.Should().BeFalse();
    }

    [Fact]
    public void CorrectedTuning_TransponderSolvesDopplerAndPreservesInvertingPair()
    {
      var link =
        BuildTransponderLink(
          invert: true);

      link.DopplerFactor =
        0.00002;
      link.SatCust!.DownlinkManualCorrection =
        120;
      link.ComputeFrequencies();

      double previousCorrected =
        link.CorrectedDownlinkFrequency;
      double previousUplink =
        link.UplinkFrequency;
      double previousOffset =
        link.TransponderOffset;
      double target =
        previousCorrected +
        25_000;

      link.SetCorrectedDownlinkFrequency(
        target,
        useRit: false).Should().BeTrue();

      link.CorrectedDownlinkFrequency
        .Should().BeApproximately(
          target,
          0.51);

      double modelDelta =
        link.TransponderOffset -
        previousOffset;

      modelDelta.Should().BeApproximately(
        25_000 /
        (1.0 - link.DopplerFactor),
        0.01);

      // In an inverting linear transponder, moving the downlink position upward
      // moves the paired nominal uplink downward by the same transponder offset.
      link.UplinkFrequency.Should()
        .BeApproximately(
          previousUplink -
          modelDelta,
          0.01);
    }

    [Fact]
    public void CorrectedTuning_RitDoesNotMoveTransponderPair()
    {
      var link =
        BuildTransponderLink(
          invert: false);

      link.DopplerFactor =
        -0.000015;
      link.ComputeFrequencies();

      double previousOffset =
        link.TransponderOffset;
      double previousUplink =
        link.UplinkFrequency;
      double target =
        link.CorrectedDownlinkFrequency -
        7_500;

      link.SetCorrectedDownlinkFrequency(
        target,
        useRit: true).Should().BeTrue();

      link.CorrectedDownlinkFrequency
        .Should().BeApproximately(
          target,
          0.01);
      link.TransponderOffset.Should().Be(
        previousOffset);
      link.UplinkFrequency.Should().Be(
        previousUplink);
      link.RitEnabled.Should().BeTrue();
    }

    [Fact]
    public void CorrectedTuning_FixedSatelliteRespectsDisabledManualCorrection()
    {
      var link =
        new RadioLink
        {
          IsTerrestrial = false,
          Tx =
            new SatnogsDbTransmitter
            {
              downlink_low =
                145_825_000
            },
          TxCust =
            new TransmitterCustomization
            {
              uuid = "fixed"
            },
          SatCust =
            new SatelliteCustomization
            {
              DownlinkDopplerCorrectionEnabled =
                true,
              DownlinkManualCorrectionEnabled =
                false
            },
          DopplerFactor =
            0.00001
        };

      link.ComputeFrequencies();
      double before =
        link.CorrectedDownlinkFrequency;

      link.SetCorrectedDownlinkFrequency(
        before + 2_000,
        useRit: false).Should().BeFalse();

      link.ComputeFrequencies();
      link.CorrectedDownlinkFrequency.Should().Be(
        before);
    }


    [Fact]
    public void ScopeView_FrequencyForXUsesCurrentSpectrumGeometry()
    {
      var frame =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_600_000,
          FrequencyBHz =
            200_000
        };

      var plot =
        new System.Drawing.Rectangle(
          10,
          20,
          101,
          100);

      IcomLanSpectrumView.FrequencyForX(
        frame.Geometry,
        plot,
        10).Should().Be(
          435_500_000);

      IcomLanSpectrumView.FrequencyForX(
        frame.Geometry,
        plot,
        60).Should().Be(
          435_600_000);

      IcomLanSpectrumView.FrequencyForX(
        frame.Geometry,
        plot,
        110).Should().Be(
          435_700_000);
    }

    [Fact]
    public void ScopeView_FrequencyForXAppliesDisplayOffset()
    {
      var frame =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            28_600_000,
          FrequencyBHz =
            200_000
        };

      var plot =
        new System.Drawing.Rectangle(
          0,
          0,
          101,
          50);

      IcomLanSpectrumView.FrequencyForX(
        frame.Geometry,
        plot,
        50,
        407_000_000).Should().Be(
          435_600_000);
    }


    [Fact]
    public void ScopeView_FrequencyForXRejectsInvalidGeometry()
    {
      IcomLanSpectrumView.FrequencyForX(
        default,
        new System.Drawing.Rectangle(
          0,
          0,
          100,
          50),
        50).Should().Be(0);
    }


    [Fact]
    public void ScopeHistoryReset_ChangesScopeOrMode()
    {
      var previous =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Center,
          FrequencyAHz = 435_600_000,
          FrequencyBHz = 100_000
        };

      var differentScope =
        new IcomScopeFrame
        {
          Scope = 1,
          Mode = (byte)IcomScopeMode.Center,
          FrequencyAHz = 145_900_000,
          FrequencyBHz = 100_000
        };

      var differentMode =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Fixed,
          FrequencyAHz = 435_500_000,
          FrequencyBHz = 435_700_000
        };

      IcomLanSpectrumView.RequiresHistoryReset(
        previous,
        differentScope).Should().BeTrue();

      IcomLanSpectrumView.RequiresHistoryReset(
        previous,
        differentMode).Should().BeTrue();
    }

    [Fact]
    public void ScopeHistoryReset_CenterTranslationKeepsHistoryWhenSpanIsStable()
    {
      var previous =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Center,
          FrequencyAHz = 435_600_000,
          FrequencyBHz = 100_000
        };

      var movedCenter =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Center,
          FrequencyAHz = 435_601_500,
          FrequencyBHz = 100_000
        };

      IcomLanSpectrumView.RequiresHistoryReset(
        previous,
        movedCenter).Should().BeFalse();
    }

    [Fact]
    public void ScopeHistoryReset_FixedEdgeMovementInvalidatesHistory()
    {
      var previous =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Fixed,
          FrequencyAHz = 435_000_000,
          FrequencyBHz = 436_000_000
        };

      var movedEdges =
        new IcomScopeFrame
        {
          Scope = 0,
          Mode = (byte)IcomScopeMode.Fixed,
          FrequencyAHz = 435_100_000,
          FrequencyBHz = 436_100_000
        };

      IcomLanSpectrumView.RequiresHistoryReset(
        previous,
        movedEdges).Should().BeTrue();
    }


    [Fact]
    public void ScopeHistoryShift_MovingCenterUpShiftsHistoryLeft()
    {
      var previous =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_600_000,
          FrequencyBHz =
            474_000
        };

      var current =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_601_000,
          FrequencyBHz =
            474_000
        };

      int shift =
        IcomLanSpectrumView
          .CalculateHistoryShiftBins(
            previous.Geometry,
            current.Geometry,
            0,
            out double residual);

      shift.Should().Be(-1);
      residual.Should()
        .BeApproximately(
          0,
          1e-12);
    }

    [Fact]
    public void ScopeHistoryShift_AccumulatesSubBinDopplerMotion()
    {
      var first =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_600_000,
          FrequencyBHz =
            474_000
        };

      var second =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_600_400,
          FrequencyBHz =
            474_000
        };

      var third =
        new IcomScopeFrame
        {
          Mode =
            (byte)IcomScopeMode.Center,
          FrequencyAHz =
            435_600_800,
          FrequencyBHz =
            474_000
        };

      IcomLanSpectrumView
        .CalculateHistoryShiftBins(
          first.Geometry,
          second.Geometry,
          0,
          out double residual1)
        .Should().Be(0);

      residual1.Should()
        .BeApproximately(
          -0.4,
          1e-12);

      IcomLanSpectrumView
        .CalculateHistoryShiftBins(
          second.Geometry,
          third.Geometry,
          residual1,
          out double residual2)
        .Should().Be(-1);

      residual2.Should()
        .BeApproximately(
          0.2,
          1e-12);
    }


    [Theory]
    [InlineData(0, 0, 100, 0)]
    [InlineData(80, 0, 100, 80)]
    [InlineData(160, 0, 100, 160)]
    [InlineData(80, 20, 100, 100)]
    [InlineData(120, 0, 200, 160)]
    [InlineData(40, 0, 200, 0)]
    public void WaterfallLevelMapping_AppliesBrightnessAndContrast(
      int level,
      int brightness,
      int contrast,
      int expected)
    {
      IcomLanSpectrumView.MapWaterfallLevel(
        level,
        brightness,
        contrast).Should().Be(expected);
    }


    [Fact]
    public void ScopeController_RoutesExplicitSkyCatControlIndependentlyFromRsBa1Data()
    {
      IcomScopeControlRequest? received = null;

      var controller =
        new IcomScopeController(
          () => true,
          request =>
          {
            received = request;
            return true;
          });

      var request =
        IcomScopeControlRequest.ForSpan(
          1,
          100_000);

      controller.RequestControl(
        IcomLanSpectrumSource.RsBa1,
        IcomScopeControlPath.SkyCat,
        request).Should().BeTrue();

      received.Should().BeSameAs(request);
      controller.EffectivePath.Should().Be(
        IcomScopeControlPath.SkyCat);
    }

    [Fact]
    public void ScopeController_AutoKeepsRsBa1WaveformReadOnly()
    {
      int requestCount = 0;

      var controller =
        new IcomScopeController(
          () => true,
          _ =>
          {
            requestCount++;
            return true;
          });

      controller.RequestControl(
        IcomLanSpectrumSource.RsBa1,
        IcomScopeControlPath.Auto,
        IcomScopeControlRequest.ForEdge(
          0,
          2)).Should().BeFalse();

      requestCount.Should().Be(0);
      controller.EffectivePath.Should().Be(
        IcomScopeControlPath.ReadOnly);
    }

    [Theory]
    [InlineData(
      (int)IcomScopeControlKind.SelectedScope,
      "U SCOPE_SELECT SUB")]
    [InlineData(
      (int)IcomScopeControlKind.Mode,
      "U SCOPE_MODE MAIN SCROLL-C")]
    [InlineData(
      (int)IcomScopeControlKind.Span,
      "U SCOPE_SPAN SUB 50000")]
    [InlineData(
      (int)IcomScopeControlKind.Edge,
      "U SCOPE_EDGE MAIN 3")]
    [InlineData(
      (int)IcomScopeControlKind.ReferenceLevel,
      "U SCOPE_REF SUB -3.5")]
    [InlineData(
      (int)IcomScopeControlKind.SweepSpeed,
      "U SCOPE_SPEED MAIN SLOW")]
    [InlineData(
      (int)IcomScopeControlKind.FixedEdge,
      "U SCOPE_FIXED_EDGE 2 1 435000000 436000000")]
    public void ScopeControlRequest_FormatsSkyCatPrivateCommand(
      int kindValue,
      string expected)
    {
      IcomScopeControlKind kind =
        (IcomScopeControlKind)kindValue;

      IcomScopeControlRequest request =
        kind switch
        {
          IcomScopeControlKind.SelectedScope =>
            IcomScopeControlRequest.ForSelectedScope(
              1),
          IcomScopeControlKind.Mode =>
            IcomScopeControlRequest.ForMode(
              0,
              IcomScopeMode.ScrollCenter),
          IcomScopeControlKind.Span =>
            IcomScopeControlRequest.ForSpan(
              1,
              50_000),
          IcomScopeControlKind.Edge =>
            IcomScopeControlRequest.ForEdge(
              0,
              3),
          IcomScopeControlKind.ReferenceLevel =>
            IcomScopeControlRequest.ForReferenceLevel(
              1,
              -3.5),
          IcomScopeControlKind.SweepSpeed =>
            IcomScopeControlRequest.ForSweepSpeed(
              0,
              IcomScopeSweepSpeed.Slow),
          _ =>
            IcomScopeControlRequest.ForFixedEdge(
              2,
              1,
              435_000_000,
              436_000_000)
        };

      CatControl.FormatIcomScopeCommand(
        request).Should().Be(
          expected);
    }


    private static RadioLink BuildTransponderLink(
      bool invert)
    {
      var link =
        new RadioLink
        {
          IsTerrestrial = false,
          Tx =
            new SatnogsDbTransmitter
            {
              downlink_low =
                435_500_000,
              downlink_high =
                435_600_000,
              uplink_low =
                145_900_000,
              uplink_high =
                146_000_000,
              invert =
                invert
            },
          TxCust =
            new TransmitterCustomization
            {
              uuid =
                "linear",
              TransponderOffset =
                20_000
            },
          SatCust =
            new SatelliteCustomization
            {
              DownlinkDopplerCorrectionEnabled =
                true,
              DownlinkManualCorrectionEnabled =
                true
            }
        };

      link.ComputeFrequencies();
      return link;
    }


    private static byte[] BuildLanUdpPacket(
      IPAddress source,
      IPAddress destination,
      int sourcePort,
      int destinationPort,
      byte[] civ)
    {
      byte[] lanPayload = new byte[21 + civ.Length];
      BitConverter.GetBytes(lanPayload.Length).CopyTo(lanPayload, 0);
      lanPayload[16] = 0xC1;
      BitConverter.GetBytes((ushort)civ.Length).CopyTo(lanPayload, 17);
      civ.CopyTo(lanPayload, 21);

      return BuildUdpPacket(
        source,
        destination,
        sourcePort,
        destinationPort,
        lanPayload);
    }

    private static byte[] BuildUdpPacket(
      IPAddress source,
      IPAddress destination,
      int sourcePort,
      int destinationPort,
      byte[] payload)
    {
      int udpLength = 8 + payload.Length;
      byte[] packet = new byte[20 + udpLength];

      packet[0] = 0x45;
      packet[9] = 17;

      source.GetAddressBytes().CopyTo(packet, 12);
      destination.GetAddressBytes().CopyTo(packet, 16);

      WriteUInt16BigEndian(packet, 20, (ushort)sourcePort);
      WriteUInt16BigEndian(packet, 22, (ushort)destinationPort);
      WriteUInt16BigEndian(packet, 24, (ushort)udpLength);

      payload.CopyTo(packet, 28);
      return packet;
    }

    private static void WriteUInt16BigEndian(
      byte[] bytes,
      int offset,
      ushort value)
    {
      bytes[offset] = (byte)(value >> 8);
      bytes[offset + 1] = (byte)value;
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
