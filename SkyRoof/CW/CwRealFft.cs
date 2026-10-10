using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using MathNet.Numerics.IntegralTransforms;

namespace SkyRoof.CW
{
  /// <summary>
  /// Reusable, unnormalised real-to-complex FFT for CW PCM/STFT. Prefer the
  /// FFTW3f native library already shipped with SkyRoof, not a new binary.
  /// An unavailable native backend falls back to the previous Math.NET DFT.
  ///
  /// FFTW returns N/2+1 complex bins in ordinary DC-to-Nyquist order,
  /// preserving Math.NET FourierOptions.Matlab forward scaling. Instances
  /// are NOT thread-safe; Rent() leases isolated working buffers/plans.
  /// </summary>
  internal sealed class CwRealFft : IDisposable
  {
    private const string FftwLibrary = "libfftw3f-3.dll";
    private const uint FftwEstimate = 1u << 6;
    private const uint FftwUnaligned = 1u << 1;
    private const int MaxPooledPerSize = 4;

    private static readonly object plannerSync = new();
    private static readonly ConcurrentDictionary<int, ConcurrentBag<CwRealFft>>
      plans = new();

    private readonly bool pooled;
    private readonly float[] input;
    private readonly float[] nativeOutput;
    private Complex[]? managedOutput;
    private GCHandle inputPin;
    private GCHandle outputPin;
    private IntPtr nativePlan;
    private bool nativeEnabled;
    private bool released;
    private bool availableForRent;

    [DllImport(FftwLibrary,
      EntryPoint = "fftwf_plan_dft_r2c_1d",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateNativePlan(
      int n, IntPtr input, IntPtr output, uint flags);

    [DllImport(FftwLibrary,
      EntryPoint = "fftwf_execute",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern void ExecuteNativePlan(IntPtr plan);

    [DllImport(FftwLibrary,
      EntryPoint = "fftwf_destroy_plan",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyNativePlan(IntPtr plan);

    internal int Size { get; }
    internal int PositiveBins => Size / 2 + 1;
    internal float[] Input => input;
    internal bool UsesNativeFftw => nativeEnabled;

    internal CwRealFft(int size, bool preferNative = true)
      : this(size, preferNative, pooled: false)
    {
    }

    private CwRealFft(int size, bool preferNative, bool pooled)
    {
      if (size < 2 || (size & 1) != 0 || size > 1_048_576)
        throw new ArgumentOutOfRangeException(nameof(size),
          "Real FFT length must be even and between 2 and 1048576.");

      Size = size;
      this.pooled = pooled;
      input = new float[size];
      nativeOutput = new float[2 * (size / 2 + 1)];

      if (preferNative && OperatingSystem.IsWindows())
      {
        try
        {
          inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
          outputPin = GCHandle.Alloc(nativeOutput, GCHandleType.Pinned);
          // FFTW planning and destruction are not concurrent-thread safe.
          // ESTIMATE avoids measurement-time modification of input; UNALIGNED
          // permits managed pinned float buffers on any host alignment.
          lock (plannerSync)
            nativePlan = CreateNativePlan(
              size,
              inputPin.AddrOfPinnedObject(),
              outputPin.AddrOfPinnedObject(),
              FftwEstimate | FftwUnaligned);
          nativeEnabled = nativePlan != IntPtr.Zero;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }

        if (!nativeEnabled)
        {
          if (outputPin.IsAllocated) outputPin.Free();
          if (inputPin.IsAllocated) inputPin.Free();
        }
      }

      if (!nativeEnabled)
        managedOutput = new Complex[size];
    }

    /// <summary>
    /// Pool workspaces for 1-second DeepCW windows rather than re-planning
    /// the 256/3840-point FFT and pinning buffers every decode hop.
    /// </summary>
    internal static CwRealFft Rent(int size)
    {
      var pool = plans.GetOrAdd(size,
        _ => new ConcurrentBag<CwRealFft>());
      if (pool.TryTake(out CwRealFft? fft))
      {
        fft.availableForRent = false;
        return fft;
      }
      return new CwRealFft(size, preferNative: true, pooled: true);
    }

    internal void Forward()
    {
      if (released || availableForRent)
        throw new ObjectDisposedException(nameof(CwRealFft));

      if (nativeEnabled)
      {
        ExecuteNativePlan(nativePlan);
        return;
      }

      Complex[] output = managedOutput!;
      for (int i = 0; i < Size; i++)
        output[i] = new Complex(input[i], 0);
      Fourier.Forward(output, FourierOptions.Matlab);
    }

    internal double Power(int bin)
    {
      if ((uint)bin >= (uint)PositiveBins)
        throw new ArgumentOutOfRangeException(nameof(bin));
      if (released || availableForRent)
        throw new ObjectDisposedException(nameof(CwRealFft));

      if (nativeEnabled)
      {
        double re = nativeOutput[bin * 2];
        double im = nativeOutput[bin * 2 + 1];
        return re * re + im * im;
      }
      Complex value = managedOutput![bin];
      return value.Real * value.Real +
        value.Imaginary * value.Imaginary;
    }

    internal double Magnitude(int bin) => Math.Sqrt(Power(bin));

    public void Dispose()
    {
      if (released || availableForRent) return;
      if (pooled)
      {
        ConcurrentBag<CwRealFft> pool = plans[Size];
        if (pool.Count < MaxPooledPerSize)
        {
          availableForRent = true;
          pool.Add(this);
          return;
        }
      }
      ReleaseNative();
      GC.SuppressFinalize(this);
    }

    ~CwRealFft() => ReleaseNative();

    private void ReleaseNative()
    {
      if (released) return;
      released = true;
      if (nativePlan != IntPtr.Zero)
      {
        lock (plannerSync)
          DestroyNativePlan(nativePlan);
        nativePlan = IntPtr.Zero;
      }
      if (outputPin.IsAllocated) outputPin.Free();
      if (inputPin.IsAllocated) inputPin.Free();
    }
  }
}
