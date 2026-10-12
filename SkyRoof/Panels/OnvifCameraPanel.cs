using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// A video-first ONVIF window. Connection and protocol controls live in
  /// OnvifCameraSettingsForm; this view only owns playback state and transport.
  /// </summary>
  internal sealed class OnvifCameraPanel : DockContent
  {
    private readonly Context ctx;
    private readonly OnvifGpuPlayer player = new();
    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Panel placeholder = new()
    { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24) };
    private readonly Label placeholderText = new()
    {
      AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 13F),
      TextAlign = ContentAlignment.MiddleCenter
    };
    private readonly Button openSettings = new() { Text = "Open Settings", Width = 140, Height = 32 };
    private readonly Label title = new()
    {
      Text = "Live video", Dock = DockStyle.Fill, AutoEllipsis = true,
      TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 11F),
      Padding = new Padding(13, 0, 0, 0)
    };
    private readonly Label status = new()
    { Text = "Not connected", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label stream = new()
    { Text = "", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button play = new() { Text = "Play", Width = 64, Height = 29 };
    private readonly Button stop = new() { Text = "Stop", Width = 64, Height = 29 };
    private readonly Button reconnect = new() { Text = "Reconnect", Width = 92, Height = 29 };
    private readonly Button settings = new() { Text = "Settings", Width = 82, Height = 29 };
    private readonly System.Windows.Forms.Timer retryTimer = new() { Interval = 5000 };
    private CancellationTokenSource? pending;
    private bool connecting;
    private bool playing;
    private bool closing;
    private bool stoppedByUser;
    private OnvifMediaProfile? currentProfile;

    public OnvifCameraPanel(Context context)
    {
      ctx = context;
      Text = "ONVIF Camera";
      Name = "OnvifCameraPanel";
      AutoScaleMode = AutoScaleMode.Font;
      ClientSize = new Size(940, 600);
      MinimumSize = new Size(500, 320);
      ctx.OnvifCameraPanel = this;
      ctx.MainForm.OnvifCameraMNU.Checked = true;

      var header = new TableLayoutPanel
      {
        Dock = DockStyle.Top, Height = 48, ColumnCount = 2,
        BackColor = SystemColors.Control, Margin = Padding.Empty
      };
      header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 355));
      header.Controls.Add(title, 0, 0);

      var commands = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false, AutoScroll = false, Padding = new Padding(0, 6, 8, 0)
      };
      commands.Controls.Add(settings);
      commands.Controls.Add(reconnect);
      commands.Controls.Add(stop);
      commands.Controls.Add(play);
      header.Controls.Add(commands, 1, 0);

      var footer = new TableLayoutPanel
      {
        Dock = DockStyle.Bottom, Height = 29, ColumnCount = 2,
        BackColor = SystemColors.Control, Padding = new Padding(10, 2, 10, 2)
      };
      footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
      footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
      footer.Controls.Add(status, 0, 0);
      footer.Controls.Add(stream, 1, 0);

      var placeholderLayout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
        BackColor = Color.FromArgb(18, 20, 24)
      };
      placeholderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
      placeholderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      placeholderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
      var prompt = new FlowLayoutPanel
      {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        Anchor = AnchorStyles.None, BackColor = Color.Transparent
      };
      placeholderText.Margin = new Padding(0, 0, 0, 12);
      openSettings.Margin = new Padding(0);
      prompt.Controls.Add(placeholderText);
      prompt.Controls.Add(openSettings);
      prompt.SizeChanged += (_, _) =>
      {
        // Center the only configuration action under the prompt.
        openSettings.Left = (prompt.ClientSize.Width - openSettings.Width) / 2;
      };
      placeholderLayout.Controls.Add(prompt, 0, 1);
      placeholder.Controls.Add(placeholderLayout);

      video.Controls.Add(placeholder);
      Controls.Add(video);
      Controls.Add(footer);
      Controls.Add(header);

      play.Click += async (_, _) => await ExecuteAsync(true);
      stop.Click += (_, _) => StopPlayback(byUser: true);
      reconnect.Click += async (_, _) => await ExecuteAsync(true);
      settings.Click += (_, _) => ShowSettings();
      openSettings.Click += (_, _) => ShowSettings();
      retryTimer.Tick += async (_, _) =>
      {
        retryTimer.Stop();
        if (!closing && !stoppedByUser && ctx.Settings.OnvifCamera.AutoReconnect)
          await ExecuteAsync(true);
      };
      player.PlaybackFailed += reason =>
      {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try
        {
          BeginInvoke((Action)(() =>
          {
            if (closing) return;
            playing = false;
            placeholder.Visible = true;
            SetStatus(reason.Contains("D3D11VA", StringComparison.OrdinalIgnoreCase)
              ? "Hardware decoder unavailable. Choose another video profile."
              : "Video connection interrupted.");
            UpdateControls();
            if (!stoppedByUser && ctx.Settings.OnvifCamera.AutoReconnect &&
                !reason.Contains("D3D11VA", StringComparison.OrdinalIgnoreCase))
              retryTimer.Start();
          }));
        }
        catch (InvalidOperationException) { }
      };
      Shown += async (_, _) =>
      {
        UpdateControls();
        if (!ctx.Settings.OnvifCamera.IsConfigured)
        {
          SetStatus("Configure an ONVIF camera to begin.");
          return;
        }
        if (ctx.Settings.OnvifCamera.AutoConnect)
          await ExecuteAsync(ctx.Settings.OnvifCamera.AutoPlay);
      };
      FormClosing += (_, _) =>
      {
        closing = true;
        StopPlayback(byUser: true);
      };
      FormClosed += (_, _) =>
      {
        ctx.OnvifCameraPanel = null;
        ctx.MainForm.OnvifCameraMNU.Checked = false;
        retryTimer.Dispose();
        pending?.Dispose();
        player.Dispose();
      };
      UpdateControls();
    }

    internal static bool ShouldAutoplay(OnvifCameraSettings configuration) =>
      configuration.IsConfigured && configuration.AutoConnect && configuration.AutoPlay;

    private void ShowSettings()
    {
      if (closing) return;
      using var dialog = new OnvifCameraSettingsForm(ctx.Settings.OnvifCamera);
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      OnvifCameraSettings before = ctx.Settings.OnvifCamera;
      ctx.Settings.OnvifCamera = dialog.Result;
      try { ctx.Settings.SaveToFile(); }
      catch (Exception)
      {
        ctx.Settings.OnvifCamera = before;
        MessageBox.Show(this,
          "Camera preferences could not be saved. Check write access to the SkyRoof data folder.",
          "ONVIF Camera", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      StopPlayback(byUser: false);
      UpdateControls();
      if (ctx.Settings.OnvifCamera.AutoConnect)
        _ = ExecuteAsync(ctx.Settings.OnvifCamera.AutoPlay);
      else SetStatus("Camera settings saved. Press Play to connect.");
    }

    private async Task ExecuteAsync(bool startPlayback)
    {
      if (closing || connecting) return;
      var config = ctx.Settings.OnvifCamera.Clone();
      if (!config.IsConfigured)
      {
        SetStatus("No camera configured.");
        ShowSettings();
        return;
      }
      StopPlayback(byUser: false);
      stoppedByUser = false;
      connecting = true;
      pending = new CancellationTokenSource();
      CancellationToken token = pending.Token;
      SetStatus("Connecting to camera...");
      UpdateControls();
      try
      {
        // Always negotiate a fresh ONVIF Profile and RTSP URI, not a stale
        // saved streaming URL. Media1/Media2 logic remains in the SOAP client.
        using var client = new OnvifProtocolClient(
          config.DeviceServiceUrl, config.Username, config.Password);
        var profiles = await client.GetProfilesAsync(token);
        token.ThrowIfCancellationRequested();
        currentProfile = OnvifCameraSettings.SelectProfile(
          profiles, config.PreferredProfileToken, config.PreferMedia2);
        if (!startPlayback)
        {
          SetStatus("Camera ready.");
          return;
        }

        SetStatus("Starting hardware-decoded video...");
        Uri uri = await client.GetStreamUriAsync(currentProfile, token);
        token.ThrowIfCancellationRequested();
        await player.StartAsync(config.MpvPath, video.Handle, uri,
          config.Username, config.Password, token);
        token.ThrowIfCancellationRequested();
        playing = true;
        placeholder.Visible = false;
        SetStatus("Live · D3D11VA hardware decoding");
      }
      catch (OperationCanceledException) { }
      catch (System.ComponentModel.Win32Exception)
      {
        SetStatus("mpv.exe was not found. Select the player in Settings.");
      }
      catch (NotSupportedException)
      {
        SetStatus("D3D11VA unavailable for this stream. Choose another profile.");
      }
      catch (Exception)
      {
        // SOAP faults and RTSP errors may include authenticated URIs; never
        // print raw exception messages into the camera status or logs.
        SetStatus("Unable to connect. Check ONVIF address, credentials and stream.");
      }
      finally
      {
        if (!playing) player.Stop();
        connecting = false;
        if (!closing) UpdateControls();
      }
    }

    private void StopPlayback(bool byUser)
    {
      stoppedByUser = byUser;
      retryTimer.Stop();
      pending?.Cancel();
      // Do not dispose a CancellationTokenSource while an async operation is
      // still in-flight; it will be released after its token is no longer used.
      player.Stop();
      playing = false;
      currentProfile = null;
      placeholder.Visible = true;
      if (!closing)
      {
        SetStatus(byUser ? "Stopped." : "Not connected.");
        UpdateControls();
      }
    }

    private void UpdateControls()
    {
      bool configured = ctx.Settings.OnvifCamera.IsConfigured;
      title.Text = configured ? ctx.Settings.OnvifCamera.CameraName : "Live video";
      play.Enabled = configured && !connecting && !playing;
      stop.Enabled = playing || connecting;
      reconnect.Enabled = configured && !connecting;
      settings.Enabled = !connecting;
      placeholderText.Text = !configured
        ? "No camera configured"
        : connecting
          ? "Connecting..."
          : playing
            ? ""
            : "Live video";
      openSettings.Visible = !configured;
      string profile = currentProfile?.Name ?? "No stream";
      stream.Text = configured
        ? $"{profile}  ·  {(playing ? "D3D11VA" : "Offline")}"
        : "";
    }

    private void SetStatus(string message)
    {
      if (!closing) status.Text = message;
    }
  }
}
