using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VE3NEA
{
  /// <summary>
  /// Converts an arbitrary interleaved float input to mono by averaging every
  /// channel in each frame. Kept separate from device code so channel handling
  /// can be unit-tested without real Windows audio hardware.
  /// </summary>
  internal sealed class MonoMixSampleProvider : ISampleProvider
  {
    private readonly ISampleProvider source;
    private readonly int channels;
    private float[] scratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    internal MonoMixSampleProvider(ISampleProvider source)
    {
      this.source = source ??
        throw new ArgumentNullException(nameof(source));
      channels = source.WaveFormat.Channels;
      if (channels < 1)
        throw new ArgumentException(
          "Audio source must expose at least one channel.",
          nameof(source));

      WaveFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(
          source.WaveFormat.SampleRate,
          1);
    }

    public int Read(
      float[] buffer,
      int offset,
      int count)
    {
      ArgumentNullException.ThrowIfNull(buffer);
      if (offset < 0 || count < 0 ||
          offset + count > buffer.Length)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (count == 0)
        return 0;

      int needed = checked(count * channels);
      if (scratch.Length < needed)
        scratch = new float[needed];

      int read =
        source.Read(
          scratch,
          0,
          needed);
      int frames = read / channels;

      for (int frame = 0;
           frame < frames;
           frame++)
      {
        double sum = 0;
        int sourceOffset =
          frame * channels;
        for (int channel = 0;
             channel < channels;
             channel++)
          sum += scratch[sourceOffset + channel];

        buffer[offset + frame] =
          (float)(sum / channels);
      }

      return frames;
    }
  }

  /// <summary>
  /// Captures a Windows render endpoint with WASAPI loopback and emits mono
  /// float PCM at the requested sample rate. This captures the endpoint mix,
  /// not a process-isolated stream; callers should select a dedicated endpoint
  /// when they require only RS-BA1 audio.
  /// </summary>
  public sealed class LoopbackInputSoundcard : Soundcard
  {
    private WasapiLoopbackCapture? soundIn;
    private BufferedWaveProvider? captureBuffer;
    private ISampleProvider? sampleSource;

    private Thread? readerThread;
    private volatile bool stopping;

    private const int BlockSize = 4800;
    private readonly DataEventArgs<float> args = new();

    public event EventHandler<DataEventArgs<float>>? SamplesAvailable;

    public LoopbackInputSoundcard(
      string? audioDeviceId = null,
      int? samplingRate = null)
      : base(audioDeviceId, samplingRate)
    {
    }

    protected override bool IsCurrentSoundcard(
      object? sender) =>
      ReferenceEquals(sender, soundIn);

    protected override void DoStart()
    {
      soundIn =
        new WasapiLoopbackCapture(mmDevice!);

      WaveFormat sourceFormat =
        soundIn.WaveFormat;
      captureBuffer =
        new BufferedWaveProvider(sourceFormat)
        {
          BufferDuration =
            TimeSpan.FromSeconds(2),
          DiscardOnBufferOverflow = true,
          ReadFully = false
        };

      ISampleProvider source =
        captureBuffer.ToSampleProvider();

      if (source.WaveFormat.Channels != 1)
        source =
          new MonoMixSampleProvider(source);

      if (source.WaveFormat.SampleRate !=
          SamplingRate)
        source =
          new WdlResamplingSampleProvider(
            source,
            SamplingRate);

      sampleSource = source;

      soundIn.DataAvailable +=
        SoundIn_DataAvailable;
      soundIn.RecordingStopped +=
        Soundcard_Stopped;
      soundIn.StartRecording();

      StartReaderThread();
    }

    private void SoundIn_DataAvailable(
      object? sender,
      WaveInEventArgs e)
    {
      captureBuffer?.AddSamples(
        e.Buffer,
        0,
        e.BytesRecorded);
    }

    protected override void DoStop()
    {
      try
      {
        soundIn?.StopRecording();
      }
      catch
      {
      }
    }

    protected override void Cleanup()
    {
      if (soundIn != null)
      {
        soundIn.DataAvailable -=
          SoundIn_DataAvailable;
        soundIn.RecordingStopped -=
          Soundcard_Stopped;
        soundIn.Dispose();
        soundIn = null;
      }

      StopReaderThread();
      captureBuffer?.ClearBuffer();
      captureBuffer = null;
      sampleSource = null;
    }

    private void StartReaderThread()
    {
      stopping = false;
      readerThread =
        new Thread(ReaderLoop)
        {
          IsBackground = true,
          Name = "LoopbackInputSoundcardReader"
        };
      readerThread.Start();
    }

    private void StopReaderThread()
    {
      stopping = true;
      readerThread?.Join();
      readerThread = null;
    }

    private void ReaderLoop()
    {
      args.Data =
        new float[BlockSize];

      while (!stopping)
      {
        try
        {
          ISampleProvider? source =
            sampleSource;
          if (source == null)
            break;

          args.Count =
            source.Read(
              args.Data,
              0,
              BlockSize);

          if (args.Count > 0)
          {
            // Timestamp the completed PCM block. CwAudioHub treats this as
            // the end time of the newest block.
            args.Utc = DateTime.UtcNow;
            SamplesAvailable?.Invoke(
              this,
              args);
          }
          else
          {
            Thread.Sleep(20);
          }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }
      }
    }
  }
}
