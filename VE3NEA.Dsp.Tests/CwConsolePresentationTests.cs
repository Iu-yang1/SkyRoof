using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwConsolePresentationTests
  {
    private static readonly DateTime T0 =
      new(
        2026, 10, 9,
        0, 0, 0,
        DateTimeKind.Utc);

    [Fact]
    public void AssociationHint_IsPreferredOverTransientTrackId()
    {
      CwSignalTrack track =
        Track(
          id: 17,
          hintId: 404);

      CwConsoleLaneIdentity identity =
        CwConsolePresentation.Identity(
          track);

      identity.UsesAssociationHint
        .Should().BeTrue();
      identity.Id.Should().Be(404);
      identity.ToString().Should().Be("H404");
    }

    [Fact]
    public void TrackId_IsFallbackWhenNoAssociationHintExists()
    {
      CwSignalTrack track =
        Track(
          id: 17,
          hintId: 0);

      CwConsoleLaneIdentity identity =
        CwConsolePresentation.Identity(
          track);

      identity.UsesAssociationHint
        .Should().BeFalse();
      identity.Id.Should().Be(17);
      identity.ToString().Should().Be("T17");
    }

    [Fact]
    public void RebuiltTrackAndTranscriptShareStableHintIdentity()
    {
      CwSignalTrack rebuilt =
        Track(
          id: 99,
          hintId: 808);

      var transcript =
        new CwTranscriptSnapshot(
          trackId: 12,
          associationHintId: 808,
          committedText: "CQ DE ",
          provisionalText: "K1ABC",
          provisionalSymbols:
            Array.Empty<CwTranscriptSymbol>(),
          lastWindowEndUtc:
            T0.AddSeconds(8));

      CwConsolePresentation.Identity(
          rebuilt)
        .Should().Be(
          CwConsolePresentation.Identity(
            transcript));
    }

    [Fact]
    public void DuplicateAssociationHint_CollapsesToOneSemanticUiLane()
    {
      CwSignalTrack weaker =
        Track(
          id: 17,
          hintId: 404) with
        {
          Ambiguous = true,
          SnrDb = 8,
          IdentityConfidence = 0.70
        };
      CwSignalTrack stronger =
        Track(
          id: 18,
          hintId: 404) with
        {
          Ambiguous = true,
          SnrDb = 14,
          IdentityConfidence = 0.85
        };

      CwSignalTrack[] visible =
        CwConsolePresentation
          .CollapseDuplicateLaneIdentities(
            new[] { weaker, stronger });

      visible.Should().ContainSingle();
      visible[0].Id.Should().Be(18);
      CwConsolePresentation.Identity(
          visible[0])
        .ToString().Should().Be("H404");
    }

    [Fact]
    public void StableSlots_DoNotReorderWhenFrequencyOrderChanges()
    {
      var slots =
        new CwConsoleLaneSlotMap(
          maxSlots: 4,
          releaseDelay:
            TimeSpan.FromSeconds(4));

      CwSignalTrack a =
        Track(1, 101) with
        {
          FrequencyHz = 700
        };
      CwSignalTrack b =
        Track(2, 202) with
        {
          FrequencyHz = 900
        };

      IReadOnlyList<CwConsoleLaneSlot> first =
        slots.Update(
          new[] { a, b },
          T0);
      int aSlot =
        first.Single(x =>
          x.Identity.HasValue && x.Identity.Value.Id == 101).Index;
      int bSlot =
        first.Single(x =>
          x.Identity.HasValue && x.Identity.Value.Id == 202).Index;

      IReadOnlyList<CwConsoleLaneSlot> crossed =
        slots.Update(
          new[]
          {
            a with { FrequencyHz = 980 },
            b with { FrequencyHz = 620 }
          },
          T0.AddSeconds(1));

      crossed.Single(x =>
          x.Identity.HasValue && x.Identity.Value.Id == 101)
        .Index.Should().Be(aSlot);
      crossed.Single(x =>
          x.Identity.HasValue && x.Identity.Value.Id == 202)
        .Index.Should().Be(bSlot);
    }

    [Fact]
    public void StableSlots_HoldGraceBeforeReleasingRow()
    {
      var slots =
        new CwConsoleLaneSlotMap(
          maxSlots: 2,
          releaseDelay:
            TimeSpan.FromSeconds(4));
      CwSignalTrack lane =
        Track(1, 101);

      slots.Update(
        new[] { lane },
        T0);

      CwConsoleLaneSlot grace =
        slots.Update(
            Array.Empty<CwSignalTrack>(),
            T0.AddSeconds(2))
          .Single(x =>
            x.Identity.HasValue && x.Identity.Value.Id == 101);
      grace.Present.Should().BeFalse();

      IReadOnlyList<CwConsoleLaneSlot> released =
        slots.Update(
          Array.Empty<CwSignalTrack>(),
          T0.AddSeconds(5));
      released.Should().NotContain(x =>
        x.Identity.HasValue && x.Identity.Value.Id == 101);
    }

    [Fact]
    public void LaneLabels_PreferStableAssociationHintButKeepTrackDiagnostic()
    {
      CwSignalTrack hinted =
        Track(
          id: 17,
          hintId: 404);
      CwSignalTrack fallback =
        Track(
          id: 23,
          hintId: 0);

      CwConsolePresentation.LaneLabel(
          hinted)
        .Should().Be("H404");
      CwConsolePresentation.LaneDiagnosticLabel(
          hinted)
        .Should().Be("H404 · #17");

      CwConsolePresentation.LaneLabel(
          fallback)
        .Should().Be("T23");
      CwConsolePresentation.LaneDiagnosticLabel(
          fallback)
        .Should().Be("T23");
    }

    [Theory]
    [InlineData(
      false, true, "Active")]
    [InlineData(
      false, false, "Hold")]
    [InlineData(
      true, true, "Ambiguous")]
    [InlineData(
      true, false, "Ambiguous")]
    public void StateText_UsesAmbiguityBeforeActivity(
      bool ambiguous,
      bool active,
      string expected)
    {
      CwSignalTrack track =
        Track(
          id: 1,
          hintId: 1) with
        {
          Ambiguous = ambiguous,
          Active = active
        };

      CwConsolePresentation.StateText(
          track)
        .Should().Be(expected);
    }

    [Theory]
    [InlineData(240, 34)]
    [InlineData(320, 40)]
    [InlineData(400, 50)]
    [InlineData(800, 52)]
    public void EightLaneOverview_FitsReadableCompactRows(
      int viewportHeight, int expected)
    {
      CwPileupLaneList.CalculateRowHeight(viewportHeight)
        .Should().Be(expected);
      CwPileupLaneList.LaneCount.Should().Be(8);
    }

    [Fact]
    public void GridTranscript_MarksOnlyProvisionalSuffix()
    {
      var transcript =
        new CwTranscriptSnapshot(
          trackId: 1,
          associationHintId: 101,
          committedText: "CQ DE ",
          provisionalText: "K1ABC",
          provisionalSymbols:
            Array.Empty<CwTranscriptSymbol>(),
          lastWindowEndUtc: T0);

      CwConsolePresentation.GridTranscript(
          transcript)
        .Should().Be(
          "CQ DE ⟦K1ABC⟧");
    }

    [Fact]
    public void GridTranscript_DoesNotDecorateFullyCommittedText()
    {
      var transcript =
        new CwTranscriptSnapshot(
          trackId: 1,
          associationHintId: 101,
          committedText: "CQ DE K1ABC",
          provisionalText: "",
          provisionalSymbols:
            Array.Empty<CwTranscriptSymbol>(),
          lastWindowEndUtc: T0);

      CwConsolePresentation.GridTranscript(
          transcript)
        .Should().Be(
          "CQ DE K1ABC");
    }

    private static CwSignalTrack Track(
      int id,
      int hintId) =>
      new(
        Id: id,
        FrequencyHz: 800,
        SnrDb: 12,
        DriftHzPerSecond: 1.2,
        FirstSeenUtc: T0,
        LastSeenUtc:
          T0.AddSeconds(4),
        Confirmed: true,
        Active: true,
        Ambiguous: false,
        FrequencySigmaHz: 2,
        MergeGroupId: 0,
        IdentityConfidence: 0.95,
        AssociationHintId: hintId);
  }
}
