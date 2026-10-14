using MathNet.Numerics;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using VE3NEA;

namespace SkyRoof
{
  public class RecordingManager : IDisposable
  {
    private readonly Context ctx;
    public event EventHandler? Changed;

    private const int RECORDING_MINUTES = 30;
    private const int BUFFER_SIZE = SdrConst.AUDIO_SAMPLING_RATE * 60 * RECORDING_MINUTES;
    private const int PLAYBACK_PREBUFFER_CHUNKS = 10;
    private const int PLAYBACK_TIMER_INTERVAL_MS = 20;
    private const int PLAYBACK_SAMPLES_PER_TICK = SdrConst.AUDIO_SAMPLING_RATE * PLAYBACK_TIMER_INTERVAL_MS / 1000;

    private const int INITIAL_BUFFER_SECONDS = 5;
    private readonly object recordingLock = new();
    private InputSoundcard<float>? captureInput;
    private LoopbackInputSoundcard? loopbackInput;
    private RecordingAudioSource activeAudioSource = RecordingAudioSource.Sdr;
    private string recordingSourceStatus = "Not recording";

    private float[]? audioBuffer;
    private Complex32[]? iqBuffer;
    private int samplesInBuffer;
    private int playbackPosition;
    private bool speakerEnabledForPlayback;
    private readonly object playbackLock = new();
    private DateTime recordingStartTime;
    private RecordingEvents recordingEvents = new();

    public RecordingManager(Context ctx)
    {
      this.ctx = ctx;
    }

    public float[]? AudioBuffer => audioBuffer;
    public Complex32[]? IqBuffer => iqBuffer;
    public int SamplesInBuffer => System.Threading.Volatile.Read(ref samplesInBuffer);
    public int PlaybackPosition => playbackPosition;
    public bool IsRecording { get; private set; }
    public bool IsPlayingBack { get; private set; }
    public bool IsRecordingAudio => iqBuffer == null;
    public RecordingEvents RecordingEvents => recordingEvents;
    public DateTime RecordingStartTime => recordingStartTime;
    public bool HasData => SamplesInBuffer > 0;
    public int AcceptedAudioSamples => SamplesInBuffer;
    public RecordingAudioSource ActiveAudioSource => activeAudioSource;
    public string RecordingSourceStatus => recordingSourceStatus;

    private RecordingAudioSource ResolveSource()
    {
      var settings = ctx.Settings.Audio;
      if (settings.RecorderSource != RecordingAudioSource.Auto)
        return settings.RecorderSource;

      return ctx.Sdr?.Enabled == true
        ? RecordingAudioSource.Sdr
        : RecordingAudioSource.RsBa1Loopback;
    }

    private string? ResolveCaptureDevice() =>
      ctx.Settings.Audio.RecorderCaptureDeviceId ??
      ctx.Settings.CwConsole.CaptureDeviceId;

    private string? ResolveLoopbackDevice() =>
      ctx.Settings.Audio.RecorderLoopbackDeviceId ??
      ctx.Settings.CwConsole.RsBa1LoopbackDeviceId ??
      ctx.Settings.Audio.RsBa1PlaybackDeviceId ??
      ctx.Settings.Audio.SpeakerSoundcard;

