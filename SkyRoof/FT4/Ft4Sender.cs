using System.Diagnostics;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using VE3NEA;

namespace SkyRoof
{
  // kind of sender's work
  public enum SenderMode { Off, Tuning, Sending }
  
  // stages of sending
  public enum SendingStage { Idle, Scheduled, Sending }

  public class Ft4Sender : IDisposable
  {  
    public const int PttOnMargin = 150; // milliseconds
    public const int PttOffMargin = 300;
    private const double PttOnSeconds = PttOnMargin * 1e-3;
    private const int PttOnSampleCount = (int)(PttOnSeconds * NativeFT4Coder.SAMPLING_RATE);
    private const int RampSampleCount = NativeFT4Coder.SAMPLES_PER_SYMBOL;
    private const double RampSeconds = RampSampleCount / (double)NativeFT4Coder.SAMPLING_RATE;
    private const int LeadSampleCount = NativeFT4Coder.SAMPLING_RATE;
    private const int WaveformSampleCount = NativeFT4Coder.ENCODE_SAMPLE_COUNT;

    private byte[] MessageChars = new byte[NativeFT4Coder.ENCODE_MESSAGE_LENGTH + 1];
    private float[] NewWaveform = new float[WaveformSampleCount];
    private float[] Waveform = new float[WaveformSampleCount];
    private float[] Ramp = new float[RampSampleCount];
    private readonly float[] TxBuffer = new float[LeadSampleCount];
    private readonly float[] Silence = new float[LeadSampleCount];

    private int txAudioFrequency = NativeFT4Coder.DEFAULT_AUDIO_FREQUENCY;
    private object lockObj = new();
    private Thread? WorkerThread;
    private bool Stopping;
    public Ft4Slot Slot = new();
    private int SampleIndex = 0;



    public OutputSoundcard<float> Soundcard = new();
    public SenderMode Mode { get; private set; } = SenderMode.Off;

    public event EventHandler? BeforeTransmit;
    public event EventHandler? AfterTransmit;

    public int TxAudioFrequency { get => txAudioFrequency; set => SetTxAudioFrequency(value); }

    private bool xitEnabled = true;
    public bool XitEnabled { get => xitEnabled; set => SetXitEnabled(value); }
    public int XitOffset { get; private set; }
    public void StartTuning() => SetMode(SenderMode.Tuning);
    public void StartSending() => SetMode(SenderMode.Sending);
    public void Stop() => SetMode(SenderMode.Off);
    public bool TxOdd;


    public SendingStage SenderPhase = SendingStage.Idle;

    public Ft4Sender()
    {
      GenerateRamp();
    }

    private void SetTxAudioFrequency(int value)
    {
      txAudioFrequency = value;
      ComputeXitOffset();
    }

    private void SetXitEnabled(bool value)
    {
      xitEnabled = value;
      ComputeXitOffset();
    }

    private void ComputeXitOffset()
    {
      if (XitEnabled)
      {
        int hz = TxAudioFrequency % 1000;
        int wholeKhz = TxAudioFrequency - hz;
        XitOffset = wholeKhz - 1000;
        // transmitter offset (XIT) is used to keep the audio frequency in this range,
        // to ensure that 3-rd harmonic is outside of the passband.
        int frequency = TxAudioFrequency - XitOffset;
        Debug.Assert(frequency >= 1000 && frequency < 2000);
      }
      else
        XitOffset = 0;
    }

    private void GenerateRamp()
    {
      for (int i = 0; i < RampSampleCount; i++) 
        Ramp[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / RampSampleCount));
    }

