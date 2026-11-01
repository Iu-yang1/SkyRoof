using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using Newtonsoft.Json;
using Serilog;
using SkyRoof.IF;
using VE3NEA;

namespace SkyRoof.IF
{
  /// <summary>Writes the original received 48 kS/s mono float PCM in its own
  /// task. WAV and per-block timestamps are generated from exactly the same
  /// queued frames. All disk I/O happens off WASAPI callback threads.</summary>
  public sealed class IfAfWavTrack : IDisposable
  {
    private readonly Channel<AudioFrame> frames =
      Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(96)
      {
        SingleReader = true, SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
      });
    private readonly Task worker;
    private readonly record struct AudioFrame(float[] Pcm, DateTime Utc);
    private long samples, dropped, firstTicks, lastTicks;
    private string? writeError;
    private int complete;
    public string WavPath { get; }
    public string TimePath { get; }
    public long WrittenSamples => Interlocked.Read(ref samples);
    public long DroppedBlocks => Interlocked.Read(ref dropped);
    public DateTime? FirstBlockUtc => Interlocked.Read(ref firstTicks) == 0
      ? null : new DateTime(Interlocked.Read(ref firstTicks), DateTimeKind.Utc);
    public DateTime? LastBlockUtc => Interlocked.Read(ref lastTicks) == 0
      ? null : new DateTime(Interlocked.Read(ref lastTicks), DateTimeKind.Utc);
    public string? Error => writeError;

    public IfAfWavTrack(string folder, string stem)
    {
      if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(stem) ||
          stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new ArgumentException("Invalid recording destination");
      Directory.CreateDirectory(folder);
      WavPath = Path.Combine(folder, stem + ".wav");
      TimePath = Path.Combine(folder, stem + ".csv");
      worker = Task.Run(WriteAsync);
    }

    public bool TryAppend(ReadOnlySpan<float> pcm, DateTime blockUtc)
    {
      if (Volatile.Read(ref complete) != 0 || pcm.IsEmpty) return false;
      var copy = pcm.ToArray();
      if (frames.Writer.TryWrite(new AudioFrame(copy, blockUtc)))
        return true;
      Interlocked.Increment(ref dropped);
      return false;
    }

    private async Task WriteAsync()
    {
      try
      {
        using var wav = new WaveFileWriter(WavPath,
          WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var csv = new StreamWriter(TimePath, false, new UTF8Encoding(false));
        await csv.WriteLineAsync("sample_offset,sample_count,callback_utc");
        await foreach (var frame in frames.Reader.ReadAllAsync())
        {
          long offset = Interlocked.Read(ref samples);
          wav.WriteSamples(frame.Pcm, 0, frame.Pcm.Length);
          await csv.WriteLineAsync(
            $"{offset},{frame.Pcm.Length},{frame.Utc.ToUniversalTime():O}");
          Interlocked.CompareExchange(ref firstTicks, frame.Utc.ToUniversalTime().Ticks, 0);
          Interlocked.Exchange(ref lastTicks, frame.Utc.ToUniversalTime().Ticks);
          Interlocked.Add(ref samples, frame.Pcm.Length);
        }
        wav.Flush();
        await csv.FlushAsync();
      }
      catch (Exception ex)
      {
        writeError = ex.ToString();
        Log.Error(ex, "Dual IF/AF WAV recording writer failed: {Path}", WavPath);
        // The consumer failed. Producers will count subsequent drops rather
        // than accumulating an unbounded queue or blocking capture.
        frames.Writer.TryComplete(ex);
      }
    }

    public async Task CompleteAsync()
    {
      if (Interlocked.Exchange(ref complete, 1) == 0)
        frames.Writer.TryComplete();
      await worker.ConfigureAwait(false);
    }

    public void Dispose() => CompleteAsync().GetAwaiter().GetResult();
  }

  public sealed record IfAfCaptureSummary(
    string SessionFolder,
    string IfWavPath, string AfWavPath, string MetadataPath,
    long IfSamples, long AfSamples, long IfDroppedBlocks, long AfDroppedBlocks,
    DateTime? IfFirstUtc, DateTime? AfFirstUtc,
    string? IfError, string? AfError);

