using System;
using System.Drawing;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MathNet.Numerics;
using NAudio.Wave;
using Serilog;
using VE3NEA;
using VE3NEA.SkySSTV;
using SkyRoof.IF;

namespace SkyRoof.SSTV
{
  public enum SstvIfModulation { Ssb, Fm, Lsb }

  public enum SstvReceiveSource
  {
    CaptureAf,
    LoopbackAf,
    Capture12kIf,
    Loopback12kIf
  }

  /// <summary>
  /// Receiver adapter for real AF from Windows capture/RS-BA1 playback loopback,
  /// or real 12 kHz IF supplied by the radio. The existing SkySignals SSTV
  /// decoder expects FM-on-FM IQ; the bridge re-modulates clean AF onto a
  /// synthetic FM IQ carrier without involving TX hardware or radio CAT.
  /// A bounded worker queue keeps decoder CPU off WASAPI capture callbacks.
  /// </summary>
  public sealed class SstvAudioReceiveSession : IDisposable
  {
    private const int SampleRate = 48000;
    private const double FmDeviationHz = 3300.0;
    private readonly InputSoundcard<float> capture = new(
      captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600);
    private readonly LoopbackInputSoundcard loopback = new();
    private IfAfService? sharedIf;
    private readonly object gate = new();
    private CancellationTokenSource? cancel;
    private Channel<float[]>? channel;
    private Task? worker;
    private int epoch;
    private int dropped;
    private SstvTxModeProfile? manuallyRequestedMode;
    private int manualCorrectionHz;
    public void ManualStart(SstvTxModeProfile mode)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      Interlocked.Exchange(ref manuallyRequestedMode, mode);
    }
    private int autoVisCorrectionHz;
    private bool autoVisEnabled = true;
    public bool AutoVisEnabled
    {
      get => Volatile.Read(ref autoVisEnabled);
      set => Volatile.Write(ref autoVisEnabled, value);
    }
    public int AutoVisCorrectionHz => Volatile.Read(ref autoVisCorrectionHz);
    public event Action<SstvVisAfcLock>? VisSynchronized;
    public event Action<float[]>? SpectrumAvailable;
    public event Action<string>? FskStationIdentified;
    public int ManualCorrectionHz
    {
      get => Volatile.Read(ref manualCorrectionHz);
      set
      {
        if (value is < -500 or > 500) throw new ArgumentOutOfRangeException(nameof(value));
        Volatile.Write(ref manualCorrectionHz, value);
      }
    }
    private bool disposed;

    public event Action<SstvImageEvent>? ImageAvailable;
    /// <summary>Ownership of the bitmap is transferred to event handlers.</summary>
    public event Action<Bitmap, string, bool, double>? RasterImageAvailable;
    public long DroppedBlocks => Interlocked.CompareExchange(ref dropped, 0, 0);
    public bool IsRunning => capture.IsRunning || loopback.IsRunning ||
      sharedIf?.IsRunning == true;

    public SstvAudioReceiveSession()
    {
      capture.SamplesAvailable += OnSamples;
      loopback.SamplesAvailable += OnSamples;
    }