    public void SetMessage(string message)
    {
      // generate samples
      int len = Math.Min(message.Length, MessageChars.Length - 1);
      Array.Clear(MessageChars);
      Encoding.ASCII.GetBytes(message, 0, len, MessageChars, 0);
      float frequency = TxAudioFrequency - XitOffset;
      NativeFT4Coder.encode_ft4(MessageChars, ref frequency, NewWaveform);

      lock (lockObj)
      {
        if (SenderPhase != SendingStage.Idle)
          Soundcard.Buffer.ExecuteLocked(() =>
            {
              if (SampleIndex > RampSampleCount && SampleIndex < WaveformSampleCount - RampSampleCount)
              {
                // remove old data from soundcard's buffer
                SampleIndex -= Soundcard.Buffer.Count;
                Soundcard.Buffer.Clear();

                // if silence was removed, put it back
                if (SampleIndex < 0)
                {
                  Soundcard.Buffer.Write(Silence, 0, -SampleIndex);
                  SampleIndex = 0;
                }

                // ramp
                for (int i = 0; i < RampSampleCount; i++)
                  TxBuffer[i] = Waveform[SampleIndex + i] * Ramp[RampSampleCount - 1 - i] + NewWaveform[SampleIndex + i] * Ramp[i];
                Soundcard.Buffer.Write(TxBuffer, 0, RampSampleCount);
                SampleIndex += RampSampleCount;

                // top up with new waveform
                Array.Copy(NewWaveform, Waveform, WaveformSampleCount);
                TopUp(NewWaveform);
              }
            });

        Array.Copy(NewWaveform, Waveform, WaveformSampleCount);
      }
    }

    public void SetMode(SenderMode newMode)
    {
      if (Mode == newMode) return;

      SenderMode oldMode = Mode;
      Mode = newMode;

      // stopping audio
      if (newMode == SenderMode.Off)
      {
        lock (lockObj) { Stopping = true; }
        Stopping = true;
        WorkerThread!.Join();
        Soundcard.Enabled = false;
      }

      // starting audio
      else if (oldMode == SenderMode.Off)
      {
        Soundcard.Enabled = true;

        if (!Soundcard.IsRunning)
        {
          MessageBox.Show("Unable to open the soundcard for output.", "SkyRoof",
            MessageBoxButtons.OK, MessageBoxIcon.Error);

          Mode = SenderMode.Off;
          return;
        }

        Stopping = false;
        if (newMode == SenderMode.Tuning)
          WorkerThread = new(TuneThreadProcedure);
        else
          WorkerThread = new(SendThreadProcedure);

        WorkerThread.IsBackground = true;
        WorkerThread.Name = GetType().Name;
        WorkerThread.Priority = ThreadPriority.Highest;
        WorkerThread.Start();
      }

      // no switching between Tuning and Sending
      else
      {
        Console.Beep();
        Mode = oldMode;
      }
    }

    private void BeginTransmit(ref bool pttActive)
    {
      pttActive = true;
      BeforeTransmit?.Invoke(this, EventArgs.Empty);
    }

    private void EndTransmit(ref bool pttActive)
    {
      if (!pttActive) return;
      try { AfterTransmit?.Invoke(this, EventArgs.Empty); }
      finally { pttActive = false; }
    }

    private void EndTransmitNoThrow(ref bool pttActive)
    {
      if (!pttActive) return;
      try { EndTransmit(ref pttActive); }
      catch (Exception ex)
      {
        Log.Error(ex, "FT4 release callback failed");
        pttActive = false;
      }
    }