    private void StartExternalAudioInput()
    {
      if (activeAudioSource == RecordingAudioSource.Sdr)
      {
        recordingSourceStatus = "SDR demodulator";
        return;
      }

      if (activeAudioSource == RecordingAudioSource.WasapiCapture)
      {
        var id = ResolveCaptureDevice();
        if (string.IsNullOrWhiteSpace(id))
        {
          recordingSourceStatus = "USB AF capture device not configured";
          Log.Warning("Recorder: USB AF capture device is not configured");
          return;
        }

        captureInput = new InputSoundcard<float>(
          id, SdrConst.AUDIO_SAMPLING_RATE,
          captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600);
        captureInput.SamplesAvailable += ExternalAudioSamplesAvailable;
        captureInput.Enabled = true;
        recordingSourceStatus = captureInput.IsRunning
          ? "USB AF capture: " + captureInput.GetDisplayName()
          : "USB AF capture failed to start: " + captureInput.GetDisplayName();
        return;
      }

      var loopbackId = ResolveLoopbackDevice();
      if (string.IsNullOrWhiteSpace(loopbackId))
      {
        recordingSourceStatus = "RS-BA1 playback endpoint not configured";
        Log.Warning("Recorder: RS-BA1 loopback playback endpoint is not configured");
        return;
      }

      loopbackInput = new LoopbackInputSoundcard(loopbackId, SdrConst.AUDIO_SAMPLING_RATE);
      loopbackInput.SamplesAvailable += ExternalAudioSamplesAvailable;
      loopbackInput.Enabled = true;
      recordingSourceStatus = loopbackInput.IsRunning
        ? "RS-BA1 loopback: " + loopbackInput.GetDisplayName()
        : "RS-BA1 loopback failed to start: " + loopbackInput.GetDisplayName();
    }

    private void ExternalAudioSamplesAvailable(object? sender, DataEventArgs<float> args)
    {
      // Only the selected external source is fed to this recorder. Do not
      // route it through Slicer or Output Stream Gain.
      AddAudioSamples(args, external: true);
    }

    private void StopExternalAudioInput()
    {
      var capture = captureInput;
      captureInput = null;
      if (capture != null)
      {
        capture.SamplesAvailable -= ExternalAudioSamplesAvailable;
        capture.Dispose();
      }

      var loopback = loopbackInput;
      loopbackInput = null;
      if (loopback != null)
      {
        loopback.SamplesAvailable -= ExternalAudioSamplesAvailable;
        loopback.Dispose();
      }
    }

    public void StartRecording(bool isAudio)
    {
      if (IsRecording) StopRecording();

      lock (recordingLock)
      {
        // Five seconds initially; grow only when samples actually arrive.
        // 30 minutes is a cap, not an up-front 345/691 MB allocation.
        if (isAudio)
        {
          audioBuffer = new float[SdrConst.AUDIO_SAMPLING_RATE * INITIAL_BUFFER_SECONDS];
          iqBuffer = null;
        }
        else
        {
          iqBuffer = new Complex32[SdrConst.AUDIO_SAMPLING_RATE * INITIAL_BUFFER_SECONDS];
          audioBuffer = null;
        }

        activeAudioSource = isAudio ? ResolveSource() : RecordingAudioSource.Sdr;
        samplesInBuffer = 0;
        playbackPosition = 0;
        recordingStartTime = DateTime.UtcNow;
        recordingEvents.Start(ctx);
        recordingEvents.StartUtc = recordingStartTime;
        IsRecording = true;
        recordingSourceStatus = isAudio ? activeAudioSource.ToString() : "SDR I/Q";
      }

      Log.Information("Starting {Mode} recording from {Source}",
        isAudio ? "audio" : "I/Q", activeAudioSource);
      if (isAudio) StartExternalAudioInput();
    }

    public TimeSpan StopRecording()
    {
      lock (recordingLock) IsRecording = false;
      StopExternalAudioInput();
      return DateTime.UtcNow - recordingStartTime;
    }

    public bool AddIqSamples(DataEventArgs<Complex32> e)
    {
      ArgumentNullException.ThrowIfNull(e);
      lock (recordingLock)
      {
        if (!IsRecording || iqBuffer == null) return false;
        return AddSamples(e.Data, e.Count, ref iqBuffer);
      }
    }

    public bool AddAudioSamples(DataEventArgs<float> e) =>
      AddAudioSamples(e, external: false);

