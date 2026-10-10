using FluentAssertions;
using SkyRoof;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwMacroBankTests
  {
    [Fact]
    public void Prepare_TrimsUppercasesAndValidatesSelectedMacro()
    {
      var settings =
        new CwMacroSettings
        {
          F1 = "  cq de bg5jsu  "
        };

      CwMacroBank.Prepare(
          settings,
          0)
        .Should().Be(
          "CQ DE BG5JSU");
    }

    [Fact]
    public void Prepare_RejectsEmptyInvalidAndOversizeMacros()
    {
      var settings =
        new CwMacroSettings
        {
          F1 = " ",
          F2 = "CQ\nTEST",
          F3 = new string(
            'A',
            CwMessageTiming.MaxCharacters + 1)
        };

      Action empty =
        () =>
          CwMacroBank.Prepare(
            settings,
            0);
      Action invalid =
        () =>
          CwMacroBank.Prepare(
            settings,
            1);
      Action tooLong =
        () =>
          CwMacroBank.Prepare(
            settings,
            2);

      empty.Should()
        .Throw<InvalidOperationException>();
      invalid.Should()
        .Throw<ArgumentException>();
      tooLong.Should()
        .Throw<ArgumentException>();
    }

    [Fact]
    public void MacroIndicesMapExactlyToF1ThroughF8()
    {
      var settings =
        new CwMacroSettings
        {
          F1 = "1",
          F2 = "2",
          F3 = "3",
          F4 = "4",
          F5 = "5",
          F6 = "6",
          F7 = "7",
          F8 = "8"
        };

      Enumerable.Range(
          0,
          CwMacroBank.Count)
        .Select(index =>
          CwMacroBank.Get(
            settings,
            index))
        .Should()
        .Equal(
          "1", "2", "3", "4",
          "5", "6", "7", "8");

      Action below =
        () =>
          CwMacroBank.Get(
            settings,
            -1);
      Action above =
        () =>
          CwMacroBank.Get(
            settings,
            8);

      below.Should()
        .Throw<ArgumentOutOfRangeException>();
      above.Should()
        .Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Preview_IsCompactAndDoesNotTransmitOrValidate()
    {
      var settings =
        new CwMacroSettings
        {
          F4 =
            "CQ CQ CQ DE BG5JSU"
        };

      string preview =
        CwMacroBank.Preview(
          settings,
          3,
          maxCharacters: 10);

      preview.Should()
        .StartWith("F4 ");
      preview.Should()
        .EndWith("…");
      preview.Length.Should()
        .BeLessThanOrEqualTo(13);
    }
  }
}
