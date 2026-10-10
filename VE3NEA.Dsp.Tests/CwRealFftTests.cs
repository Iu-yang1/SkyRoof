using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using MathNet.Numerics.IntegralTransforms;
using SkyRoof.CW;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwRealFftTests
  {
    private readonly ITestOutputHelper output;

    public CwRealFftTests(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(3840)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(11520)]
    public void R2cMatchesMathNet_UnnormalisedAmplitudeAndComplexPhase(int n)
    {
      float[] samples = Input(n);
      var reference = new Complex[n];
      for (int i = 0; i < n; i++)
        reference[i] = new Complex(samples[i], 0);
      Fourier.Forward(reference, FourierOptions.Matlab);

      using var actual = new CwRealFft(n);
      samples.AsSpan().CopyTo(actual.Input);
      actual.Forward();
      actual.PositiveBins.Should().Be(n / 2 + 1);

      double largestError = 0;
      for (int bin = 0; bin < actual.PositiveBins; bin++)
      {
        Complex expected = reference[bin];
        Complex got = actual.Bin(bin);
        double error = Complex.Abs(got - expected);
        double tolerance = Math.Max(0.015, 0.00025 * expected.Magnitude);
        error.Should().BeLessThan(tolerance,
          $"R2C must preserve complex phase and DC/Nyquist bin at N={n}, k={bin}");
        largestError = Math.Max(largestError, error);

        double expectedPower =
          expected.Real * expected.Real +
          expected.Imaginary * expected.Imaginary;
        actual.Power(bin).Should().BeApproximately(
          got.Real * got.Real + got.Imaginary * got.Imaginary,
          Math.Max(1e-8, expectedPower * 1e-9));
      }
      output.WriteLine(
        $"N={n}; backend={(actual.UsesNativeFftw ? "FFTW3f" : "MathNet")}; " +
        $"peak complex error={largestError:R}");
    }

    [Fact]
    public void ManagedFallback_UsesTheSameUnscaledPositiveSpectrum()
    {
      const int n = 3840;
      float[] samples = Input(n);
      using var fft = new CwRealFft(n, preferNative: false);
      fft.UsesNativeFftw.Should().BeFalse();
      samples.AsSpan().CopyTo(fft.Input);
      fft.Forward();

      var reference = samples.Select(s => new Complex(s, 0)).ToArray();
      Fourier.Forward(reference, FourierOptions.Matlab);
      for (int i = 0; i < n / 2 + 1; i++)
        Complex.Abs(fft.Bin(i) - reference[i])
          .Should().BeLessThan(1e-8);
    }

    [Fact]
    public void Pool_UsesSeparateWorkspacesForSimultaneousCallers()
    {
      using var a = CwRealFft.Rent(4096);
      using var b = CwRealFft.Rent(4096);
      a.Should().NotBeSameAs(b);
      a.Input.Fill(0);
      b.Input.Fill(0);
      a.Input[0] = 1f;
      b.Input[0] = 3f;
      a.Forward();
      b.Forward();
      a.Bin(1).Real.Should().BeApproximately(1, 0.001);
      b.Bin(1).Real.Should().BeApproximately(3, 0.001);
    }

    [Fact]
    public void WindowsDistribution_LoadsItsAlreadyBundledFftwBinary()
    {
      if (!OperatingSystem.IsWindows())
        return;

      string library = Path.Combine(
        AppContext.BaseDirectory, "libfftw3f-3.dll");
      File.Exists(library).Should().BeTrue(
        "the SkyRoof Windows output explicitly ships FFTW3f");
      using var fft = new CwRealFft(3840);
      fft.UsesNativeFftw.Should().BeTrue(
        "the native FFTW3f R2C entrypoints should be available");
    }

    // KFR fft-benchmark methodology: repeated warmed transforms,
    // real-vs-complex, the same transform length and host. This benchmark
    // is opt-in because shared CI runners are not stable CPU reference
    // machines; numbers never become release performance claims.
    [Theory]
    [InlineData(256)]
    [InlineData(2048)]
    [InlineData(3840)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(11520)]
    public void OptionalBenchmark_RealR2cVersusComplexFft(int n)
    {
      if (Environment.GetEnvironmentVariable(
        "SKYROOF_RUN_FFT_BENCHMARK") != "1")
        return;

      float[] samples = Input(n);
      using var real = new CwRealFft(n);
      samples.AsSpan().CopyTo(real.Input);
      var complex = new Complex[n];

      void Native()
      {
        real.Forward();
        _ = real.Power(n / 4);
      }
      void Managed()
      {
        for (int i = 0; i < n; i++)
          complex[i] = new Complex(samples[i], 0);
        Fourier.Forward(complex, FourierOptions.Matlab);
        _ = complex[n / 4].Magnitude;
      }

      for (int i = 0; i < 20; i++)
      {
        Native();
        Managed();
      }

      double MedianUs(Action task)
      {
        var trials = new List<double>();
        int iterations = n < 2048 ? 200 : 60;
        for (int t = 0; t < 9; t++)
        {
          long start = Stopwatch.GetTimestamp();
          for (int i = 0; i < iterations; i++)
            task();
          trials.Add(1000000.0 *
            Stopwatch.GetElapsedTime(start).TotalSeconds / iterations);
        }
        trials.Sort();
        return trials[trials.Count / 2];
      }

      double nativeUs = MedianUs(Native);
      double managedUs = MedianUs(Managed);
      output.WriteLine(
        $"size={n}; backend={(real.UsesNativeFftw ? "FFTW3f-R2C" : "MathNet-R2C-fallback")}; " +
        $"real_median_us={nativeUs:F3}; complex_median_us={managedUs:F3}; " +
        $"speedup={managedUs / nativeUs:F2}x");
    }

    private static float[] Input(int n)
    {
      var random = new Random(123456 + n);
      var samples = new float[n];
      for (int i = 0; i < n; i++)
      {
        double t = i / 48000.0;
        samples[i] = (float)(
          0.7 * Math.Sin(2 * Math.PI * 847 * t + 0.3) +
          0.2 * Math.Cos(2 * Math.PI * 1582 * t - 0.2) +
          0.08 * (random.NextDouble() * 2 - 1));
      }
      return samples;
    }
  }
}