  /// <summary>Capture test: identical RF pass, radio USB 12-kHz IF and its
  /// internal LAN AF output. NOT a loopback of SkyRoof's demodulated VAC.
  /// Callback UTC is host receive time, NOT hardware sample-clock time.</summary>
  public sealed class IfAfComparisonRecorder : IDisposable
  {
    private InputSoundcard<float>? localIf;
    private LoopbackInputSoundcard? originalAf;
    private IfAfService? shared;
    private IfAfWavTrack? ifTrack, afTrack;
    private int active;
    private bool disposed;
    private DateTime startUtc;
    private string? folder, inputDevice, outputDevice, inputName, outputName;
    private AudioChannelChoice channel;
    private bool reused;
    private string mode = "";
    private double downlinkHz;
    private string notes = "";
    public bool IsRecording => Volatile.Read(ref active) != 0;
    public long IfSamples => ifTrack?.WrittenSamples ?? 0;
    public long AfSamples => afTrack?.WrittenSamples ?? 0;
    public long DroppedBlocks =>
      (ifTrack?.DroppedBlocks ?? 0) + (afTrack?.DroppedBlocks ?? 0);
    public string? SessionFolder => folder;

    public void Start(string ifDevice, string afPlaybackDevice,
      AudioChannelChoice ifChannel, string parentFolder, IfAfService? hub,
      string radioMode, double frequencyHz, string operatorNotes)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (IsRecording) throw new InvalidOperationException("Already recording");
      if (string.IsNullOrWhiteSpace(ifDevice) ||
          string.IsNullOrWhiteSpace(afPlaybackDevice))
        throw new ArgumentException("Choose both USB IF capture and LAN AF playback endpoints.");
      if (string.IsNullOrWhiteSpace(parentFolder))
        throw new ArgumentException("Choose a destination folder.");

      var ifEntry = Array.Find(Soundcard.ListDevices(NAudio.CoreAudioApi.DataFlow.Capture),
        d => d.Id == ifDevice);
      var afEntry = Array.Find(Soundcard.ListDevices(NAudio.CoreAudioApi.DataFlow.Render),
        d => d.Id == afPlaybackDevice);
      if (ifEntry == null || afEntry == null)
        throw new InvalidOperationException("Selected Windows audio endpoint is unavailable.");
      if (!ifEntry.Name.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase) &&
          !ifEntry.Name.Contains("ICOM", StringComparison.OrdinalIgnoreCase))
        Log.Warning("Dual capture: IF device {Device} is not identified as an IC-9700 USB endpoint", ifEntry.Name);
      if (hub?.IsRunning == true &&
          string.Equals(hub.InputDeviceId, ifDevice, StringComparison.Ordinal) &&
          ifChannel != AudioChannelChoice.Left)
        throw new InvalidOperationException(
          "Shared IF-AF uses the Left channel. Choose Left for recording, or disable IF-AF and capture another channel directly.");

