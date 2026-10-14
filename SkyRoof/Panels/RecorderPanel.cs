using MathNet.Numerics;
using MultimediaTimer;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using VE3NEA;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public partial class RecorderPanel : DockContent
  {
    private readonly Context ctx = null!;
    private const int PLAYBACK_TIMER_INTERVAL_MS = 20;
    private const int PLAYBACK_UI_INTERVAL_MS = 100;
    private System.Windows.Forms.Timer? recordingTimer;
    private System.Windows.Forms.Timer? playbackUiTimer;
    private MultimediaTimer.Timer? playbackTimer;
    private RecordingManager recordingManager = null!;
    private DateTime RecordingStart;
    private readonly ToolStripMenuItem audioSourceMenu = new("Audio Source");

    public RecordingEvents RecordingEvents => recordingManager.RecordingEvents;
    internal bool isPlayingBack => recordingManager?.IsPlayingBack == true;

    //----------------------------------------------------------------------------------------------
    //                                        startup
    //----------------------------------------------------------------------------------------------
    public RecorderPanel()
    {
      InitializeComponent();
    }

    public RecorderPanel(Context ctx)
    {
      InitializeComponent();

      this.ctx = ctx;
      recordingManager = new(ctx);
      WaveformWidget.RecordingManager = recordingManager;
      WaveformWidget.GainDb = GainSlider.Value;
      Log.Information("Creating RecorderPanel");

      ctx.RecorderPanel = this;
      ctx.MainForm.RecorderMNU.Checked = true;

      // Initialize recording timer
      recordingTimer = new System.Windows.Forms.Timer();
      recordingTimer.Interval = 1000;
      recordingTimer.Tick += RecordingTimer_Tick;

      playbackUiTimer = new System.Windows.Forms.Timer();
      playbackUiTimer.Interval = PLAYBACK_UI_INTERVAL_MS;
      playbackUiTimer.Tick += PlaybackUiTimer_Tick;

      playbackTimer = new MultimediaTimer.Timer();
      playbackTimer.Mode = TimerMode.Periodic;
      playbackTimer.Interval = TimeSpan.FromMilliseconds(PLAYBACK_TIMER_INTERVAL_MS);
      playbackTimer.Resolution = TimeSpan.FromMilliseconds(1);
      playbackTimer.Elapsed += PlaybackTimer_Elapsed;

      InitAudioSourceMenu();
      ApplySettings();
    }

    private void InitAudioSourceMenu()
    {
      // The CW Console input is independent of Recorder. Users must be able
      // to choose the *actual* audio endpoint being recorded.
      foreach (RecordingAudioSource source in Enum.GetValues<RecordingAudioSource>())
      {
        var item = new ToolStripMenuItem(source switch
        {
          RecordingAudioSource.Auto => "Auto (SDR if running, else RS-BA1 playback)",
          RecordingAudioSource.Sdr => "SkyRoof SDR",
          RecordingAudioSource.WasapiCapture => "IC-9700 USB AF (Windows input)",
          RecordingAudioSource.RsBa1Loopback => "RS-BA1 playback (WASAPI loopback)",
          _ => source.ToString()
        }) { Tag = source };

        item.Click += (_, _) =>
        {
          ctx.Settings.Audio.RecorderSource = source;
          ctx.Settings.SaveToFile();
          UpdateAudioSourceMenu();
        };
        audioSourceMenu.DropDownItems.Add(item);
      }

      RecordMenu.Items.Add(new ToolStripSeparator());
      RecordMenu.Items.Add(audioSourceMenu);
      UpdateAudioSourceMenu();
    }

    private void UpdateAudioSourceMenu()
    {
      foreach (ToolStripMenuItem item in audioSourceMenu.DropDownItems.OfType<ToolStripMenuItem>())
        item.Checked = item.Tag is RecordingAudioSource source &&
                       source == ctx.Settings.Audio.RecorderSource;
    }

    private void RecorderPanel_Shown(object sender, EventArgs e)
    {
      // Restore splitter positions or other UI state from settings if needed
      // SplitContainer.SplitterDistance = ctx.Settings.Recorder.SplitterDistance;
    }

    private void RecorderPanel_FormClosing(object sender, FormClosingEventArgs e)
    {
      Log.Information("Closing RecorderPanel");

      // Stop recording if active
      if (recordingManager.IsRecording) StopRecording();
      if (recordingManager.IsPlayingBack) StopPlayback();

      // Dispose timer
      recordingTimer?.Stop();
      recordingTimer?.Dispose();
      playbackUiTimer?.Stop();
      playbackUiTimer?.Dispose();
      if (playbackTimer != null)
      {
        playbackTimer.Stop();
        playbackTimer.Elapsed -= PlaybackTimer_Elapsed;
        playbackTimer = null;
      }

      recordingManager.Dispose();
      ctx.RecorderPanel = null;
      ctx.MainForm.RecorderMNU.Checked = false;

      // Save UI state to settings if needed
      // ctx.Settings.Recorder.SplitterDistance = SplitContainer.SplitterDistance;

      // Dispose of any resources here
    }

    public void ApplySettings()
    {
      // Apply settings when the panel is created or settings are changed
      // var sett = ctx.Settings.Recorder;
    }




    //----------------------------------------------------------------------------------------------
    //                                        commands
    //----------------------------------------------------------------------------------------------
    private void RecordBtn_Click(object sender, EventArgs e)
    {
      if (recordingManager.IsRecording) StopRecording();
      else if (ModifierKeys.HasFlag(Keys.Control)) RecordIqMNU_Click(sender, e);
      else RecordAudioMNU_Click(sender, e);
    }

    private void RecordMenuBtn_Click(object sender, EventArgs e)
    {
      UpdateAudioSourceMenu();
      RecordMenu.Show(RecordMenuBtn, new Point(0, RecordMenuBtn.Height));
    }

    private void RecordAudioMNU_Click(object sender, EventArgs e)
    {
      StartRecording(true);
    }

    private void RecordIqMNU_Click(object sender, EventArgs e)
    {
      StartRecording(false);
    }

    private void SaveBtn_Click(object sender, EventArgs e)
    {
      if (recordingManager.IsRecordingAudio) SaveMp3MNU_Click(sender, e);
      else SaveWavMNU_Click(sender, e);
    }

    private void SaveMenuBtn_Click(object sender, EventArgs e)
    {
      SaveMp3MNU.Enabled = recordingManager.IsRecordingAudio;
      SaveMenu.Show(SaveMenuBtn, new Point(0, SaveMenuBtn.Height));
    }

    private void SaveMp3MNU_Click(object sender, EventArgs e)
    {
      SaveRecordingWithDialog("mp3");
    }

    private void SaveWavMNU_Click(object sender, EventArgs e)
    {
      SaveRecordingWithDialog("wav");
    }

    private void LoadBtn_Click(object sender, EventArgs e)
    {
      using var dialog = new OpenFileDialog();
      dialog.Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*";
      dialog.InitialDirectory = GetRecordingsPath();
      dialog.Title = "Load Recording";

      if (dialog.ShowDialog() != DialogResult.OK) return;

      try
      {
        LoadRecording(dialog.FileName);
      }
      catch (Exception ex)
      {
        MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Log.Error(ex, "Error loading recording");
      }
    }

    private void PlaybackBtn_Click(object sender, EventArgs e)
    {
      if (PlaybackBtn.BackColor == Color.LightGreen) StopPlayback();
      else StartPlayback();
    }




    //----------------------------------------------------------------------------------------------
    //                                        record
    //----------------------------------------------------------------------------------------------
    private void StartRecording(bool isAudio)
    {
      RecordBtn.BackColor = Color.LightCoral;
      recordingManager.StartRecording(isAudio);
      StatusLabel.Text = recordingManager.GetRecordingStatus();
      recordingTimer?.Start();
      RecordingStart = DateTime.Now;
      if (isAudio)
        Log.Information("Recorder input selected: {Source}", recordingManager.RecordingSourceStatus);

      // Disable controls during recording
      RecordMenuBtn.Enabled = false;
      SetSaveButtonsEnabled(false);
      LoadBtn.Enabled = false;
      PlaybackBtn.Enabled = false;
      toolTip1.SetToolTip(RecordBtn, "Stop Recording");

      WaveformWidget.Invalidate();
    }

    private void StopRecording()
    {
      TimeSpan duration = recordingManager.StopRecording();

      RecordBtn.BackColor = Color.Transparent;
      recordingTimer?.Stop();

      StatusLabel.Text = recordingManager.HasData
        ? $"Recording stopped - Duration: {duration:mm\\:ss} ({recordingManager.SamplesInBuffer} samples)"
        : $"No audio/IQ samples recorded - input: {recordingManager.RecordingSourceStatus}";
      Log.Information($"Recording stopped - {recordingManager.SamplesInBuffer} samples recorded");

      // Enable Save button if we have data
      SetSaveButtonsEnabled(recordingManager.HasData);

      // Re-enable controls
      RecordMenuBtn.Enabled = true;
      LoadBtn.Enabled = true;
      PlaybackBtn.Enabled = recordingManager.HasData;
      toolTip1.SetToolTip(RecordBtn, "Record Audio");

      WaveformWidget.Invalidate();
    }

    private void SetSaveButtonsEnabled(bool enabled)
    {
      SaveBtn.Enabled = enabled;
      SaveMenuBtn.Enabled = enabled;
    }

    private void HandleBufferFull()
    {
      StopRecording();
      StatusLabel.Text = "Recording stopped - Buffer full (30 min)";
    }




    //----------------------------------------------------------------------------------------------
    //                                        play back
    //----------------------------------------------------------------------------------------------
    private void StartPlayback()
    {
      if (!recordingManager.StartPlayback()) return;

      PlaybackBtn.BackColor = Color.LightGreen;
      toolTip1.SetToolTip(PlaybackBtn, "Stop Playback");
      UpdatePlaybackStatus();
      Log.Information("Starting playback");

      RecordBtn.Enabled = false;
      RecordMenuBtn.Enabled = false;
      LoadBtn.Enabled = false;
      SetSaveButtonsEnabled(false);

      playbackUiTimer?.Start();
      playbackTimer?.Start();
      WaveformWidget.Invalidate();
    }

    private void StopPlayback(bool resetPosition = false)
    {
      playbackUiTimer?.Stop();
      playbackTimer?.Stop();
      recordingManager.StopPlayback(resetPosition);

      PlaybackBtn.BackColor = Color.Transparent;
      toolTip1.SetToolTip(PlaybackBtn, "Start Playback");
      StatusLabel.Text = "Playback stopped";
      Log.Information("Playback stopped");

      RecordBtn.Enabled = true;
      RecordMenuBtn.Enabled = true;
      LoadBtn.Enabled = true;
      SetSaveButtonsEnabled(recordingManager.HasData);
      WaveformWidget.Invalidate();
    }

    private void PlaybackTimer_Elapsed(object? sender, EventArgs e)
    {
      if (recordingManager.PumpPlaybackChunk()) return;

      BeginInvoke(() => StopPlayback(true));
    }

    private void PlaybackUiTimer_Tick(object? sender, EventArgs e)
    {
      if (!recordingManager.IsPlayingBack) return;

      UpdatePlaybackStatus();
      WaveformWidget.Invalidate();
    }

    private void UpdatePlaybackStatus()
    {
      StatusLabel.Text = recordingManager.GetPlaybackStatus();
    }

    private void WaveformWidget_SeekRequested(object? sender, WaveformWidget.SeekRequestedEventArgs e)
    {
      if (!recordingManager.IsPlayingBack) return;

      playbackTimer?.Stop();
      bool ok = recordingManager.SeekPlaybackToPosition(e.Position);
      if (!ok)
      {
        StopPlayback(true);
        return;
      }

      UpdatePlaybackStatus();
      WaveformWidget.Invalidate();
      playbackTimer?.Start();
    }




    //----------------------------------------------------------------------------------------------
    //                                        data in
    //----------------------------------------------------------------------------------------------
    internal void AddIqSamples(DataEventArgs<Complex32> e)
    {
      if (!recordingManager.AddIqSamples(e)) return;
      BeginInvoke(HandleBufferFull);
    }

    internal void AddAudioSamples(DataEventArgs<float> e)
    {
      if (!recordingManager.AddAudioSamples(e)) return;
      BeginInvoke(HandleBufferFull);
    }




    //----------------------------------------------------------------------------------------------
    //                                        read/write file
    //----------------------------------------------------------------------------------------------
    private void LoadRecording(string filename)
    {
      bool hasData = recordingManager.LoadRecording(filename);
      SetSaveButtonsEnabled(hasData);
      PlaybackBtn.Enabled = hasData;

      StatusLabel.Text = $"Loaded {Path.GetFileName(filename)}";
      Log.Information($"Recording loaded from {filename}");
      WaveformWidget.Invalidate();
    }

    private async void SaveRecordingWithDialog(string ext)
    {
      bool isMp3 = ext == "mp3";
      bool isIqWav = ext == "wav" && !recordingManager.IsRecordingAudio;

      using var dialog = new SaveFileDialog();
      dialog.Filter = isMp3
        ? "MP3 Files (*.mp3)|*.mp3|All Files (*.*)|*.*"
        : isIqWav
          ? "I/Q WAV Files (*.iq.wav)|*.iq.wav|WAV Files (*.wav)|*.wav|All Files (*.*)|*.*"
          : "WAV Files (*.wav)|*.wav|All Files (*.*)|*.*";
      dialog.DefaultExt = isIqWav ? "iq.wav" : ext;
      dialog.InitialDirectory = GetRecordingsPath();
      dialog.FileName = BuildRecordingFileName(ext, isIqWav);

      if (dialog.ShowDialog() != DialogResult.OK) return;

      string fileName = isIqWav ? EnsureIqWavExtension(dialog.FileName) : dialog.FileName;

      SetSaveButtonsEnabled(false);
      try
      {
        StatusLabel.Text = $"Saving {Path.GetFileName(fileName)}...";
        await recordingManager.SaveRecordingAsync(fileName, isMp3);
        if (!IsDisposed)
          StatusLabel.Text = $"Saved to {Path.GetFileName(fileName)}";
        Log.Information("Recording saved to {File}", fileName);
      }
      catch (Exception ex)
      {
        if (!IsDisposed)
          MessageBox.Show($"Error saving file: {ex.Message}", "Error",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
        Log.Error(ex, "Error saving recording");
      }
      finally
      {
        if (!IsDisposed)
          SetSaveButtonsEnabled(recordingManager.HasData &&
            !recordingManager.IsRecording && !recordingManager.IsPlayingBack);
      }
    }

    private string EnsureIqWavExtension(string fileName)
    {
      if (fileName.EndsWith(".iq.wav", StringComparison.OrdinalIgnoreCase)) return fileName;
      if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        return fileName[..^4] + ".iq.wav";

      return fileName + ".iq.wav";
    }

    private string GetRecordingsPath()
    {
      string path = Path.Combine(Utils.GetUserDataFolder(), "Recordings");
      Directory.CreateDirectory(path);
      return path;
    }

    private string BuildRecordingFileName(string ext, bool isIqWav)
    {
      string? satelliteName = recordingManager.RecordingEvents.GetSingleSatelliteName();
      return RecordingManager.BuildRecordingFileName(RecordingStart, satelliteName, ext, isIqWav);
    }




    //----------------------------------------------------------------------------------------------
    //                                        widget
    //----------------------------------------------------------------------------------------------
    private void GainSlider_ValueChanged(object sender, EventArgs e)
    {
      double db = GainSlider.Value;
      toolTip1.SetToolTip(GainSlider, $"Gain {db:F0} dB");
      WaveformWidget.GainDb = (int)db;
      WaveformWidget.Invalidate();
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
      if (!recordingManager.IsRecording) return;

      RememberRecordingEvents();
      StatusLabel.Text = recordingManager.SamplesInBuffer == 0 &&
                         (DateTime.UtcNow - recordingManager.RecordingStartTime).TotalSeconds > 3
        ? $"No samples from {recordingManager.RecordingSourceStatus} - check Recorder audio source/device"
        : recordingManager.GetRecordingStatus();
      WaveformWidget.Invalidate();
    }

    internal void RememberRecordingEvents()
    {
      if (!recordingManager.RememberRecordingEvents()) return;
      WaveformWidget.Invalidate();
    }

    internal void RememberQsoSaved(string callsign)
    {
      if (!recordingManager.RememberQsoSaved(callsign)) return;
      WaveformWidget.Invalidate();
    }
  }
}
