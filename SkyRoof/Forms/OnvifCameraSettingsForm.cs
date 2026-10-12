using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Connection parameters and discovery live here, not in the Live video view.
  /// The dialog edits a detached settings copy and only persists after OK.
  /// </summary>
  internal sealed class OnvifCameraSettingsForm : Form
  {
    private readonly OnvifCameraSettings edited;
    private readonly ComboBox devices = new()
    { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox cameraName = new() { Dock = DockStyle.Fill };
    private readonly TextBox user = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new()
    { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ComboBox profiles = new()
    { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox mpvPath = new() { Dock = DockStyle.Fill };
    private readonly CheckBox autoConnect = new() { Text = "Connect when window opens", AutoSize = true };
    private readonly CheckBox autoPlay = new() { Text = "Start video automatically", AutoSize = true };
    private readonly CheckBox autoReconnect = new() { Text = "Reconnect after playback interruption", AutoSize = true };
    private readonly CheckBox media2 = new() { Text = "Prefer ONVIF Media2 (fallback Media1)", AutoSize = true };
    private readonly Label feedback = new()
    { AutoSize = false, Dock = DockStyle.Fill, Height = 48, AutoEllipsis = true,
      Text = "Select a camera or enter its ONVIF Device Service address." };
    private CancellationTokenSource? operations;

    internal OnvifCameraSettings Result => edited.Clone();

    internal OnvifCameraSettingsForm(OnvifCameraSettings current)
    {
      edited = current.Clone();
      Text = "ONVIF Camera Settings";
      StartPosition = FormStartPosition.CenterParent;
      AutoScaleMode = AutoScaleMode.Font;
      ClientSize = new Size(760, 560);
      MinimumSize = new Size(640, 520);
      ShowInTaskbar = false;
      Font = new Font("Segoe UI", 9F);

      cameraName.Text = edited.CameraName;
      endpoint.Text = edited.DeviceServiceUrl;
      user.Text = edited.Username;
      password.Text = edited.Password;
      mpvPath.Text = edited.MpvPath;
      autoConnect.Checked = edited.AutoConnect;
      autoPlay.Checked = edited.AutoPlay;
      autoReconnect.Checked = edited.AutoReconnect;
      media2.Checked = edited.PreferMedia2;
      // Previously loaded profiles must not be kept when the camera endpoint
      // changes; profile tokens are device-specific.
      endpoint.TextChanged += (_, _) => profiles.Items.Clear();
      endpoint.Leave += (_, _) =>
      {
        string validation = OnvifCameraSettings.ValidateEndpoint(endpoint.Text);
        if (validation.Length != 0 && !string.IsNullOrWhiteSpace(endpoint.Text))
          feedback.Text = validation;
      };
      autoPlay.CheckedChanged += (_, _) =>
      {
        if (autoPlay.Checked) autoConnect.Checked = true;
      };

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(16, 12, 16, 0), ColumnCount = 2,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

      AddRow(layout, "Camera name", cameraName);
      var discover = MakeRow(devices, Button("Discover", DiscoverAsync));
      devices.SelectedIndexChanged += (_, _) =>
      {
        if (devices.SelectedItem is DeviceOption chosen)
          endpoint.Text = chosen.Device.Endpoint;
      };
      AddRow(layout, "Device", discover);
      AddRow(layout, "Device service", endpoint);
      AddRow(layout, "Username", user);
      AddRow(layout, "Password", password);

      var profileRow = MakeRow(profiles, Button("Get profiles", GetProfilesAsync));
      AddRow(layout, "Video profile", profileRow);

      var playerRow = MakeRow(mpvPath, Button("Browse", BrowsePlayerAsync));
      AddRow(layout, "Player", playerRow);

      var preferences = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoSize = true, Margin = new Padding(2, 4, 0, 8)
      };
      preferences.Controls.Add(media2);
      preferences.Controls.Add(autoConnect);
      preferences.Controls.Add(autoPlay);
      preferences.Controls.Add(autoReconnect);
      AddRow(layout, "Playback", preferences);

      feedback.ForeColor = SystemColors.GrayText;
      AddRow(layout, "Status", feedback);

      var actions = new FlowLayoutPanel
      {
        Dock = DockStyle.Bottom, Height = 56,
        FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 9, 16, 8)
      };
      var save = new Button { Text = "Save", Width = 100, Height = 30 };
      var cancel = new Button { Text = "Cancel", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
      save.Click += (_, _) => Save();
      actions.Controls.Add(save);
      actions.Controls.Add(cancel);
      CancelButton = cancel;
      var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
      scroll.Controls.Add(layout);
      layout.Dock = DockStyle.Top;
      Controls.Add(scroll);
      Controls.Add(actions);
      FormClosing += (_, _) => operations?.Cancel();
    }

    private static void AddRow(TableLayoutPanel table, string label, Control content)
    {
      int row = table.RowCount++;
      table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      var caption = new Label
      {
        Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0),
        Margin = new Padding(0, 4, 5, 11)
      };
      content.Margin = new Padding(0, 2, 0, 9);
      table.Controls.Add(caption, 0, row);
      table.Controls.Add(content, 1, row);
    }

    private static FlowLayoutPanel MakeRow(params Control[] controls)
    {
      var row = new FlowLayoutPanel
      {
        FlowDirection = FlowDirection.LeftToRight,
        Dock = DockStyle.Fill, Height = 35, WrapContents = false,
        AutoScroll = true, Margin = new Padding(0)
      };
      foreach (Control control in controls)
      {
        control.Margin = new Padding(0, 1, 9, 0);
        if (control is TextBox box)
          box.Width = 300;
        row.Controls.Add(control);
      }
      return row;
    }

    private Button Button(string title, Func<Task> action)
    {
      var button = new Button { Text = title, AutoSize = true, Height = 28 };
      button.Click += async (_, _) =>
      {
        button.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { feedback.Text = "Operation cancelled."; }
        catch (Exception)
        {
          // Do not expose arbitrary SOAP/RTSP exception contents: they can
          // contain an authenticated URI or a camera-specific credential.
          feedback.Text = "Connection failed. Check camera address, permissions and credentials.";
        }
        finally { if (!IsDisposed) button.Enabled = true; }
      };
      return button;
    }

    private CancellationToken BeginOperation()
    {
      operations?.Cancel();
      operations?.Dispose();
      operations = new CancellationTokenSource();
      return operations.Token;
    }

    private async Task DiscoverAsync()
    {
      feedback.Text = "Discovering ONVIF devices on active IPv4 adapters...";
      var found = await OnvifDiscovery.ProbeWithDiagnosticsAsync(BeginOperation());
      if (IsDisposed) return;
      devices.Items.Clear();
      foreach (var device in found.Devices) devices.Items.Add(new DeviceOption(device));
      if (devices.Items.Count > 0) devices.SelectedIndex = 0;
      feedback.Text = found.Devices.Count > 0
        ? $"{found.Devices.Count} ONVIF endpoint(s) on {found.InterfacesProbed} adapter(s)."
        : $"No ONVIF replies ({found.InterfacesProbed} adapters, " +
          $"{found.ResponsePackets} UDP packets). Check UDP 3702/firewall, " +
          "or enter an HTTP ONVIF Device Service address (not RTSP port 554).";
    }

    private async Task GetProfilesAsync()
    {
      string error = OnvifCameraSettings.ValidateEndpoint(endpoint.Text);
      if (error.Length != 0) { feedback.Text = error; return; }
      feedback.Text = "Querying Media1 / Media2 profiles...";
      using var client = new OnvifProtocolClient(
        endpoint.Text.Trim(), user.Text.Trim(), password.Text);
      var found = await client.GetProfilesAsync(BeginOperation());
      if (IsDisposed) return;
      profiles.Items.Clear();
      foreach (var profile in found) profiles.Items.Add(profile);
      var preferred = OnvifCameraSettings.SelectProfile(
        found, edited.PreferredProfileToken, media2.Checked);
      int index = found.ToList().IndexOf(preferred);
      if (index >= 0) profiles.SelectedIndex = index;
      feedback.Text = $"{found.Count} ONVIF profile(s). Choose the default stream.";
    }

    private Task BrowsePlayerAsync()
    {
      using var chooser = new OpenFileDialog
      { Title = "Select mpv.exe", Filter = "mpv player (mpv.exe)|mpv.exe|Executables (*.exe)|*.exe" };
      if (chooser.ShowDialog(this) == DialogResult.OK) mpvPath.Text = chooser.FileName;
      return Task.CompletedTask;
    }

    private void Save()
    {
      string error = OnvifCameraSettings.ValidateEndpoint(endpoint.Text);
      if (error.Length != 0)
      {
        MessageBox.Show(this, error, "Invalid ONVIF address",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
        endpoint.Focus();
        return;
      }
      edited.CameraName = string.IsNullOrWhiteSpace(cameraName.Text)
        ? "Camera 01" : cameraName.Text.Trim();
      if (!string.Equals(edited.DeviceServiceUrl, endpoint.Text.Trim(),
            StringComparison.OrdinalIgnoreCase))
        edited.PreferredProfileToken = string.Empty;
      edited.DeviceServiceUrl = endpoint.Text.Trim();
      edited.Username = user.Text.Trim();
      edited.Password = password.Text;
      edited.MpvPath = string.IsNullOrWhiteSpace(mpvPath.Text) ? "mpv.exe" : mpvPath.Text.Trim();
      edited.PreferMedia2 = media2.Checked;
      // Selection may not have been queried on this edit. Preserve the saved
      // preferred token unless the operator explicitly picks a loaded profile.
      if (profiles.SelectedItem is OnvifMediaProfile profile)
      {
        edited.PreferredProfileToken = profile.Token;
        edited.PreferMedia2 = profile.Media2;
      }
      edited.AutoConnect = autoConnect.Checked || autoPlay.Checked;
      edited.AutoPlay = autoPlay.Checked;
      edited.AutoReconnect = autoReconnect.Checked;
      DialogResult = DialogResult.OK;
      Close();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing) { operations?.Cancel(); operations?.Dispose(); }
      base.Dispose(disposing);
    }

    private sealed class DeviceOption(OnvifCameraDevice device)
    {
      internal OnvifCameraDevice Device => device;
      public override string ToString() =>
        Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var uri)
          ? $"{uri.Host}:{uri.Port}" : "ONVIF camera";
    }
  }
}
