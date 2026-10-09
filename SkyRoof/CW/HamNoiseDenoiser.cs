using System.Runtime.InteropServices;

namespace SkyRoof.CW
{
  public enum CwDenoiseMode
  {
    Bypass,
    HamNoiseClassic,
    HamNoiseV2
  }

  public interface ICwLaneDenoiser
  {
    int SampleRate { get; }
    string Name { get; }

    float[] Process(
      ReadOnlySpan<float> input,
      int sampleRate,
      double wet = 1.0);
  }

  internal static class HamNoiseNative
  {
    internal const string LibraryName =
      "hamnoise_skyroof.dll";
    internal const int ExpectedAbiVersion = 1;

    [DllImport(
      LibraryName,
      CallingConvention = CallingConvention.Cdecl)]
    internal static extern int
      hamnoise_skyroof_abi_version();

    [DllImport(
      LibraryName,
      CallingConvention = CallingConvention.Cdecl)]
    internal static extern int
      hamnoise_skyroof_sample_rate();

    [DllImport(
      LibraryName,
      CallingConvention = CallingConvention.Cdecl)]
    internal static extern int
      hamnoise_skyroof_process_classic(
        float[] input,
        int sampleCount,
        float[] output);

    [DllImport(
      LibraryName,
      CallingConvention = CallingConvention.Cdecl)]
    internal static extern int
      hamnoise_skyroof_process_v2(
        float[] input,
        int sampleCount,
        float[] output);

    internal static bool IsAvailable()
    {
      if (!NativeLibrary.TryLoad(
            LibraryName,
            out nint handle))
        return false;

      NativeLibrary.Free(handle);

      try
      {
        return hamnoise_skyroof_abi_version() ==
                 ExpectedAbiVersion &&
               hamnoise_skyroof_sample_rate() ==
                 9600;
      }
      catch (
        Exception ex) when (
          ex is DllNotFoundException or
          EntryPointNotFoundException or
          BadImageFormatException)
      {
        return false;
      }
    }
  }

  /// <summary>
  /// Optional per-lane HamNoise backend. SkyRoof always performs carrier
  /// detection/tracking on the untouched 48 kHz receive stream. This backend
  /// only sees one DDC-isolated lane after it has been translated to the
  /// DeepCW center frequency and resampled to HamNoise's 9.6 kHz rate.
  /// </summary>
  public sealed class HamNoiseLaneDenoiser :
    ICwLaneDenoiser
  {
    public CwDenoiseMode Mode { get; }
    public int SampleRate { get; }
    public string Name =>
      Mode == CwDenoiseMode.HamNoiseV2
        ? "HamNoise CW V2"
        : "HamNoise CW Classic";

    public HamNoiseLaneDenoiser(
      CwDenoiseMode mode)
    {
      if (mode is not (
            CwDenoiseMode.HamNoiseClassic or
            CwDenoiseMode.HamNoiseV2))
        throw new ArgumentOutOfRangeException(
          nameof(mode));

      Mode = mode;

      if (!HamNoiseNative.IsAvailable())
        throw new InvalidOperationException(
          "HamNoise native backend is not available. " +
          "Build or install hamnoise_skyroof.dll.");

      int abi =
        HamNoiseNative
          .hamnoise_skyroof_abi_version();
      if (abi !=
          HamNoiseNative.ExpectedAbiVersion)
        throw new InvalidOperationException(
          $"HamNoise ABI mismatch: expected " +
          $"{HamNoiseNative.ExpectedAbiVersion}, " +
          $"got {abi}.");

      SampleRate =
        HamNoiseNative
          .hamnoise_skyroof_sample_rate();
      if (SampleRate != 9600)
        throw new InvalidOperationException(
          $"Unsupported HamNoise sample rate " +
          $"{SampleRate} Hz.");
    }

    public static bool IsAvailable() =>
      HamNoiseNative.IsAvailable();

    public float[] Process(
      ReadOnlySpan<float> input,
      int sampleRate,
      double wet = 1.0)
    {
      if (sampleRate != SampleRate)
        throw new ArgumentException(
          $"HamNoise requires {SampleRate} Hz PCM.",
          nameof(sampleRate));
      if (!double.IsFinite(wet) ||
          wet < 0 ||
          wet > 1)
        throw new ArgumentOutOfRangeException(
          nameof(wet));
      if (input.Length == 0)
        return [];

      float[] dry =
        input.ToArray();
      if (wet == 0)
        return dry;

      float[] enhanced =
        new float[dry.Length];

      int status =
        Mode ==
          CwDenoiseMode.HamNoiseV2
          ? HamNoiseNative
              .hamnoise_skyroof_process_v2(
                dry,
                dry.Length,
                enhanced)
          : HamNoiseNative
              .hamnoise_skyroof_process_classic(
                dry,
                dry.Length,
                enhanced);

      if (status != 0)
        throw new InvalidOperationException(
          $"{Name} failed with status {status}.");

      if (wet >= 1)
        return enhanced;

      float blend =
        (float)wet;
      for (int i = 0;
           i < enhanced.Length;
           i++)
      {
        enhanced[i] =
          dry[i] +
          (enhanced[i] - dry[i]) *
          blend;
      }

      return enhanced;
    }
  }
}
