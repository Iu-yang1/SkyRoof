using System;
using System.Linq;
using System.Windows.Forms;
using Serilog;
using VE3NEA;

namespace SkyRoof
{
  public partial class GainWidget : UserControl
  {
    public Context ctx;
    private readonly RsBa1AudioSessionController RemoteAudio = new();
    private RsBa1AudioSessionController.AudioSessionInfo? ActiveAudioSession;
    private readonly ToolTip GainTips = new();
    private readonly ContextMenuStrip AfSessionMenu = new();
    private bool UpdatingSliders;
    private bool LastRemoteAudioMode;
    private bool LastRadioRfMode;
    private DateTime LastRfReadbackRequestedUtc = DateTime.MinValue;
    private DateTime LastAfSessionCheckUtc = DateTime.MinValue;

    public GainWidget()
    {
      InitializeComponent();
      AfSessionMenu.Opening += (_, _) => PopulateAudioSessionMenu();
      AfGainSlider.ContextMenuStrip = AfSessionMenu;
      AfGainLabel.ContextMenuStrip = AfSessionMenu;
      GainTips.SetToolTip(AfGainSlider,
        "RF remote mode: controls RS-BA1 Remote Utility in Windows Volume Mixer. " +
        "Right-click to select the audio session.");
    }

    private bool UseRadioRf =>
      ctx.CatControl.GetIcomRfGainBackend()?.IsRunning == true;

    private bool UseRsBa1Af =>
      UseRadioRf ||
      (ctx.Sdr == null &&
       (!ctx.Settings.Sdr.Enabled ||
        string.IsNullOrWhiteSpace(ctx.Settings.Sdr.SelectedDeviceName) ||
        !string.IsNullOrWhiteSpace(ctx.Settings.Audio.RsBa1PlaybackProcessName)));

    internal void RefreshControlBindings()
    {
      if (ctx == null) return;
      bool remoteRf = UseRadioRf;
      bool remoteAf = UseRsBa1Af;

      if (remoteRf != LastRadioRfMode)
      {
        LastRadioRfMode = remoteRf;
        LastRfReadbackRequestedUtc = DateTime.MinValue;
        ApplyRfGain();
      }
      if (remoteAf != LastRemoteAudioMode)
      {
        LastRemoteAudioMode = remoteAf;
        ApplyAfGain();
      }

      // Readback in the existing CAT worker (never from the UI thread).
      if (remoteRf &&
          DateTime.UtcNow - LastRfReadbackRequestedUtc >
            TimeSpan.FromSeconds(6))
      {
        if (ctx.CatControl.RequestIcomRfGainReadback())
          LastRfReadbackRequestedUtc = DateTime.UtcNow;
      }

      if (remoteAf &&
          DateTime.UtcNow - LastAfSessionCheckUtc >
            TimeSpan.FromSeconds(2))
        RefreshRsBa1AudioSession();
    }

    public void ApplyAfGain()
    {
      if (ctx == null) return;
      if (UseRsBa1Af)
      {
        RefreshRsBa1AudioSession();
        return;
      }

      ActiveAudioSession = null;
      AfGainSlider.Enabled = true;
      UpdatingSliders = true;
      try
      {
        AfGainSlider.Value = Math.Clamp(
          ctx.Settings.Audio.SoundcardVolume,
          AfGainSlider.Minimum, AfGainSlider.Maximum);
      }
      finally { UpdatingSliders = false; }

      // Existing SDR/local speaker behavior is unchanged.
      ctx.SpeakerSoundcard.Volume =
        Dsp.FromDb2(ctx.Settings.Audio.SoundcardVolume);
      AfGainLabel.Text = $"{AfGainSlider.Value} dB";
      GainTips.SetToolTip(AfGainSlider, "SkyRoof local SDR soundcard volume");
    }

    private void RefreshRsBa1AudioSession()
    {
      LastAfSessionCheckUtc = DateTime.UtcNow;
      try
      {
        if (RemoteAudio.TryGetVolume(
              ctx.Settings.Audio.RsBa1PlaybackProcessName,
              ctx.Settings.Audio.RsBa1PlaybackDeviceId,
              out float scalar, out var session))
        {
          ActiveAudioSession = session;
          AfGainSlider.Enabled = true;
          UpdatingSliders = true;
          try
          {
            AfGainSlider.Value = Math.Clamp(
              RsBa1AudioSessionController.ScalarToGainDb(scalar),
              AfGainSlider.Minimum, AfGainSlider.Maximum);
          }
          finally { UpdatingSliders = false; }
          AfGainLabel.Text = $"{AfGainSlider.Value} dB";
          GainTips.SetToolTip(AfGainSlider,
            $"Windows mixer: {session!.DisplayText}. Right-click to select another session.");
        }
        else
        {
          ActiveAudioSession = null;
          AfGainSlider.Enabled = false;
          AfGainLabel.Text = "--";
          GainTips.SetToolTip(AfGainSlider,
            "RS-BA1 audio session not found or ambiguous. " +
            "Start Remote Utility and right-click AF Gain to select its playback session.");
        }
      }
      catch (Exception ex)
      {
        ActiveAudioSession = null;
        AfGainSlider.Enabled = false;
        AfGainLabel.Text = "--";
        Log.Warning(ex, "Unable to enumerate Windows RS-BA1 playback sessions.");
      }
    }