    private void TuneThreadProcedure()
    {
      bool pttActive = false;
      try
      {
        int samplesNeeded = 0;
        double sinePhase = 0;
        double phaseInc = 0;

        BeginTransmit(ref pttActive);
        Thread.Sleep(PttOnMargin);

        while (!Stopping)
        {
          Thread.Sleep(50);
          samplesNeeded = Math.Max(0, LeadSampleCount - Soundcard.Buffer.Count);
          if (samplesNeeded <= 0) continue;

          phaseInc = 2.0 * Math.PI * (TxAudioFrequency - XitOffset) / NativeFT4Coder.SAMPLING_RATE;
          for (int i = 0; i < samplesNeeded; i++)
          {
            TxBuffer[i] = (float)Math.Sin(sinePhase);
            sinePhase += phaseInc;
            if (sinePhase > 2 * Math.PI) sinePhase -= 2 * Math.PI;
          }

          Soundcard.AddSamples(TxBuffer, 0, samplesNeeded);
        }

        Soundcard.Buffer.Clear();
        Thread.Sleep(PttOffMargin);
        EndTransmit(ref pttActive);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "FT4 tuning worker failed");
      }
      finally
      {
        try { Soundcard.Buffer.Clear(); } catch { }
        EndTransmitNoThrow(ref pttActive);
      }
    }

    private void SendThreadProcedure()
    {
      int sampleCount;
      DateTime now;
      SenderPhase = SendingStage.Idle;
      Soundcard.Buffer.Clear();

      while (!Stopping)
      {
        now = DateTime.UtcNow;
        Slot.Utc = now;
        var startTime = Slot.GetTxStartTime(TxOdd);

        switch (SenderPhase)
        {
          case SendingStage.Idle:
            sampleCount = (int)((startTime - now).TotalSeconds * NativeFT4Coder.SAMPLING_RATE);

            // time to schedule
            if (now < startTime && sampleCount < LeadSampleCount)
            {
              Soundcard.AddSamples(Silence, 0, sampleCount);
              Soundcard.AddSamples(Silence, 0, PttOnSampleCount);
              SampleIndex = 0;
              TopUp();
              SenderPhase = SendingStage.Scheduled;
            }

            // starting in the middle of transmission time
            else if (now > startTime)
            {
              double missedSeconds = (now - startTime).TotalSeconds + PttOnSeconds;
              StartInMiddle(missedSeconds);
            }
            break;

          case SendingStage.Scheduled:
            // keep buffer full
            TopUp();

            // scheduled transmission starts now
            if (now > startTime)
            {
              SenderPhase = SendingStage.Sending;
              BeforeTransmit?.Invoke(this, EventArgs.Empty);
            }
            break;

          case SendingStage.Sending:
            // keep buffer full
            if (SampleIndex < WaveformSampleCount) TopUp();

            // all samples have been sent, switch to idle
            else if (Soundcard.Buffer.Count == 0)
            {
              SenderPhase = SendingStage.Idle;
              Thread.Sleep(PttOffMargin);
              AfterTransmit?.Invoke(this, EventArgs.Empty);
            }

            // Odd/Even changed, stop sending
            if (SampleIndex < WaveformSampleCount && startTime > now) RampDown();

            break;
        }

        Thread.Sleep(10);
      }

      // aborted, stop audio and fire event
      if (SenderPhase == SendingStage.Sending)
      {
        RampDown();
        SenderPhase = SendingStage.Idle;
        Thread.Sleep(PttOffMargin);
        AfterTransmit?.Invoke(this, EventArgs.Empty);
      }

      Soundcard.Buffer.Clear();
    }

    private void StartInMiddle(double missedSeconds)
    {
      {
        if (missedSeconds < NativeFT4Coder.ENCODE_SECONDS - PttOnSeconds - RampSeconds)
        {
          // silence for PttOnMargin 
          Soundcard.Buffer.Clear();
          int sampleCount = (int)(PttOnSeconds * NativeFT4Coder.SAMPLING_RATE);
          Soundcard.AddSamples(Silence, 0, sampleCount);

          // current position in waveform
          SampleIndex = (int)(missedSeconds * NativeFT4Coder.SAMPLING_RATE);

          // ramped waveform
          for (int i = 0; i < RampSampleCount; i++) TxBuffer[i] = Waveform[SampleIndex + i] * Ramp[i];
          Soundcard.Buffer.Write(TxBuffer, 0, RampSampleCount);
          SampleIndex += RampSampleCount;

          // waveform
          TopUp();

          // PTT switch
          SenderPhase = SendingStage.Sending;
          BeforeTransmit?.Invoke(this, EventArgs.Empty);
          Thread.Sleep(PttOnMargin);
        }
      }

    }

    void RampDown()
    {
      Soundcard.Buffer.ExecuteLocked(() =>
      {
        SampleIndex -= Soundcard.Buffer.Count;
        Soundcard.Buffer.Clear();
        if (SampleIndex >= 0 && SampleIndex + RampSampleCount <= WaveformSampleCount)
        {
          for (int i = 0; i < RampSampleCount; i++) TxBuffer[i] = Waveform[SampleIndex + i] * Ramp[RampSampleCount - 1 - i];
          Soundcard.Buffer.Write(TxBuffer, 0, RampSampleCount);
        }
      });

      SampleIndex = WaveformSampleCount;
    }

    private void TopUp(float[]? buffer = null)
    {
      int sampleCount = LeadSampleCount - Soundcard.Buffer.Count;
      if (sampleCount < 0) return;

      sampleCount = Math.Min(WaveformSampleCount - SampleIndex, sampleCount);
      Soundcard.AddSamples(buffer ?? Waveform, SampleIndex, sampleCount);
      SampleIndex += sampleCount;
    }

    public void Dispose()
    {
      Stop();
      Soundcard.Dispose();
    }
  }
}