    private bool AddAudioSamples(DataEventArgs<float> e, bool external)
    {
      ArgumentNullException.ThrowIfNull(e);
      lock (recordingLock)
      {
        if (!IsRecording || audioBuffer == null ||
            (activeAudioSource == RecordingAudioSource.Sdr) == external)
          return false;
        return AddSamples(e.Data, e.Count, ref audioBuffer);
      }
    }

    private bool AddSamples<T>(T[] source, int validCount, ref T[]? buffer)
    {
      int written = AppendValidSamples(source, validCount, ref buffer,
        samplesInBuffer, BUFFER_SIZE);
      if (written == 0) return false;
      samplesInBuffer += written;
      return samplesInBuffer >= BUFFER_SIZE;
    }

    internal static int AppendValidSamples<T>(T[] source, int validCount,
      ref T[]? buffer, int alreadyWritten, int maximum)
    {
      ArgumentNullException.ThrowIfNull(source);
      if (validCount < 0 || validCount > source.Length)
        throw new ArgumentOutOfRangeException(nameof(validCount),
          "Valid sample count is outside the source buffer.");
      if (buffer == null || validCount == 0) return 0;
      if (alreadyWritten < 0 || maximum < 0 || alreadyWritten > maximum)
        throw new ArgumentOutOfRangeException(nameof(alreadyWritten));

      int toWrite = Math.Min(validCount, maximum - alreadyWritten);
      if (toWrite <= 0) return 0;
      int required = alreadyWritten + toWrite;
      if (required > buffer.Length)
      {
        int capacity = (int)Math.Min(maximum,
          Math.Max((long)required, Math.Max(1L, (long)buffer.Length * 2)));
        Array.Resize(ref buffer, capacity);
      }

      Array.Copy(source, 0, buffer, alreadyWritten, toWrite);
      return toWrite;
    }

    public void Dispose()
    {
      StopRecording();
      GC.SuppressFinalize(this);
    }

    public bool StartPlayback()
    {
      if (samplesInBuffer <= 0) return false;

      if (audioBuffer != null && !ctx.SpeakerSoundcard.Enabled)
      {
        ctx.SpeakerSoundcard.Enabled = true;
        speakerEnabledForPlayback = true;
      }

      if (playbackPosition >= samplesInBuffer) playbackPosition = 0;
      IsPlayingBack = true;
      PreparePlaybackOutputs();
      PrebufferPlayback();
      if (playbackPosition >= samplesInBuffer)
      {
        StopPlayback(true);
        return false;
      }

      return true;
    }

    public void StopPlayback(bool resetPosition = false)
    {
      IsPlayingBack = false;
      if (resetPosition) playbackPosition = 0;

      if (speakerEnabledForPlayback)
      {
        ctx.SpeakerSoundcard.Enabled = false;
        speakerEnabledForPlayback = false;
      }
    }

    public bool PumpPlaybackChunk()
    {
      lock (playbackLock)
      {
        int samplesRemaining = samplesInBuffer - playbackPosition;
        if (samplesRemaining <= 0) return false;

        int count = Math.Min(PLAYBACK_SAMPLES_PER_TICK, samplesRemaining);
        if (audioBuffer != null)
          ctx.MainForm.RoutePlaybackAudio(audioBuffer, playbackPosition, count);
        else if (iqBuffer != null)
          ctx.MainForm.RoutePlaybackIq(iqBuffer, playbackPosition, count);

        playbackPosition += count;
        return playbackPosition < samplesInBuffer;
      }
    }

    public bool SeekPlaybackToPosition(int position)
    {
      if (!IsPlayingBack || samplesInBuffer <= 0) return false;

      lock (playbackLock)
        playbackPosition = Math.Clamp(position, 0, Math.Max(0, samplesInBuffer - 1));

      PreparePlaybackOutputs();
      PrebufferPlayback();
      if (playbackPosition >= samplesInBuffer)
      {
        StopPlayback(true);
        return false;
      }

      return true;
    }