    private void PopulateAudioSessionMenu()
    {
      AfSessionMenu.Items.Clear();
      try
      {
        var sessions = RemoteAudio.ListSessions();
        if (sessions.Count == 0)
        {
          AfSessionMenu.Items.Add(new ToolStripMenuItem(
            "No active Windows playback sessions") { Enabled = false });
          return;
        }

        AfSessionMenu.Items.Add(new ToolStripMenuItem(
          "Select RS-BA1 Remote Utility audio session") { Enabled = false });
        AfSessionMenu.Items.Add(new ToolStripSeparator());

        foreach (var session in sessions)
        {
          var item = new ToolStripMenuItem(session.DisplayText) {
            Checked = ctx.Settings.Audio.RsBa1PlaybackProcessName ==
                        session.ProcessName &&
                      ctx.Settings.Audio.RsBa1PlaybackDeviceId ==
                        session.DeviceId
          };
          item.Click += (_, _) =>
          {
            // This user-selected identity is tied to an application and a
            // specific output endpoint; never write to the global mixer.
            ctx.Settings.Audio.RsBa1PlaybackProcessName = session.ProcessName;
            ctx.Settings.Audio.RsBa1PlaybackDeviceId = session.DeviceId;
            LastAfSessionCheckUtc = DateTime.MinValue;
            RefreshRsBa1AudioSession();
            ctx.Settings.SaveToFile();
          };
          AfSessionMenu.Items.Add(item);
        }
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Unable to list Windows playback audio sessions.");
        AfSessionMenu.Items.Add(new ToolStripMenuItem(
          "Audio session enumeration failed") { Enabled = false });
      }
    }

    private void AfGainSlider_ValueChanged(object sender, EventArgs e)
    {
      if (ctx == null || UpdatingSliders) return;
      if (UseRsBa1Af)
      {
        if (ActiveAudioSession == null)
          return;
        try
        {
          if (!RemoteAudio.TrySetVolume(ActiveAudioSession,
                RsBa1AudioSessionController.GainDbToScalar(AfGainSlider.Value)))
          {
            RefreshRsBa1AudioSession();
            return;
          }
          AfGainLabel.Text = $"{AfGainSlider.Value} dB";
        }
        catch (Exception ex)
        {
          Log.Warning(ex, "Failed to update RS-BA1 playback session volume.");
          RefreshRsBa1AudioSession();
        }
        return;
      }

      ctx.Settings.Audio.SoundcardVolume = AfGainSlider.Value;
      ctx.SpeakerSoundcard.Volume =
        Dsp.FromDb2(ctx.Settings.Audio.SoundcardVolume);
      AfGainLabel.Text = $"{AfGainSlider.Value} dB";
    }

    internal void ApplyRfGain()
    {
      if (ctx == null) return;
      if (UseRadioRf)
      {
        RfGainSlider.Enabled = true;
        GainTips.SetToolTip(RfGainSlider,
          "IC-9700 hardware RF gain via SkyCAT CI-V 14 02 (0..255).");
        // Never apply the default 100% UI value to the radio at startup.
        // Wait for SCOPE-independent RF gain readback.
        RfGainLabel.Text = "--";
        return;
      }

      RfGainSlider.Enabled = ctx.Sdr?.CanChangeGain == true;
      GainTips.SetToolTip(RfGainSlider,
        RfGainSlider.Enabled ? "Local SoapySDR RF gain" :
          "No suitable SkyCAT IC-9700 or SDR gain backend");
      if (ctx.Sdr == null)
      {
        RfGainLabel.Text = "--";
        return;
      }

      UpdatingSliders = true;
      try
      {
        RfGainSlider.Value = Math.Clamp(ctx.Sdr.NormalizedGain,
          RfGainSlider.Minimum, RfGainSlider.Maximum);
      }
      finally { UpdatingSliders = false; }
      RfGainLabel.Text = RfGainSlider.Value.ToString();
    }

    internal void ApplyRadioRfGainReadback(int raw)
    {
      if (!UseRadioRf || RfGainSlider.Capture || raw is < 0 or > 255)
        return;

      UpdatingSliders = true;
      try
      {
        RfGainSlider.Value = Math.Clamp(
          (int)Math.Round(raw * 100.0 / 255.0),
          RfGainSlider.Minimum, RfGainSlider.Maximum);
      }
      finally { UpdatingSliders = false; }
      RfGainLabel.Text = RfGainSlider.Value.ToString();
    }

    private void RfGainSlider_ValueChanged(object sender, EventArgs e)
    {
      if (ctx == null || UpdatingSliders) return;
      RfGainLabel.Text = RfGainSlider.Value.ToString();
      if (UseRadioRf)
      {
        int raw = (int)Math.Round(RfGainSlider.Value * 255.0 / 100.0);
        if (!ctx.CatControl.RequestIcomRfGain(raw))
        {
          RfGainLabel.Text = "--";
          Log.Warning("IC-9700 RF gain request rejected: no active SkyCAT gain backend.");
        }
        return;
      }

      if (ctx.Sdr?.CanChangeGain == true)
        ctx.Sdr.NormalizedGain = RfGainSlider.Value;
    }
  }
}