      startUtc = DateTime.UtcNow;
      folder = Path.Combine(parentFolder, "IF-AF_" +
        startUtc.ToString("yyyyMMddTHHmmssfffZ") + "_" +
        Guid.NewGuid().ToString("N")[..6]);
      inputDevice = ifDevice;
      outputDevice = afPlaybackDevice;
      inputName = ifEntry.Name;
      outputName = afEntry.Name;
      channel = ifChannel;
      mode = radioMode;
      downlinkHz = frequencyHz;
      notes = operatorNotes;
      reused = hub?.IsRunning == true &&
        string.Equals(hub.InputDeviceId, ifDevice, StringComparison.Ordinal);
      Directory.CreateDirectory(folder);
      ifTrack = new IfAfWavTrack(folder, "ic9700_usb_12khz_if");
      afTrack = new IfAfWavTrack(folder, "ic9700_lan_original_af");
      try
      {
        originalAf = new LoopbackInputSoundcard(afPlaybackDevice, 48000);
        originalAf.SamplesAvailable += OnAf;
        // Start LAN AF first, then IF. All received blocks keep separate UTC
        // callback timestamps for later alignment and clock-drift estimation.
        Volatile.Write(ref active, 1);
        originalAf.Enabled = true;
        if (!originalAf.IsRunning)
          throw new InvalidOperationException("LAN AF loopback failed to start.");
        if (reused)
        {
          shared = hub;
          shared!.RawIfAvailable += OnIf;
        }
        else
        {
          localIf = new InputSoundcard<float>(ifDevice, 48000,
            captureBufferMilliseconds: 40, readerBlockSizeSamples: 1600,
            channelChoice: ifChannel);
          localIf.SamplesAvailable += OnIf;
          localIf.Enabled = true;
          if (!localIf.IsRunning)
            throw new InvalidOperationException("USB IF capture failed to start.");
        }
      }
      catch
      {
        StopAsync().GetAwaiter().GetResult();
        throw;
      }
    }

    private void OnIf(object? _, DataEventArgs<float> e)
    {
      if (!IsRecording) return;
      ifTrack?.TryAppend(e.Data.AsSpan(0, Math.Min(e.Count, e.Data.Length)), e.Utc);
    }
    private void OnAf(object? _, DataEventArgs<float> e)
    {
      if (!IsRecording) return;
      afTrack?.TryAppend(e.Data.AsSpan(0, Math.Min(e.Count, e.Data.Length)), e.Utc);
    }

    public async Task<IfAfCaptureSummary?> StopAsync()
    {
      if (Interlocked.Exchange(ref active, 0) == 0 && ifTrack == null)
        return null;
      if (shared != null) shared.RawIfAvailable -= OnIf;
      shared = null;
      if (localIf != null)
      {
        localIf.SamplesAvailable -= OnIf;
        localIf.Enabled = false;
        localIf.Dispose();
        localIf = null;
      }
      if (originalAf != null)
      {
        originalAf.SamplesAvailable -= OnAf;
        originalAf.Enabled = false;
        originalAf.Dispose();
        originalAf = null;
      }
      var first = ifTrack;
      var second = afTrack;
      ifTrack = afTrack = null;
      if (first == null || second == null) return null;
      await Task.WhenAll(first.CompleteAsync(), second.CompleteAsync())
        .ConfigureAwait(false);
      DateTime endUtc = DateTime.UtcNow;
      var record = new
      {
        Schema = "skyroof.if-af-comparison.v1",
        StartUtc = startUtc, EndUtc = endUtc,
        Radio = "IC-9700", Mode = mode, CorrectedDownlinkHz = downlinkHz,
        IfSource = new
        {
          EndpointId = inputDevice, EndpointName = inputName,
          Channel = channel.ToString(), SharedWithIfAf = reused,
          Kind = "USB real 12-kHz IF, selected mono PCM after optional Windows resampling",
          Rate = 48000, Channels = 1, Encoding = "IEEE float32",
          File = Path.GetFileName(first.WavPath),
          Timestamps = Path.GetFileName(first.TimePath),
          Samples = first.WrittenSamples, DroppedBlocks = first.DroppedBlocks,
          FirstCallbackUtc = first.FirstBlockUtc, LastCallbackUtc = first.LastBlockUtc,
          Error = first.Error
        },
        AfSource = new
        {
          EndpointId = outputDevice, EndpointName = outputName,
          Kind = "WASAPI loopback, ENTIRE render endpoint mix (not application-isolated)",
          Rate = 48000, Channels = 1, Encoding = "IEEE float32",
          File = Path.GetFileName(second.WavPath),
          Timestamps = Path.GetFileName(second.TimePath),
          Samples = second.WrittenSamples, DroppedBlocks = second.DroppedBlocks,
          FirstCallbackUtc = second.FirstBlockUtc, LastCallbackUtc = second.LastBlockUtc,
          Error = second.Error
        },
        Notes = notes,
        Limitations = new[]
        {
          "WASAPI host callback timestamps are not hardware-synchronized sample timestamps.",
          "LAN AF loopback captures all audio mixed into its selected playback endpoint.",
          "WAV contains mono 48-kHz float samples after host mixer/resampler; not raw USB packet bytes.",
          "When no LAN audio is played, WASAPI loopback may omit silent frames.",
          "The first and last valid blocks may start/stop at different UTC instants.",
          "No assumption of equal sample clocks or zero drift: align and resample offline."
        }
      };
      var metadataPath = Path.Combine(folder!, "capture.json");
      await File.WriteAllTextAsync(metadataPath,
        JsonConvert.SerializeObject(record, Formatting.Indented))
        .ConfigureAwait(false);
      var result = new IfAfCaptureSummary(folder!, first.WavPath,
        second.WavPath, metadataPath, first.WrittenSamples,
        second.WrittenSamples, first.DroppedBlocks, second.DroppedBlocks,
        first.FirstBlockUtc, second.FirstBlockUtc, first.Error, second.Error);
      first.Dispose();
      second.Dispose();
      return result;
    }

    public void Dispose()
    {
      if (disposed) return;
      StopAsync().GetAwaiter().GetResult();
      disposed = true;
    }
  }
}
