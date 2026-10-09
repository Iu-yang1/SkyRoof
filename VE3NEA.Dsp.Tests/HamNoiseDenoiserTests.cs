using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class HamNoiseDenoiserTests
  {
    [Theory]
    [InlineData(CwDenoiseMode.HamNoiseClassic)]
    [InlineData(CwDenoiseMode.HamNoiseV2)]
    public void NativeBackend_PreservesLengthAndProducesFiniteSignal(
      CwDenoiseMode mode)
    {
      // Ordinary developer builds are allowed to omit the optional native
      // backend. Compile Check / benchmark workflows build it before tests,
      // so this test executes there.
      if (!HamNoiseLaneDenoiser.IsAvailable())
        return;

      const int sampleRate = 9600;
      float[] input =
        new float[sampleRate * 2];
      var random = new Random(5001);

      for (int i = 0; i < input.Length; i++)
      {
        double t =
          i / (double)sampleRate;
        bool keyDown =
          (t % 0.12) < 0.06;
        double tone =
          keyDown
            ? 0.28 *
              Math.Sin(
                2 * Math.PI *
                800 * t)
            : 0;
        double noise =
          0.02 *
          (2 * random.NextDouble() - 1);
        input[i] =
          (float)(tone + noise);
      }

      var denoiser =
        new HamNoiseLaneDenoiser(mode);

      float[] output =
        denoiser.Process(
          input,
          sampleRate,
          wet: 1.0);

      output.Should()
        .HaveCount(input.Length);
      output.Should()
        .OnlyContain(
          value => float.IsFinite(value));
      Rms(output).Should()
        .BeGreaterThan(1e-5);
    }

    private static double Rms(
      IReadOnlyList<float> values)
    {
      double sum = 0;
      for (int i = 0;
           i < values.Count;
           i++)
        sum +=
          values[i] * values[i];

      return Math.Sqrt(
        sum /
        Math.Max(1, values.Count));
    }
  }
}