    public void Start(SstvReceiveSource source, string deviceId,
      SstvIfModulation ifModulation = SstvIfModulation.Ssb)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (string.IsNullOrWhiteSpace(deviceId))
        throw new ArgumentException("Choose a valid soundcard endpoint.", nameof(deviceId));
      Stop();
      Interlocked.Exchange(ref manuallyRequestedMode, null);
      var queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(16)
      {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
      });
      var stop = new CancellationTokenSource();
      int version;
      lock (gate)
      {
        version = ++epoch;
        channel = queue;
        cancel = stop;
        Interlocked.Exchange(ref dropped, 0);
        Interlocked.Exchange(ref autoVisCorrectionHz, 0);
        worker = Task.Run(() => RunAsync(queue.Reader, stop.Token, source,
          version, ifModulation));
      }
      try
      {
        if (source is SstvReceiveSource.CaptureAf or SstvReceiveSource.Capture12kIf)
        {
          capture.SetDeviceId(deviceId);
          capture.Enabled = true;
          if (!capture.IsRunning) throw new InvalidOperationException("Audio input failed to start");
        }
        else
        {
          loopback.SetDeviceId(deviceId);
          loopback.Enabled = true;
          if (!loopback.IsRunning) throw new InvalidOperationException("Playback loopback failed to start");
        }
      }
      catch
      {
        Stop();
        throw;
      }
    }

    public void StartShared(IfAfService hub, SstvIfModulation ifModulation)
    {
      ArgumentNullException.ThrowIfNull(hub);
      if (!hub.IsRunning) throw new InvalidOperationException("Shared IF input stopped");
      Stop();
      Interlocked.Exchange(ref manuallyRequestedMode, null);
      var next = Channel.CreateBounded<float[]>(new BoundedChannelOptions(16)
      {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
      });
      var token = new CancellationTokenSource();
      lock (gate)
      {
        int version = ++epoch;
        channel = next;
        cancel = token;
        Interlocked.Exchange(ref dropped, 0);
        Interlocked.Exchange(ref autoVisCorrectionHz, 0);
        worker = Task.Run(() => RunAsync(next.Reader, token.Token,
          SstvReceiveSource.Capture12kIf, version, ifModulation));
      }
      sharedIf = hub;
      hub.RawIfAvailable += OnSamples;
    }

    public void Stop()
    {
      if (sharedIf != null)
      {
        sharedIf.RawIfAvailable -= OnSamples;
        sharedIf = null;
      }
      capture.Enabled = false;
      loopback.Enabled = false;
      lock (gate)
      {
        epoch++;
        channel?.Writer.TryComplete();
        channel = null;
        cancel?.Cancel();
        cancel = null;
        worker = null;
      }
    }

    private void OnSamples(object? sender, DataEventArgs<float> args)
    {
      var destination = channel;
      if (disposed || destination == null || args.Count <= 0) return;
      int count = Math.Min(args.Count, args.Data.Length);
      var copy = new float[count];
      Array.Copy(args.Data, copy, count);
      if (!destination.Writer.TryWrite(copy))
        Interlocked.Increment(ref dropped);
    }

    private async Task RunAsync(ChannelReader<float[]> reader, CancellationToken token,
      SstvReceiveSource source, int version, SstvIfModulation ifModulation)
    {
      SstvDecoder decoder = new();
      var ifDemod = new SstvIfAudioDemodulator(ifModulation);
      using var raster = new SstvLiveRasterReceiver();
      raster.ImageReady += (bitmap, mode, final, offset) =>
      {
        if (Volatile.Read(ref epoch) == version && !disposed)
        {
          if (RasterImageAvailable == null) bitmap.Dispose();
          else RasterImageAvailable.Invoke(bitmap, mode, final, offset);
        }
        else bitmap.Dispose();
      };
      var manualShifter = new SstvAfFrequencyShifter();
      var shifter = new SstvAfFrequencyShifter();
      var nativeSync = new SstvNativeLineSyncAfc();
      nativeSync.OffsetUpdated += offset =>
      {
        if (Volatile.Read(ref epoch) == version && AutoVisEnabled)
          Volatile.Write(ref autoVisCorrectionHz,
            Math.Clamp((int)Math.Round(-offset), -500, 500));
      };
      var liveFsk = new SstvLiveFskIdDetector();
      liveFsk.StationIdentified += call =>
      {
        if (Volatile.Read(ref epoch) == version && !disposed)
          FskStationIdentified?.Invoke(call);
      };
      var visDetector = new SstvVisAfcDetector();
      visDetector.Reset(version);
      visDetector.VisLocked += detected =>
      {
        if (Volatile.Read(ref epoch) != version) return;
        if (AutoVisEnabled)
          Volatile.Write(ref autoVisCorrectionHz,
            Math.Clamp((int)Math.Round(-detected.OffsetHz), -500, 500));
        SstvModeSpec? native = SstvModes.FromVisByte(detected.VisByte);
        if (native != null) nativeSync.Begin(detected, native);
        else nativeSync.Disable();
        VisSynchronized?.Invoke(detected);
      };
      double fmPhase = 0;
      long lastDropped = 0;
      var spectrumWindow = new float[2048];
      int spectrumWrite = 0, spectrumFilled = 0, spectrumInterval = 0;
      void OnImage(SstvImageEvent evt)
      {
        if (!disposed && Volatile.Read(ref epoch) == version)
          ImageAvailable?.Invoke(evt);
      }

      decoder.ImageUpdated += OnImage;
      decoder.ImageCompleted += OnImage;
      try
      {
        await foreach (float[] samples in reader.ReadAllAsync(token))
        {
          if (Volatile.Read(ref epoch) != version) return;
          long droppedNow = DroppedBlocks;
          if (droppedNow != lastDropped)
          {
            // A gap invalidates SSTV scan timing; resync instead of joining
            // disconnected PCM into a plausible-looking corrupted image.
            lastDropped = droppedNow;
            decoder.ImageUpdated -= OnImage;
            decoder.ImageCompleted -= OnImage;
            decoder.Dispose();
            decoder = new SstvDecoder();
            decoder.ImageUpdated += OnImage;
            decoder.ImageCompleted += OnImage;
            ifDemod.Reset();
            manualShifter.Reset();
            shifter.Reset();
            raster.Reset();
            nativeSync.Reset();
            liveFsk.Reset();
            visDetector.Reset(version);
            Volatile.Write(ref autoVisCorrectionHz, 0);
            fmPhase = 0;
            spectrumWrite = spectrumFilled = spectrumInterval = 0;
            Array.Clear(spectrumWindow);
            // Keep receiving, but do not claim seamless scan timing.
          }
          var iq = new Complex32[samples.Length];
          var af = new float[samples.Length];
          bool intermediateIf = source is SstvReceiveSource.Capture12kIf or
            SstvReceiveSource.Loopback12kIf;
          // Operator correction must precede *both* VIS acquisition and
          // the extended live raster decoder. Previously it affected only
          // the native Robot/PD branch, so ±300 Hz satellite tuning errors
          // could not be recovered by manual tune for RGB modes.
          int manualHz = ManualCorrectionHz;
          for (int i = 0; i < samples.Length; i++)
          {
            double audio = intermediateIf ? ifDemod.Process(samples[i]) : samples[i];
            float clean = (float)Math.Clamp(double.IsFinite(audio) ? audio : 0, -1.0, 1.0);
            af[i] = manualShifter.Process(clean, manualHz);
          }
          // Show the operator the same AF that enters the VIS detector:
          // live 900–2400 Hz tone power, without unverified dBm claims.
          for (int i = 0; i < af.Length; i++)
          {
            spectrumWindow[spectrumWrite] = af[i];
            if (++spectrumWrite == spectrumWindow.Length) spectrumWrite = 0;
            if (spectrumFilled < spectrumWindow.Length) spectrumFilled++;
          }
          spectrumInterval += af.Length;
          if (spectrumFilled == spectrumWindow.Length &&
              spectrumInterval >= 9600 && SpectrumAvailable != null)
          {
            spectrumInterval = 0;
            float[] spectrumAudio = new float[spectrumWindow.Length];
            int tail = spectrumWindow.Length - spectrumWrite;
            Array.Copy(spectrumWindow, spectrumWrite, spectrumAudio, 0, tail);
            if (spectrumWrite > 0)
              Array.Copy(spectrumWindow, 0, spectrumAudio, tail, spectrumWrite);
            SpectrumAvailable.Invoke(SstvToneSpectrum.Measure(spectrumAudio));
          }
          // VIS offset is now estimated relative to the manually centered
          // AF. Do not include that manual correction again downstream.
          visDetector.Process(af);
          nativeSync.Process(af);
          liveFsk.Process(af);
          SstvTxModeProfile? forced =
            Interlocked.Exchange(ref manuallyRequestedMode, null);
          if (forced != null) raster.ForceMode(forced);
          int correction = AutoVisEnabled ? AutoVisCorrectionHz : 0;
          var correctedAf = new float[af.Length];
          // VIS and line-sync estimators always inspect pre-AFC audio.
          // Feed the SAME software-corrected AF to both extended raster
          // (Scottie/Martin/SC2) and native Robot/PD decoder branches.
          for (int i = 0; i < af.Length; i++)
          {
            double audio = shifter.Process(af[i], correction);
            correctedAf[i] = (float)audio;
            fmPhase += 2.0 * Math.PI * (FmDeviationHz / SampleRate) * audio;
            if (fmPhase > Math.PI) fmPhase -= 2 * Math.PI;
            if (fmPhase < -Math.PI) fmPhase += 2 * Math.PI;
            iq[i] = new Complex32((float)Math.Cos(fmPhase), (float)Math.Sin(fmPhase));
          }
          raster.Process(correctedAf);
          decoder.Process(iq);
        }
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested) { }
      catch (Exception ex) { Log.Error(ex, "SSTV AF/IF receiver stopped unexpectedly"); }
      finally
      {
        decoder.ImageUpdated -= OnImage;
        decoder.ImageCompleted -= OnImage;
        decoder.Dispose();
      }
    }

    public void Dispose()
    {
      if (disposed) return;
      Stop();
      disposed = true;
      capture.SamplesAvailable -= OnSamples;
      loopback.SamplesAvailable -= OnSamples;
      capture.Dispose();
      loopback.Dispose();
    }
  }

  /// <summary>Unified IF demodulation shared with the WSJT-X AF pipeline.
  /// Real multiplication is intentionally NOT used: it folds opposite-sideband
  /// noise and would make SSB worse than the IC-9700's own AF path.</summary>
  public sealed class SstvIfAudioDemodulator
  {
    private readonly IfAfDemodulator decoder;

    public SstvIfAudioDemodulator(
      SstvIfModulation modulation = SstvIfModulation.Ssb)
    {
      decoder = new IfAfDemodulator(modulation switch
      {
        SstvIfModulation.Fm => IfAfMode.Fm,
        SstvIfModulation.Lsb => IfAfMode.Lsb,
        _ => IfAfMode.Usb
      });
    }

    public double Process(float sample) => decoder.Process(sample);

    public void Reset() => decoder.Reset();
  }
}
