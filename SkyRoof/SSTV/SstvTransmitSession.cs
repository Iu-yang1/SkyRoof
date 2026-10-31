using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Manually triggered SSTV WAV transmission on a dedicated WASAPI endpoint.
  /// No automatic CQ. Radio CAT writer remains the existing SkyRoof CAT engine;
  /// cancellation and every fault path request an ownership-aware PTT release.
  /// Real RF tests are required before treating this as production-ready.
  /// </summary>
  public sealed class SstvTransmitSession : IDisposable
  {
    private readonly Context ctx;
    private CancellationTokenSource? stop;
    private int sending;
    private bool disposed;

    public bool IsSending => Volatile.Read(ref sending) != 0;
    public SstvTransmitSession(Context ctx) => this.ctx = ctx;

    public void RequestStop()
    {
      stop?.Cancel();
      // This request only releases SkyRoof-owned PTT; a second independent
      // controller's transmission must never be forcibly unkeyed.
      ctx.FrequencyControl?.ReleaseApplicationPtt();
    }

    private (SstvTxGuard Guard, double LoHz) CaptureGuard(bool approved)
    {
      RadioLink link = ctx.FrequencyControl.RadioLink;
      var cat = ctx.CatControl.Tx;
      double loHz = 0;
      if (ctx.Settings.Transverter.TxCatOffsetEnabled)
      {
        var band = ctx.Settings.Transverter.GetCatBand(
          link.CorrectedUplinkFrequency);
        if (band == null)
          throw new InvalidOperationException(
            "选定上行频率无法映射到发射机 CAT 频段。");
        loHz = band.LoOffset;
      }

      SstvTxGuard guard = SstvTxPolicy.Validate(
        link.Sat?.sat_id, link.Tx?.uuid,
        link.IsTerrestrial, link.HasUplink, link.HasObservation,
        link.IsAboveHorizon, link.UplinkMode, link.CorrectedUplinkFrequency,
        loHz, cat?.LastReadTxFrequency ?? 0,
        cat?.CanPtt() == true,
        cat?.Ptt == true || ctx.CatControl.Rx?.Ptt == true,
        ctx.CwTransmit?.TxCatWritesFrozen == true,
        approved, DateTime.UtcNow);
      return (guard, loHz);
    }

    private bool CheckLiveGuard(SstvTxGuard guard)
    {
      var link = ctx.FrequencyControl.RadioLink;
      var cat = ctx.CatControl.Tx;
      if (cat == null || !cat.Ptt) return false;
      double lo = 0;
      if (ctx.Settings.Transverter.TxCatOffsetEnabled)
      {
        var band = ctx.Settings.Transverter.GetCatBand(
          link.CorrectedUplinkFrequency);
        if (band == null) return false;
        lo = band.LoOffset;
      }
      return SstvTxPolicy.StillSafe(
        guard, link.Sat?.sat_id, link.Tx?.uuid,
        link.HasObservation, link.IsAboveHorizon,
        link.CorrectedUplinkFrequency, lo,
        cat.LastReadTxFrequency, cat.Ptt,
        cat.LastPttHardwareReadbackUtc, DateTime.UtcNow);
    }

    public async Task SendWavAsync(
      string wavPath, string outputDeviceId, bool approved,
      Action<string>? status = null, CancellationToken cancellationToken = default)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (Interlocked.CompareExchange(ref sending, 1, 0) != 0)
        throw new InvalidOperationException("已有 SSTV 图片正在发送。");
      CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
      stop = local;
      bool pttRequested = false;
      try
      {
        if (string.IsNullOrWhiteSpace(wavPath) ||
            string.IsNullOrWhiteSpace(outputDeviceId))
          throw new InvalidOperationException("请选择有效的音频文件和发射声卡。");
        var (guard, _) = CaptureGuard(approved);

        using var reader = new WaveFileReader(wavPath);
        if (reader.WaveFormat.SampleRate != 48000 ||
            reader.WaveFormat.Channels != 1 ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.TotalTime.TotalSeconds < 1 ||
            reader.TotalTime.TotalSeconds > SstvTxPolicy.MaximumAudioDurationSeconds)
          throw new InvalidOperationException("SSTV 音频格式或发送时长不符合要求。");

        // Pre-initialize WASAPI (device, format, shared mode) BEFORE PTT.
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(outputDeviceId);
        if (endpoint.State != DeviceState.Active ||
            endpoint.DataFlow != DataFlow.Render)
          throw new InvalidOperationException("选择的设备不是可用的发射音频输出。");

        using var player = new WasapiOut(
          endpoint, AudioClientShareMode.Shared, false, 100);
        var volume = new VolumeSampleProvider(reader.ToSampleProvider())
        {
          Volume = 0.35f
        };
        player.Init(new SampleToWaveProvider(volume));

        var playbackCompleted = new TaskCompletionSource<Exception?>(
          TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
          playbackCompleted.TrySetResult(e.Exception);

        status?.Invoke("正在确认电台状态...");
        if (local.IsCancellationRequested) local.Token.ThrowIfCancellationRequested();

        var cat = ctx.CatControl.Tx!;
        DateTime requestedUtc = DateTime.UtcNow;
        ctx.FrequencyControl.SetPtt(true);
        pttRequested = true;
        bool acknowledged = false;
        for (int attempt = 0; attempt < 35; attempt++)
        {
          await Task.Delay(100, local.Token);
          if (cat.Ptt &&
              cat.LastPttHardwareReadbackUtc >= requestedUtc &&
              CheckLiveGuard(guard))
          {
            acknowledged = true;
            break;
          }
        }
        if (!acknowledged)
          throw new InvalidOperationException(
            "CAT 未能确认发射状态、频率或 PTT 读回；发送已取消。");

        // Allow the TX audio path and linear satellite relay to settle.
        await Task.Delay(250, local.Token);
        if (!CheckLiveGuard(guard))
          throw new InvalidOperationException("上行电台状态发生变化，发送已取消。");

        status?.Invoke("正在发送 SSTV 图片...");
        player.Play();
        DateTime deadline = DateTime.UtcNow.AddSeconds(
          Math.Min(SstvTxPolicy.MaximumAudioDurationSeconds,
            reader.TotalTime.TotalSeconds + 5));
        while (true)
        {
          local.Token.ThrowIfCancellationRequested();
          if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("SSTV 发射超时，已停止。");
          if (!CheckLiveGuard(guard))
            throw new InvalidOperationException(
              "卫星或发射机状态变化，已紧急停止发射。");
          Task done = await Task.WhenAny(playbackCompleted.Task,
            Task.Delay(120, local.Token));
          if (done == playbackCompleted.Task)
          {
            Exception? failure = await playbackCompleted.Task;
            if (failure != null) throw failure;
            if (reader.Position < reader.Length - 2048)
              throw new IOException("音频输出提前停止。");
            break;
          }
        }
        player.Stop();
        status?.Invoke("SSTV 音频发送结束");
      }
      finally
      {
        // Stop/close/cancellation/CAT errors all converge on the same
        // ownership-aware release. Never leave the rig keyed on exception.
        if (pttRequested)
          ctx.FrequencyControl.ReleaseApplicationPtt();
        stop = null;
        local.Dispose();
        Interlocked.Exchange(ref sending, 0);
      }
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      RequestStop();
    }
  }
}