    public string GetPlaybackStatus()
    {
      TimeSpan elapsed = TimeSpan.FromSeconds(playbackPosition / (double)SdrConst.AUDIO_SAMPLING_RATE);
      TimeSpan total = TimeSpan.FromSeconds(samplesInBuffer / (double)SdrConst.AUDIO_SAMPLING_RATE);
      int percent = samplesInBuffer <= 0 ? 0 : (int)Math.Round(100.0 * playbackPosition / samplesInBuffer);
      percent = Math.Clamp(percent, 0, 100);
      return $"Playing back {(IsRecordingAudio ? "Audio" : "I/Q")} {elapsed:mm\\:ss} of {total:mm\\:ss} ({percent}%)";
    }

    public string GetRecordingStatus()
    {
      TimeSpan elapsed = DateTime.UtcNow - recordingStartTime;
      return $"Recording {(IsRecordingAudio ? "Audio" : "I/Q")} {elapsed:mm\\:ss}";
    }

    public bool LoadRecording(string filename)
    {
      string ext = Path.GetExtension(filename).ToLowerInvariant();
      bool isMp3 = ext == ".mp3";

      using var reader = new AudioFileReader(filename);

      iqBuffer = null;
      audioBuffer = null;
      IsRecording = false;
      IsPlayingBack = false;

      if (isMp3)
      {
        ISampleProvider source = reader;
        if (reader.WaveFormat.SampleRate != SdrConst.AUDIO_SAMPLING_RATE)
          source = new WdlResamplingSampleProvider(source, SdrConst.AUDIO_SAMPLING_RATE);
        audioBuffer = ReadAudioSamples(source);
      }
      else
      {
        if (reader.WaveFormat.SampleRate != SdrConst.AUDIO_SAMPLING_RATE)
          throw new InvalidOperationException($"Unsupported sample rate: {reader.WaveFormat.SampleRate} Hz");

        if (reader.WaveFormat.Channels == 1) audioBuffer = ReadAudioSamples(reader);
        else if (reader.WaveFormat.Channels == 2) iqBuffer = ReadIqSamples(reader);
        else throw new InvalidOperationException($"Unsupported channel count: {reader.WaveFormat.Channels}");
      }

      playbackPosition = 0;
      LoadRecordingEvents(filename);
      recordingStartTime = recordingEvents.StartUtc;
      Changed?.Invoke(this, EventArgs.Empty);
      return HasData;
    }

    public void SaveRecording(string fileName, bool saveAsMp3)
    {
      if (saveAsMp3) SaveRecordingAsMp3(fileName);
      else SaveRecordingAsWav(fileName);
      SaveRecordingEvents(fileName);
    }

    public Task SaveRecordingAsync(string fileName, bool saveAsMp3)
    {
      // Capture immutable array and metadata references before starting any
      // background task. A new pass may begin recording before export ends.
      RecordingManager snapshot = new(ctx);
      lock (recordingLock)
      {
        if (IsRecording)
          throw new InvalidOperationException("Stop the recording before saving.");
        snapshot.audioBuffer = audioBuffer;
        snapshot.iqBuffer = iqBuffer;
        snapshot.samplesInBuffer = samplesInBuffer;
        snapshot.recordingStartTime = recordingStartTime;
        snapshot.recordingEvents = new RecordingEvents
        {
          StartUtc = recordingEvents.StartUtc,
          Events = recordingEvents.Events.ToList()
        };
      }
      return Task.Run(() => snapshot.SaveRecording(fileName, saveAsMp3));
    }

    public bool RememberRecordingEvents()
    {
      bool changed = IsRecording && recordingEvents.RememberChanges(ctx);
      if (changed) Changed?.Invoke(this, EventArgs.Empty);
      return changed;
    }

    public bool RememberQsoSaved(string callsign)
    {
      bool changed = IsRecording && recordingEvents.RememberQsoSaved(callsign);
      if (changed) Changed?.Invoke(this, EventArgs.Empty);
      return changed;
    }

