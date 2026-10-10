using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using MathNet.Numerics.IntegralTransforms;

namespace SkyRoof.CW
{
  /// <summary>
  /// Unnormalised, forward real-to-complex FFT for CW windows.
  /// Uses the existing SkyRoof FFTW3f binary and its SIMD-aligned malloc,
  /// with the prior Math.NET Matlab-scaled complex FFT as fallback.
  /// Only the non-redundant DC..Nyquist N/2+1 bins are exposed.
  ///
  /// One plan owns one input/output workspace, so concurrent callers must
  /// use different instances. Rent/Dispose pools plans across DeepCW hops.
  /// </summary>
  internal sealed unsafe class CwRealFft : IDisposable
  {
    private const string FftwLibrary = "libfftw3f-3.dll";
    private const uint FftwEstimate = 1u << 6;
    private const int MaxPooledPerSize = 4;

    private static readonly object plannerSync = new();
    private static readonly ConcurrentDictionary<int, ConcurrentBag<CwRealFft>>
      pools = new();

    private readonly bool pooled;
    private readonly float[] managedInput;
    private Complex[]? managedOutput;
    private IntPtr nativeInput;
    private IntPtr nativeOutput;
    private IntPtr nativePlan;
    private bool nativeEnabled;
    private bool released;
    private bool availableForRent;

    [DllImport(FftwLibrary, EntryPoint = "fftwf_malloc",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeMalloc(nuint bytes);

    [DllImport(FftwLibrary, EntryPoint = "fftwf_free",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFree(IntPtr ptr);

    [DllImport(FftwLibrary, EntryPoint = "fftwf_plan_dft_r2c_1d",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateNativePlan(
      int n, IntPtr input, IntPtr output, uint flags);

    [DllImport(FftwLibrary, EntryPoint = "fftwf_execute",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern void ExecuteNativePlan(IntPtr plan);

    [DllImport(FftwLibrary, EntryPoint = "fftwf_destroy_plan",
      CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyNativePlan(IntPtr plan);

    internal int Size { get; }
    internal int PositiveBins => Size / 2 + 1;
    internal bool UsesNativeFftw => nativeEnabled;

    internal Span<float> Input
    {
      get
      {
        ThrowIfUnavailable();
        return nativeEnabled
          ? new Span<float>((void*)nativeInput, Size)
          : managedInput;
      }
    }

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
      managedInput = new float[size];

      if (preferNative && OperatingSystem.IsWindows())
      {
        try
        {
          nativeInput = NativeMalloc((nuint)(sizeof(float) * size));
          nativeOutput = NativeMalloc(
            (nuint)(sizeof(float) * 2 * PositiveBins));
          if (nativeInput != IntPtr.Zero &&
              nativeOutput != IntPtr.Zero)
          {
            // FFTW's own allocator provides SIMD-safe alignment. Do not
            // pass FFTW_UNALIGNED, which would restrict SIMD codelets.
            // Planning/destruction are serialized; executing independent
            // plans may run concurrently.
            lock (plannerSync)
              nativePlan = CreateNativePlan(
                size, nativeInput, nativeOutput, FftwEstimate);
            nativeEnabled = nativePlan != IntPtr.Zero;
          }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }

        if (!nativeEnabled)
          FreeNativeResources();
      }

      if (!nativeEnabled)
        managedOutput = new Complex[size];
    }

    internal static CwRealFft Rent(int size)
    {
      var pool = pools.GetOrAdd(size,
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
      ThrowIfUnavailable();
      if (nativeEnabled)
      {
        ExecuteNativePlan(nativePlan);
        return;
      }

      Complex[] output = managedOutput!;
      for (int i = 0; i < Size; i++)
        output[i] = new Complex(managedInput[i], 0);
      Fourier.Forward(output, FourierOptions.Matlab);
    }

    internal Complex Bin(int bin)
    {
      ThrowIfUnavailable();
      if ((uint)bin >= (uint)PositiveBins)
        throw new ArgumentOutOfRangeException(nameof(bin));
      if (nativeEnabled)
      {
        float* spectrum = (float*)nativeOutput;
        return new Complex(spectrum[2 * bin], spectrum[2 * bin + 1]);
      }
      return managedOutput![bin];
    }

    internal double Power(int bin)
    {
      ThrowIfUnavailable();
      if ((uint)bin >= (uint)PositiveBins)
        throw new ArgumentOutOfRangeException(nameof(bin));

      if (nativeEnabled)
      {
        float* spectrum = (float*)nativeOutput;
        double re = spectrum[bin * 2];
        double im = spectrum[bin * 2 + 1];
        return re * re + im * im;
      }
      Complex value = managedOutput![bin];
      return value.Real * value.Real +
        value.Imaginary * value.Imaginary;
    }

    internal double Magnitude(int bin) => Math.Sqrt(Power(bin));

    private void ThrowIfUnavailable()
    {
      if (released || availableForRent)
        throw new ObjectDisposedException(nameof(CwRealFft));
    }

    public void Dispose()
    {
      if (released || availableForRent) return;
      if (pooled)
      {
        var pool = pools[Size];
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
      FreeNativeResources();
    }

    private void FreeNativeResources()
    {
      if (nativePlan != IntPtr.Zero)
      {
        lock (plannerSync)
          DestroyNativePlan(nativePlan);
        nativePlan = IntPtr.Zero;
      }
      if (nativeOutput != IntPtr.Zero)
      {
        NativeFree(nativeOutput);
        nativeOutput = IntPtr.Zero;
      }
      if (nativeInput != IntPtr.Zero)
      {
        NativeFree(nativeInput);
        nativeInput = IntPtr.Zero;
      }
    }
  }
}
