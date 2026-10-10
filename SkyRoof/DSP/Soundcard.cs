using System.Runtime.InteropServices;
using MathNet.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace VE3NEA
{
  public class AudioDeviceEntry
  {
    public string Id, Name;
    public AudioDeviceEntry(string id, string name) { Id = id; Name = name; }
  }




  //-----------------------------------------------------------------------------------------------
  //                                  NAudio output adapter
  //-----------------------------------------------------------------------------------------------

  // Keep the existing VE3NEA.Dsp RingBuffer<T> contract while presenting it to NAudio as float PCM.
  // T is float (mono) or Complex32 (two interleaved float channels).
  internal sealed class RingBufferWaveProvider<T> : IWaveProvider
  {
    private float volume;

    public RingBuffer<T> Buffer { get; }
    public WaveFormat WaveFormat { get; }

    public float Volume
    {
      get => volume;
      set
      {
        if (value < 0f || value > 1f) throw new ArgumentOutOfRangeException(nameof(value));
        volume = value;
      }
    }

    public RingBufferWaveProvider(int samplingRate)
    {
      int channelCount = typeof(T) == typeof(Complex32) ? 2 : 1;
      WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(samplingRate, channelCount);
      Buffer = new RingBuffer<T>(2 * samplingRate);
    }

    public void AddSamples(T[] samples, int offset = 0, int? count = null)
    {
      Buffer.Write(samples, offset, count ?? samples.Length);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
      int read = Buffer.ReadBytes(buffer, offset, count);
      float gain = volume;

      if (gain == 1f) return read;

      int floatByteCount = read - (read % sizeof(float));
      Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, floatByteCount));

      if (gain == 0f)
        samples.Clear();
      else
        for (int i = 0; i < samples.Length; i++)
          samples[i] *= gain;

      return read;
    }
  }




  //-----------------------------------------------------------------------------------------------
  //                                     base class
  //-----------------------------------------------------------------------------------------------

  // <T> is float or Complex32

  public abstract class Soundcard : IDisposable
  {
    protected enum SoundcardState { Stopped, Starting, Running, Stopping }


    protected const int DEFAULT_SAMPLING_RATE = 48_000;
    protected readonly int SamplingRate;
    protected MMDevice? mmDevice;
    protected bool enabled;
    private System.Timers.Timer? Timer;
    protected SoundcardState State = SoundcardState.Stopped;

    public bool Retry;
    public bool Enabled { get => enabled; set => SetEnabled(value); }
    public bool IsRunning => State == SoundcardState.Running;
    
    public event EventHandler? StateChanged;
   

    public Soundcard(string? audioDeviceId = null, int? samplingRate = null)
    {
      SamplingRate = samplingRate ?? DEFAULT_SAMPLING_RATE;
      SetDeviceId(audioDeviceId);
    }




    //-----------------------------------------------------------------------------------------------
    //                                     start / stop
    //-----------------------------------------------------------------------------------------------
    private void SetEnabled(bool value)
    {
      if (value == Enabled) return;
      enabled = value;

      if (value) Start(); else Stop();
    }

    protected void Start()
    {
      if (State != SoundcardState.Stopped) return;
      State = SoundcardState.Starting;

      try
      {
        if (mmDevice?.State != DeviceState.Active)
          throw new Exception($"Audio device not active: {GetDisplayName()}");

        DoStart();
        State = SoundcardState.Running;
        OnStateChanged();
      }
      catch (Exception e)
      {
        Log.Error(e, $"Error starting {GetType().Name}");
        Stop();
      }
    }

    protected void Stop()
    {
      if (State == SoundcardState.Stopped) return;
      State = SoundcardState.Stopping;

      DoStop();
      Cleanup();

      State = SoundcardState.Stopped;
      OnStateChanged();
    }

    protected void OnStateChanged()
    {
      StateChanged?.Invoke(this, EventArgs.Empty);

      EnableRetry(Retry && Enabled && !IsRunning);
    }

    protected void Soundcard_Stopped(object? sender, StoppedEventArgs e)
    {
      if (!IsCurrentSoundcard(sender)) return;
      if (State == SoundcardState.Stopped || State == SoundcardState.Stopping) return;

      if (e.Exception != null)
        Log.Error(e.Exception, $"{GetType().Name} stopped unexpectedly");

      ThreadPool.QueueUserWorkItem(_ => Stop());
    }




    //-----------------------------------------------------------------------------------------------
    //                                        retry
    //-----------------------------------------------------------------------------------------------
    private void EnableRetry(bool value)
    {
      if (Timer != null)
      {
        Timer.Elapsed -= Timer_Elapsed;
        Timer.Stop();
        Timer.Dispose();
        Timer = null;
      }

      if (value)
      {
        Timer = new();
        Timer.Interval = 3000;
        Timer.Elapsed += Timer_Elapsed;
        Timer.Start();
      }
    }

    private void Timer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
      Start();
      EnableRetry(!IsRunning);
    }




    //-----------------------------------------------------------------------------------------------
    //                                        get / set
    //-----------------------------------------------------------------------------------------------
    public void SetDeviceId(string? deviceId)
    {
      bool wasEnabled = enabled;
      Enabled = false;

      if (deviceId == null)
      {
        mmDevice?.Dispose();
        mmDevice = null;
        return;
      }

      MMDevice? newDevice = null;

      try
      {
        using var deviceEnumerator = new MMDeviceEnumerator();
        newDevice = deviceEnumerator.GetDevice(deviceId);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error setting soundcard device ID");
        Enabled = wasEnabled;
        return;
      }

      mmDevice?.Dispose();
      mmDevice = newDevice;
      Enabled = wasEnabled;
    }

    internal string GetDisplayName()
    {
      try
      {
        return mmDevice?.FriendlyName ?? "None Selected";
      }
      catch
      {
        return "Device Failed";
      }
    }




    //-----------------------------------------------------------------------------------------------
    //                                     device list
    //-----------------------------------------------------------------------------------------------
    
    //TODO: return Dictionary instead of array
    public static AudioDeviceEntry[] ListDevices(DataFlow direction)
    {
      using var deviceEnumerator = new MMDeviceEnumerator();
      var entries = new List<AudioDeviceEntry>();

      foreach (MMDevice device in deviceEnumerator.EnumerateAudioEndPoints(direction, DeviceState.Active))
        using (device)
          entries.Add(new AudioDeviceEntry(device.ID, device.FriendlyName));

      return entries.ToArray();
    }

    public static string? GetPreferredSoundcardId(
      DataFlow direction,
      params string[] preferredNameFragments)
    {
      AudioDeviceEntry[] devices =
        ListDevices(direction);

      foreach (string fragment in
        preferredNameFragments.Where(x =>
          !string.IsNullOrWhiteSpace(x)))
      {
        AudioDeviceEntry? match =
          devices.FirstOrDefault(device =>
            device.Name.Contains(
              fragment,
              StringComparison.OrdinalIgnoreCase));
        if (match != null)
          return match.Id;
      }

      return GetDefaultSoundcardId(direction);
    }

    public static string? GetDefaultSoundcardId(DataFlow direction)
    {
      try
      {
        using var deviceEnumerator = new MMDeviceEnumerator();
        using MMDevice device = deviceEnumerator.GetDefaultAudioEndpoint(direction, Role.Multimedia);
        return device.ID;
      }
      catch (Exception ex)
      {
        Log.Error(ex, $"Default {direction} audio device not found.");
        return null;
      }
    }

    public static string? GetFirstVacId(DataFlow direction)
    {
      using var deviceEnumerator = new MMDeviceEnumerator();

      foreach (MMDevice device in deviceEnumerator.EnumerateAudioEndPoints(direction, DeviceState.Active))
        using (device)
          if (device.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            return device.ID;

      return null;
    }



    public void Dispose()
    {
      Enabled = false;
      EnableRetry(false);
      mmDevice?.Dispose();
      mmDevice = null;
      GC.SuppressFinalize(this);
    }

    protected abstract void DoStart();
    protected abstract void DoStop();
    protected abstract void Cleanup();
    protected abstract bool IsCurrentSoundcard(object? sender);
  }




  //-----------------------------------------------------------------------------------------------
  //                                     output soundcard
  //-----------------------------------------------------------------------------------------------
  public class OutputSoundcard<T> : Soundcard
  {
    private readonly RingBufferWaveProvider<T> waveProvider;
    private WasapiOut? wasapiOut;
    private float volume;

    public RingBuffer<T> Buffer => waveProvider.Buffer;
    public float Volume { get => volume; set => SetVolume(value); }


    public OutputSoundcard(string? audioDeviceId = null, int? samplingRate = null) 
      : base(audioDeviceId, samplingRate)
    {
      waveProvider = new RingBufferWaveProvider<T>(SamplingRate);
      waveProvider.Volume = volume;
    }

    protected override bool IsCurrentSoundcard(object? sender)
    {
      return ReferenceEquals(sender, wasapiOut);
    }

    protected override void DoStart()
    {
      waveProvider.Buffer.Clear();
      waveProvider.Volume = volume;

      wasapiOut = new WasapiOut(mmDevice!, AudioClientShareMode.Shared, false, 200);
      wasapiOut.Init(waveProvider);
      wasapiOut.PlaybackStopped += Soundcard_Stopped;
      wasapiOut.Play();
    }

    protected override void DoStop()
    {
      try { wasapiOut?.Stop(); } catch { }
    }

    protected override void Cleanup()
    {
      if (wasapiOut != null)
      {
        wasapiOut.PlaybackStopped -= Soundcard_Stopped;
        wasapiOut.Dispose();
        wasapiOut = null;
      }

      waveProvider.Buffer.Clear();
    }

    private void SetVolume(float value)
    {
      if (value < 0f || value > 1f) throw new ArgumentOutOfRangeException(nameof(value));

      volume = value;
      waveProvider.Volume = value;
    }

    public void AddSamples(T[] samples, int offset = 0, int? count = null)
    {
      if (Enabled) waveProvider.AddSamples(samples, offset, count);
    }
  }




  //-----------------------------------------------------------------------------------------------
  //                                     input soundcard
  //-----------------------------------------------------------------------------------------------
  public class InputSoundcard<T> : Soundcard
  {
    private WasapiCapture? soundIn;
    private BufferedWaveProvider? captureBuffer;
    private ISampleProvider? SampleSource;

    private Thread? ReaderThread;
    private volatile bool stopping;

    public event EventHandler<DataEventArgs<float>>? SamplesAvailable;

    public InputSoundcard(string? audioDeviceId = null, int? samplingRate = null)
      : base(audioDeviceId, samplingRate)
    {
    }

    protected override bool IsCurrentSoundcard(object? sender)
    {
      return ReferenceEquals(sender, soundIn);
    }

    protected override void DoStart()
    {
      int channelCount = typeof(T) == typeof(Complex32) ? 2 : 1;
      WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SamplingRate, channelCount);

      soundIn = new WasapiCapture(mmDevice!, false, 200)
      {
        ShareMode = AudioClientShareMode.Shared,
        WaveFormat = format
      };

      captureBuffer = new BufferedWaveProvider(format)
      {
        BufferDuration = TimeSpan.FromSeconds(2),
        DiscardOnBufferOverflow = true,
        ReadFully = false
      };

      SampleSource = captureBuffer.ToSampleProvider();

      soundIn.DataAvailable += SoundIn_DataAvailable;
      soundIn.RecordingStopped += Soundcard_Stopped;
      soundIn.StartRecording();

      StartReaderThread();
    }

    private void SoundIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
      captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    protected override void DoStop()
    {
      try { soundIn?.StopRecording(); } catch { }
    }

    protected override void Cleanup()
    {
      if (soundIn != null)
      {
        soundIn.DataAvailable -= SoundIn_DataAvailable;
        soundIn.RecordingStopped -= Soundcard_Stopped;
        soundIn.Dispose();
        soundIn = null;
      }

      StopReaderThread();
      captureBuffer?.ClearBuffer();
      captureBuffer = null;
      SampleSource = null;
    }

    private void StartReaderThread()
    {
      stopping = false;

      ReaderThread = new Thread(ReaderLoop)
      {
        IsBackground = true,
        Name = "InputSoundcardReader"
      };

      ReaderThread.Start();
    }

    private void StopReaderThread()
    {
      stopping = true;
      ReaderThread?.Join();
      ReaderThread = null;
    }

    private const int blockSize = 4800;
    private readonly DataEventArgs<float> Args = new();

    private void ReaderLoop()
    {
      Args.Data = new float[blockSize];

      while (!stopping)
        try
        {
          if (SampleSource == null) break;

          Args.Count = SampleSource.Read(Args.Data, 0, blockSize);

          if (Args.Count > 0)
          {
            Args.Utc = DateTime.UtcNow;
            // this event is always processed synchronously in ThreadedProcessor#StartProcessing
            SamplesAvailable?.Invoke(this, Args);
          }
          else
            Thread.Sleep(20); // avoid busy spin if device starves
        }
        catch (ObjectDisposedException) {}
        catch (InvalidOperationException) {}
        catch (COMException) {}
    }
  }
}