    public double GetVisibleDurationSeconds()
    {
      int sampleCount = SamplesInBuffer == 0 || IsRecording ? BUFFER_SIZE : SamplesInBuffer;
      return sampleCount / (double)SdrConst.AUDIO_SAMPLING_RATE;
    }

    public string GetRecordingEventsFileName(string recordingFileName)
    {
      return recordingFileName + ".json";
    }

    // shared file naming so the manual RecorderPanel and the headless AutoSelector name files identically:
    // yyyy-MM-dd_HH_mm_ss[_<SatName>] with a .mp3 / .wav / .iq.wav extension
    public static string BuildRecordingFileName(DateTime startLocal, string? satelliteName, string ext, bool isIqWav)
    {
      string fileName = $"{startLocal:yyyy-MM-dd_HH_mm_ss}";
      if (!string.IsNullOrWhiteSpace(satelliteName)) fileName += "_" + Utils.SanitizeFileNamePart(satelliteName);

      return isIqWav ? fileName + ".iq.wav" : fileName + "." + ext;
    }

    private void PreparePlaybackOutputs()
    {
      if (audioBuffer != null)
      {
        ctx.SpeakerSoundcard.Buffer.Clear();
        if (ctx.Settings.OutputStream.Type == DataStreamType.AudioToVac)
          ctx.AudioVacSoundcard.Buffer.Clear();
      }
      else if (iqBuffer != null && ctx.Settings.OutputStream.Type == DataStreamType.IqToVac)
        ctx.IqVacSoundcard.Buffer.Clear();
    }

    private void PrebufferPlayback()
    {
      for (int i = 0; i < PLAYBACK_PREBUFFER_CHUNKS; i++)
        if (!PumpPlaybackChunk()) break;
    }

    private float[] ReadAudioSamples(ISampleProvider reader)
    {
      int channels = reader.WaveFormat.Channels;
      float[] samples = new float[SdrConst.AUDIO_SAMPLING_RATE * INITIAL_BUFFER_SECONDS];
      float[] buffer = new float[4096 * channels];
      samplesInBuffer = 0;

      int samplesRead;
      while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0 && samplesInBuffer < BUFFER_SIZE)
      {
        int framesRead = samplesRead / channels;
        int framesToCopy = Math.Min(framesRead, BUFFER_SIZE - samplesInBuffer);
        if (samplesInBuffer + framesToCopy > samples.Length)
          Array.Resize(ref samples, Math.Min(BUFFER_SIZE,
            Math.Max(samplesInBuffer + framesToCopy, samples.Length * 2)));

        if (channels == 1) Array.Copy(buffer, 0, samples, samplesInBuffer, framesToCopy);
        else
          for (int i = 0; i < framesToCopy; i++)
          {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++)
              sum += buffer[i * channels + channel];

            samples[samplesInBuffer + i] = sum / channels;
          }

        samplesInBuffer += framesToCopy;
      }

