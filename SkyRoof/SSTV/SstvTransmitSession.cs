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
    private int ownPttRequested;
    private bool disposed;

    public bool IsSending => Volatile.Read(ref sending) != 0;
    public SstvTransmitSession(Context ctx) => this.ctx = ctx;

    public void RequestStop()
    {
      stop?.Cancel();
      // Never release a PTT lease asserted by another SkyRoof module.
      if (Volatile.Read(ref ownPttRequested) != 0)
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
      if (cat == null || !cat.Ptt ||
          !cat.PttMayBeOwnedByApplication ||
          link.UplinkMode != guard.Mode ||
          ctx.CwTransmit?.TxCatWritesFrozen == true)
        return false;
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
      bool unsuccessful = false;
      bool audioFinished = false;
      DateTime pttRequestUtc = default;
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
        pttRequestUtc = requestedUtc;
        // Publish ownership intent before issuing the asynchronous CAT write
        // so STOP can cancel a still-pending PTT-ON request.
        Interlocked.Exchange(ref ownPttRequested, 1);
        pttRequested = true;
        ctx.FrequencyControl.SetPtt(true);
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
        audioFinished = true;
      }
      catch
      {
        unsuccessful = true;
        throw;
      }
      finally
      {
        // A successful OFF command ACK is NOT independent PTT readback.
        // Do not report completion while the transceiver may still be
        // transmitting. Cancellation of the picture must not cancel this
        // short safety wait.
        bool confirmedOff = !pttRequested;
        try
        {
          if (pttRequested)
          {
            ctx.FrequencyControl.ReleaseApplicationPtt();
            var radio = ctx.CatControl.Tx;
            for (int attempt = 0; attempt < 40; attempt++)
            {
              if (radio != null &&
                  SstvTxPolicy.HasConfirmedPttOff(
                    radio.Ptt, radio.PttMayBeOwnedByApplication,
                    radio.LastPttHardwareReadbackUtc, pttRequestUtc,
                    DateTime.UtcNow))
              {
                confirmedOff = true;
                break;
              }
              await Task.Delay(100); // release watchdog is not user-cancelable
            }
          }
        }
        finally
        {
          Interlocked.Exchange(ref ownPttRequested, 0);
          stop = null;
          local.Dispose();
          Interlocked.Exchange(ref sending, 0);
        }
        if (!confirmedOff)
        {
          status?.Invoke("无法确认 PTT 已解除，请立即检查电台！");
          // Preserve the primary transmission exception when one exists.
          if (!unsuccessful)
            throw new IOException("未收到新的 CAT PTT OFF 硬件读回，需人工确认电台停止发射。");
        }
        else if (!unsuccessful && audioFinished)
          status?.Invoke("SSTV 发送完成，PTT 已确认解除");
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
