using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  internal sealed class OnvifCameraPanel : DockContent
  {
    private readonly Context ctx;
    private readonly ComboBox devices = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox profiles = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox endpoint = new() { Width = 310, PlaceholderText = "http://192.168.1.10/onvif/device_service" };
    private readonly TextBox username = new() { Width = 130, PlaceholderText = "Camera user" };
    private readonly TextBox password = new() { Width = 130, PlaceholderText = "Camera password", UseSystemPasswordChar = true };
    private readonly TextBox mpvPath = new() { Width = 300, Text = "mpv.exe" };
    private readonly Label status = new()
    { Text = "ONVIF device discovery + Media1/Media2; D3D11VA hardware decoding only.",
      Dock = DockStyle.Bottom, Height = 28, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly OnvifGpuPlayer player = new();
    private CancellationTokenSource? work;
    private OnvifProtocolClient? protocol;

    public OnvifCameraPanel(Context context)
    {
      ctx = context;
      Text = "ONVIF Camera";
      Name = "OnvifCameraPanel";
      ClientSize = new Size(950, 560);
      MinimumSize = new Size(650, 380);
      ctx.OnvifCameraPanel = this;
      ctx.MainForm.OnvifCameraMNU.Checked = true;

      var top = new TableLayoutPanel
      {
        Dock = DockStyle.Top, Height = 128, ColumnCount = 1, RowCount = 3,
        AutoSize = false
      };
      top.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
      top.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
      top.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
      top.Controls.Add(Row(
        new Label { Text = "Device", AutoSize = true },
        devices, Button("Discover", DiscoverAsync),
        new Label { Text = "Endpoint", AutoSize = true },
        endpoint, Button("Profiles", LoadProfilesAsync)), 0, 0);
      top.Controls.Add(Row(
        new Label { Text = "User", AutoSize = true },
        username, new Label { Text = "Password", AutoSize = true },
        password, new Label { Text = "Video", AutoSize = true },
        profiles, Button("Play (GPU)", PlayAsync),
        Button("Stop", () => { StopPlayback(); return Task.CompletedTask; })), 0, 1);
      top.Controls.Add(Row(
        new Label { Text = "mpv.exe", AutoSize = true },
        mpvPath, Button("Browse", () =>
        {
          using var dialog = new OpenFileDialog
          { Filter = "mpv player (mpv.exe)|mpv.exe|Applications (*.exe)|*.exe" };
          if (dialog.ShowDialog(this) == DialogResult.OK) mpvPath.Text = dialog.FileName;
          return Task.CompletedTask;
        }),
        new Label { Text = "Hardware-only: H.264/H.265 GPU support required. No software fallback.", AutoSize = true }
      ), 0, 2);

      devices.SelectedIndexChanged += (_, _) =>
      {
        if (devices.SelectedItem is DeviceOption selected)
          endpoint.Text = selected.Device.Endpoint;
      };

      Controls.Add(video);
      Controls.Add(status);
      Controls.Add(top);
      player.PlaybackFailed += reason =>
      {
        if (!IsDisposed && IsHandleCreated)
          BeginInvoke((Action)(() => SetStatus(reason)));
      };
      FormClosing += (_, _) => StopPlayback();
      FormClosed += (_, _) =>
      {
        ctx.OnvifCameraPanel = null;
        ctx.MainForm.OnvifCameraMNU.Checked = false;
        protocol?.Dispose();
        player.Dispose();
      };
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
      var flow = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false, AutoScroll = true, Padding = new Padding(3),
        Margin = new Padding(0)
      };
      foreach (Control c in controls)
      {
        c.Margin = new Padding(3);
        if (c is Label label) label.Padding = new Padding(0, 5, 0, 0);
        flow.Controls.Add(c);
      }
      return flow;
    }

    private Button Button(string label, Func<Task> action)
    {
      var button = new Button { Text = label, AutoSize = true, Height = 27 };
      button.Click += async (_, _) =>
      {
        button.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Operation cancelled."); }
        catch (Exception e)
        {
          // Never expose credentials or RTSP userinfo in the user-visible error.
          SetStatus(e is System.ComponentModel.Win32Exception ?
            "mpv.exe not found. Choose an installed mpv executable using Browse." :
            e.Message);
        }
        finally { if (!IsDisposed) button.Enabled = true; }
      };
      return button;
    }

    private CancellationToken BeginOperation()
    {
      work?.Cancel();
      work?.Dispose();
      work = new CancellationTokenSource();
      return work.Token;
    }

    private async Task DiscoverAsync()
    {
      SetStatus("Searching for ONVIF WS-Discovery ProbeMatches (4 seconds)...");
      var list = await OnvifDiscovery.ProbeAsync(BeginOperation());
      devices.Items.Clear();
      foreach (OnvifCameraDevice item in list)
        devices.Items.Add(new DeviceOption(item));
      if (devices.Items.Count > 0) devices.SelectedIndex = 0;
      SetStatus($"ONVIF discovery completed: {list.Count} endpoint(s).");
    }

    private async Task LoadProfilesAsync()
    {
      StopPlayback();
      var uri = endpoint.Text.Trim();
      protocol?.Dispose();
      protocol = new OnvifProtocolClient(uri, username.Text, password.Text);
      SetStatus("Querying ONVIF GetServices/GetCapabilities and Media profiles...");
      var list = await protocol.GetProfilesAsync(BeginOperation());
      profiles.Items.Clear();
      foreach (var item in list) profiles.Items.Add(item);
      if (profiles.Items.Count != 0) profiles.SelectedIndex = 0;
      SetStatus($"{list.Count} ONVIF media profiles found. Select one and press Play.");
    }

    private async Task PlayAsync()
    {
      if (protocol == null || profiles.SelectedItem is not OnvifMediaProfile selected)
        throw new InvalidOperationException("Query ONVIF Profiles before playing.");
      CancellationToken token = BeginOperation();
      SetStatus("Negotiating ONVIF GetStreamUri...");
      Uri rtsp = await protocol.GetStreamUriAsync(selected, token);
      SetStatus("Opening RTSP/TCP and verifying D3D11VA hardware decoder...");
      await player.StartAsync(mpvPath.Text.Trim(), video.Handle, rtsp,
        username.Text, password.Text, token);
      SetStatus("Live ONVIF stream: hardware decoding verified (D3D11VA).");
    }

    private void StopPlayback()
    {
      work?.Cancel();
      work?.Dispose();
      work = null;
      player.Stop();
    }

    private void SetStatus(string message) => status.Text = message;

    private sealed class DeviceOption(OnvifCameraDevice device)
    {
      internal OnvifCameraDevice Device { get; } = device;
      public override string ToString() =>
        Uri.TryCreate(Device.Endpoint, UriKind.Absolute, out Uri? uri)
          ? uri.Host + ":" + uri.Port
          : "ONVIF camera";
    }
  }
}