      return samples;
    }

    private Complex32[] ReadIqSamples(ISampleProvider reader)
    {
      Complex32[] samples = new Complex32[SdrConst.AUDIO_SAMPLING_RATE * INITIAL_BUFFER_SECONDS];
      float[] buffer = new float[4096 * 2];
      samplesInBuffer = 0;

      int samplesRead;
      while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0 && samplesInBuffer < BUFFER_SIZE)
      {
        int framesRead = samplesRead / 2;
        int framesToCopy = Math.Min(framesRead, BUFFER_SIZE - samplesInBuffer);
        if (samplesInBuffer + framesToCopy > samples.Length)
          Array.Resize(ref samples, Math.Min(BUFFER_SIZE,
            Math.Max(samplesInBuffer + framesToCopy, samples.Length * 2)));

        for (int i = 0; i < framesToCopy; i++)
          samples[samplesInBuffer + i] = new Complex32(buffer[2 * i], buffer[2 * i + 1]);

        samplesInBuffer += framesToCopy;
      }

      return samples;
    }

    private void SaveRecordingEvents(string recordingFileName)
    {
      recordingEvents.Save(GetRecordingEventsFileName(recordingFileName));
    }

    private void LoadRecordingEvents(string recordingFileName)
    {
      recordingEvents = RecordingEvents.Load(GetRecordingEventsFileName(recordingFileName));
    }

    // Stream to the encoder in small blocks. The old exporter allocated a
    // second 345/691 MB buffer for WAV (and more for MP3), causing long
    // recording saves to stall the UI or run out of managed heap.
    private void SaveRecordingAsWav(string fileName)
    {
      float[]? audio;
      Complex32[]? iq;
      int count;
      lock (recordingLock)
      {
        audio = audioBuffer;
        iq = iqBuffer;
        count = samplesInBuffer;
      }
      if (count <= 0 || (audio == null && iq == null))
        throw new InvalidOperationException("No recorded samples to save");

      const int chunkFrames = 4096;
      int channels = audio != null ? 1 : 2;
      WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(
        SdrConst.AUDIO_SAMPLING_RATE, channels);
      using var writer = new WaveFileWriter(fileName, format);
      var bytes = new byte[chunkFrames * channels * sizeof(float)];
      float[] iqInterleaved = iq == null ? Array.Empty<float>() :
        new float[chunkFrames * 2];

      for (int pos = 0; pos < count; pos += chunkFrames)
      {
        int frames = Math.Min(chunkFrames, count - pos);
        if (audio != null)
          Buffer.BlockCopy(audio, pos * sizeof(float),
            bytes, 0, frames * sizeof(float));
        else
        {
          for (int i = 0; i < frames; i++)
          {
            iqInterleaved[2 * i] = iq![pos + i].Real;
            iqInterleaved[2 * i + 1] = iq[pos + i].Imaginary;
          }
          Buffer.BlockCopy(iqInterleaved, 0,
            bytes, 0, frames * 2 * sizeof(float));
        }
        writer.Write(bytes, 0, frames * channels * sizeof(float));
      }
    }

    internal sealed class RecordingPcm16WaveProvider : IWaveProvider
    {
      private readonly float[] samples;
      private readonly int sampleCount;
      private int position;

      public WaveFormat WaveFormat { get; } =
        new WaveFormat(SdrConst.AUDIO_SAMPLING_RATE, 16, 1);

      public RecordingPcm16WaveProvider(float[] samples, int sampleCount)
      {
        this.samples = samples;
        this.sampleCount = sampleCount;
      }

      public int Read(byte[] buffer, int offset, int count)
      {
        int frames = Math.Min(count / sizeof(short), sampleCount - position);
        for (int i = 0; i < frames; i++)
        {
          float sample = Math.Clamp(samples[position + i], -1f, 1f);
          short pcm = (short)(sample * short.MaxValue);
          int index = offset + i * sizeof(short);
          buffer[index] = (byte)pcm;
          buffer[index + 1] = (byte)(pcm >> 8);
        }
        position += frames;
        return frames * sizeof(short);
      }
    }

    private void SaveRecordingAsMp3(string filename)
    {
      float[]? audio;
      int count;
      lock (recordingLock)
      {
        audio = audioBuffer;
        count = samplesInBuffer;
      }

      if (audio == null || count <= 0)
        throw new InvalidOperationException("No audio samples for MP3 export");

      const int targetSampleRate = 16000;
      const int targetBitrate = 24000;

      // Convert float samples into PCM16 incrementally, then resample and
      // encode through Media Foundation without materializing the whole file.
      var input = new RecordingPcm16WaveProvider(audio, count);
      using var resampled = new MediaFoundationResampler(input, targetSampleRate)
      {
        ResamplerQuality = 60
      };
      MediaFoundationEncoder.EncodeToMp3(resampled, filename, targetBitrate);
    }

  }
}
