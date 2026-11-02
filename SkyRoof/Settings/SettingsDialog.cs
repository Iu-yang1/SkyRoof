using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SkyRoof.IF;
using System.Text.RegularExpressions;
using Accessibility;
using VE3NEA;

namespace SkyRoof

{
  internal partial class SettingsDialog : Form
  {
    private readonly Context ctx;
    private readonly List<string> ChangedFields = new();
    private UiLanguage appliedLanguage = UiLanguage.SimplifiedChinese;
    private ComboBox? languageSelector;
    private Label? languageLabel;
    private Button? cableOfficialDownload;
    private Button? cableRefresh;
    private Button? cableUninstall;


    internal SettingsDialog()
    {
      InitializeComponent();
    }

    internal SettingsDialog(Context ctx, string section = null)
    {
      InitializeComponent();
      this.ctx = ctx;
      appliedLanguage = ctx.Settings.Ui.Language;
      SettingsLanguage.Set(appliedLanguage);
      grid.SelectedObject = Utils.DeepClone(ctx.Settings);
      InstallLanguageSelector();
      InstallSignedCableToolbar();

      if (section == null)
        grid.CollapseAllGridItems();
      else
        grid.ExpandTopLevelProperties(grid.GetItemByFullName(section));
    }

    private void InstallLanguageSelector()
    {
      // Place the selector outside the PropertyGrid. It must remain reachable
      // even when categories are collapsed, and is never serialized as a
      // translated string or accidentally submitted as a radio setting.
      var languageBar = new Panel
      {
        Dock = DockStyle.Top, Height = 40,
        Padding = new Padding(8, 5, 8, 3),
        BackColor = SystemColors.Control
      };
      languageLabel = new Label
      {
        AutoSize = false, Location = new Point(9, 9),
        Width = 185, Height = 24,
        TextAlign = ContentAlignment.MiddleLeft
      };
      languageSelector = new ComboBox
      {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 171, Height = 28,
        Anchor = AnchorStyles.Top | AnchorStyles.Right
      };
      languageSelector.Items.AddRange(new object[]
      {
        "简体中文", "English"
      });
      languageBar.Controls.Add(languageSelector);
      languageBar.Controls.Add(languageLabel);
      languageBar.Resize += (_, _) =>
      {
        if (languageSelector == null) return;
        languageSelector.Left = Math.Max(194, languageBar.ClientSize.Width -
          languageSelector.Width - 10);
      };
      languageSelector.SelectedIndex = appliedLanguage ==
        UiLanguage.English ? 1 : 0;
      languageSelector.SelectedIndexChanged += (_, _) =>
      {
        if (grid.SelectedObject is not Settings edited) return;
        UiLanguage selected = languageSelector.SelectedIndex == 1
          ? UiLanguage.English : UiLanguage.SimplifiedChinese;
        edited.Ui.Language = selected;
        SettingsLanguage.Set(selected);
        // Rebuild labels while preserving the same edited settings instance.
        // This avoids discarding uncommitted changes in other categories.
        grid.SelectedObject = null;
        grid.SelectedObject = edited;
        RefreshLanguageCaptions();
      };
      Controls.Add(languageBar);
      // Dock order: top bar precedes the fill PropertyGrid.
      Controls.SetChildIndex(languageBar, 0);
      RefreshLanguageCaptions();
    }

    private void InstallSignedCableToolbar()
    {
      var bar = new FlowLayoutPanel
      {
        Dock = DockStyle.Top, Height = 37,
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        Padding = new Padding(8, 3, 8, 3)
      };
      cableOfficialDownload = new Button { Width = 166, Height = 28 };
      cableUninstall = new Button { Width = 166, Height = 28 };
      cableRefresh = new Button { Width = 158, Height = 28 };
      cableOfficialDownload.Click += async (_, _) =>
        await ManageVendorCableAsync("Install");
      cableUninstall.Click += async (_, _) =>
        await ManageVendorCableAsync("Uninstall");
      cableRefresh.Click += (_, _) => grid.Refresh();
      bar.Controls.Add(cableOfficialDownload);
      bar.Controls.Add(cableUninstall);
      bar.Controls.Add(cableRefresh);
      Controls.Add(bar);
      Controls.SetChildIndex(bar, 1);
      RefreshLanguageCaptions();
    }

    private async Task ManageVendorCableAsync(string action)
    {
      bool uninstall = action == "Uninstall";
      string folder = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "VB-CABLE");
      string manager = Path.Combine(folder, "Manage-VbCable.ps1");
      string archive = Path.Combine(folder, "VBCABLE_Driver_Pack45.zip");
      string managerLog = Path.Combine(folder, "VB-CABLE-manager.log");
      if (!File.Exists(manager) || !File.Exists(archive))
      {
        DialogResult redirect = MessageBox.Show(SettingsLanguage.Text(
            "此构建未捆绑官方 VB-CABLE 安装包。是否打开 VB-Audio 官方下载网站？",
            "This build has no original VB-CABLE package. Open the vendor's official website?"),
          "VB-CABLE", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (redirect == DialogResult.Yes)
          Process.Start(new ProcessStartInfo(IfAfSignedCableDiscovery.VbCableUrl)
          { UseShellExecute = true });
        return;
      }
      if (uninstall && ctx.Settings.IfAf.Enabled)
      {
        MessageBox.Show(SettingsLanguage.Text(
          "先禁用 IF→AF 并应用设置，再卸载共享虚拟声卡。",
          "Disable and apply IF->AF before removing the shared audio cable."),
          "VB-CABLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      string warning = uninstall ?
        SettingsLanguage.Text(
          "VB-CABLE 可能也被其他软件使用。仅允许卸载 SkyRoof 自己安装的实例。确认卸载并在完成后重新启动 Windows 吗？",
          "Other programs may use VB-CABLE. Only SkyRoof-installed instances can be removed. Proceed and reboot afterward?") :
        SettingsLanguage.Text(
          "VB-CABLE 是 VB-Audio 软件（www.vb-cable.com），采用捐赠软件模式，欢迎自愿捐赠。即将经 UAC 提权调用未修改的原厂安装程序，安装后可能需要重启。是否继续？",
          "VB-CABLE by VB-Audio (www.vb-cable.com) is donationware; contributions are welcome. Run the original vendor installer with UAC and reboot if needed?");
      if (MessageBox.Show(warning, "VB-CABLE", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        return;
      if (cableOfficialDownload != null) cableOfficialDownload.Enabled = false;
      if (cableUninstall != null) cableUninstall.Enabled = false;
      try
      {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
          "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(exe)
        { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(manager);
        start.ArgumentList.Add("-Action");
        start.ArgumentList.Add(action);
        start.ArgumentList.Add("-Consent");
        using var process = Process.Start(start) ??
          throw new InvalidOperationException("Cannot start the original vendor package manager.");
        await process.WaitForExitAsync();
        grid.Refresh();
        if (process.ExitCode != 0 && process.ExitCode != 3010)
        {
          // The UAC-elevated PowerShell window may close before its error is
          // readable. Show the manager's last diagnostic phase directly.
          string evidence = SettingsLanguage.Text(
            "未找到驱动安装日志。", "No driver installation log was created.");
          try
          {
            if (File.Exists(managerLog))
            {
              string log = File.ReadAllText(managerLog);
              int marker = log.LastIndexOf("VB-CABLE FAILED at stage [",
                StringComparison.Ordinal);
              if (marker >= 0) log = log.Substring(marker);
              evidence = log.Length > 1800 ? log.Substring(log.Length - 1800) : log;
            }
          }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
          throw new InvalidOperationException(
            SettingsLanguage.Text(
              $"VB-CABLE 安装失败（退出码 {process.ExitCode}）。\n诊断日志：{managerLog}\n\n{evidence}",
              $"VB-CABLE installation failed (exit code {process.ExitCode}).\nDiagnostic log: {managerLog}\n\n{evidence}"));
        }
        MessageBox.Show(SettingsLanguage.Text(
          "原厂安装/卸载操作已返回。建议重新启动 Windows，再在设置中重新检测 CABLE Input / CABLE Output。",
          "The vendor installer finished. Restart Windows and rescan both CABLE Input and CABLE Output."),
          "VB-CABLE", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
      catch (Exception ex)
      {
        MessageBox.Show(ex.Message, "VB-CABLE", MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
      }
      finally
      {
        if (cableOfficialDownload != null) cableOfficialDownload.Enabled = true;
        if (cableUninstall != null) cableUninstall.Enabled = true;
      }
    }

    private void RefreshLanguageCaptions()
    {
      Text = SettingsLanguage.Text("设置", "Settings");
      if (cableOfficialDownload != null)
        cableOfficialDownload.Text = SettingsLanguage.Text("安装 VB-CABLE", "Install VB-CABLE");
      if (cableUninstall != null)
        cableUninstall.Text = SettingsLanguage.Text("卸载 VB-CABLE", "Remove VB-CABLE");
      if (cableRefresh != null)
        cableRefresh.Text = SettingsLanguage.Text("重新检测音频端点", "Rescan audio endpoints");
      if (languageLabel != null)
        languageLabel.Text = SettingsLanguage.Text("界面语言", "Interface language");
      applyBtn.Text = SettingsLanguage.Text("应用", "Apply");
      cancelBtn.Text = SettingsLanguage.Text("取消", "Cancel");
      okBtn.Text = SettingsLanguage.Text("确定", "OK");
      resetToolStripMenuItem.Text = SettingsLanguage.Text("重置", "Reset");
    }

    private void SettingsDialog_Shown(object? sender, EventArgs e)
    {
      ctx.Settings.Ui.RestorePropertyGridLabelWidth("SettingsDialog", grid);
    }

    private void SettingsDialog_FormClosing(object? sender, FormClosingEventArgs e)
    {
      ctx.Settings.Ui.SavePropertyGridLabelWidth("SettingsDialog", grid);
      // Cancel must also undo a language preview. Apply commits the preview
      // independently of subsequent edits, matching standard Settings UX.
      if (DialogResult != DialogResult.OK)
        SettingsLanguage.Set(appliedLanguage);
    }

    private void SelectSection(string section)
    {
      if (section == null) return;

      var gridItem = grid.GetItemByFullName(section);
      grid.ExpandAndSelect(gridItem);
    }

    private void resetToolStripMenuItem_Click(object sender, EventArgs e)
    {
      string label = PropertyGridEx.GetItemProperty(grid.SelectedGridItem, "HelpKeyword");

      // collection properties don't have a [DefaultValue]; handle their reset explicitly
      var settings = (Settings?)grid.SelectedObject;
      switch (label)
      {
        case "SkyRoof.TransverterSettings.SdrBands":
          settings!.Transverter.ResetSdrBands();
          grid.Refresh();
          break;

        case "SkyRoof.TransverterSettings.CatBands":
          settings!.Transverter.ResetCatBands();
          grid.Refresh();
          break;

        default:
          grid.ResetSelectedProperty();
          break;
      }

      ChangedFields.Add(label);
      RefreshConditionalSettingsWhenModeChanges(label);
    }

    private void RefreshConditionalSettingsWhenModeChanges(string? name)
    {
      bool rotator = name is
        "SkyRoof.RotatorSettings.DriverMode" or
        "SkyRoof.RotatorSettings.NativeTransport" or
        "SkyRoof.RotatorSettings.NativeProtocol";

      bool spectrum = name is
        "SkyRoof.IcomLanSpectrumSettings.Source" or
        "SkyRoof.IcomLanSpectrumSettings.ControlPath";

      if (!rotator && !spectrum) return;

      // PropertyGrid caches nested descriptors while a dropdown is open.
      // Rebuild the affected subsection after its edit is committed, without
      // losing values that are now hidden by the other mode.
      BeginInvoke(new Action(() =>
      {
        if (IsDisposed || grid.IsDisposed) return;
        if (grid.SelectedObject is Settings settings)
        {
          if (rotator) TypeDescriptor.Refresh(settings.Rotator);
          if (spectrum) TypeDescriptor.Refresh(settings.IcomLanSpectrum);
        }
        grid.Refresh();
      }));
    }

    private void applyBtn_Click(object sender, EventArgs e)
    {
      ctx.Settings = Utils.DeepClone((Settings)grid.SelectedObject);
      ApplyChangedSettings();
    }

    private void okBtn_Click(object sender, EventArgs e)
    {
      ctx.Settings = (Settings)grid.SelectedObject;
      ApplyChangedSettings();
    }




    //--------------------------------------------------------------------------------------------------------------
    //                                        validate changes
    //--------------------------------------------------------------------------------------------------------------
    private void grid_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
    {
      bool canChange = true;
      string label = PropertyGridEx.GetItemProperty(e.ChangedItem, "HelpKeyword");

      switch (label)
      {
        case "SkyRoof.UserSettings.Call":
          canChange = ValidateByRegex(e, Utils.CallsignRegex, "callsign", CharacterCasing.Upper);
          break;

        case "SkyRoof.UserSettings.Square":
          canChange = ValidateByRegex(e, Utils.GridSquare6Regex, "grid square", CharacterCasing.Upper);
          break;

        case "SkyRoof.UserSettings.Altitude":
          ValidateInt(e, 8849);
          break;

        case "SkyRoof.AnnouncerSettings.Voice":
          ctx.Announcer.SayVoiceName(e.ChangedItem.Value!.ToString());
          break;

        case "SkyRoof.AnnouncerSettings.Volume":
          ValidateInt(e, 100);
          break;

        case "SkyRoof.AosAnnouncement.Minutes":
          ValidateInt(e, 5);
          break;

        case "SkyRoof.PositionAnnouncement.Degrees":
          ValidateInt(e, 30, 1);
          break;

        case "SkyRoof.OutputStreamSettings.Gain":
          ValidateInt(e, 60, -60);
          break;

        case "SkyRoof.Ft4WaterfallSettings.Bandwidth":
          ValidateInt(e, 5000, 2000);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.WaterfallRows":
          ValidateInt(e, 800, 40);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.SpectrumHeightPercent":
          ValidateInt(e, 80, 20);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.WaterfallBrightness":
          ValidateInt(e, 80, -80);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.WaterfallContrast":
          ValidateInt(e, 250, 25);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.ScopeEdgeNumber":
          ValidateInt(e, 4, 1);
          break;

        case "SkyRoof.IcomLanSpectrumSettings.ScopeReferenceLevelDb":
          ValidateHalfDb(e);
          break;

        case "SkyRoof.Ft4ConsoleSettings.TxGain":
          ValidateInt(e, 0, -60);
          break;

        case "SkyRoof.Ft4ConsoleSettings.EnableTransmit":
          canChange = ValidateFt4Transmit(e);
          break;

        case "SkyRoof.Ft4ConsoleSettings.TxWatchDog":
          ValidateInt(e, 20, 1);
          break;

        case "SkyRoof.CwConsoleSettings.CwKeyerPort":
          ValidateInt(e, 65535, 1);
          break;

        case "SkyRoof.RotatorSettings.StepSize":
          ValidateFloat(e, 30, 0.01f);
          break;
      }

      if (canChange)
      {
        ChangedFields.Add(label);
        RefreshConditionalSettingsWhenModeChanges(label);
      }
    }

    private bool ValidateFt4Transmit(PropertyValueChangedEventArgs e)
    {
      var value = (bool)e.ChangedItem!.Value!;
      var call = ((Settings)grid.SelectedObject!).User.Call;

      bool error = value && string.IsNullOrEmpty(call);

      if (error)
      {
        MessageBox.Show("Enter your callsign before enabling FT4 transmission.", "SkyRoof", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        e.ChangedItem.PropertyDescriptor.SetValue(e.ChangedItem.Parent.Value, e.OldValue);
      }

      return !error;
    }

    private bool ValidateByRegex(PropertyValueChangedEventArgs e, Regex regEx, string fieldName, CharacterCasing casing)
    {
      string newValue = e.ChangedItem.Value.ToString();
      string cleanValue = Utils.SetCasing(newValue.Trim(), casing);

      if (!regEx.IsMatch(cleanValue))
      {
        e.ChangedItem.PropertyDescriptor.SetValue(e.ChangedItem.Parent.Value, e.OldValue);
        MessageBox.Show($"Invalid {fieldName}: \"{newValue}\"", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        return false;
      }

      if (cleanValue != newValue) e.ChangedItem.PropertyDescriptor.SetValue(e.ChangedItem.Parent.Value, cleanValue);
      return true;
    }

    private void ValidateInt(PropertyValueChangedEventArgs e, int max, int min = 0)
    {
      int cleanValue = Math.Max(min, Math.Min(max, (int)e.ChangedItem.Value));
      e.ChangedItem.PropertyDescriptor.SetValue(e.ChangedItem.Parent.Value, cleanValue);
    }
    private void ValidateFloat(PropertyValueChangedEventArgs e, float max, float min = 0)
    {
      float cleanValue = Math.Max(min, Math.Min(max, (float)e.ChangedItem.Value));
      e.ChangedItem.PropertyDescriptor.SetValue(e.ChangedItem.Parent.Value, cleanValue);
    }

    private void ValidateHalfDb(PropertyValueChangedEventArgs e)
    {
      double value =
        Convert.ToDouble(
          e.ChangedItem.Value);

      double cleanValue =
        Math.Clamp(
          Math.Round(
            value * 2,
            MidpointRounding.AwayFromZero) / 2.0,
          -20.0,
          20.0);

      e.ChangedItem.PropertyDescriptor.SetValue(
        e.ChangedItem.Parent.Value,
        cleanValue);
    }




    //--------------------------------------------------------------------------------------------------------------
    //                                            apply changes
    //--------------------------------------------------------------------------------------------------------------
    private void ApplyChangedSettings()
    {
      if (ChangedFields.Contains("SkyRoof.UserSettings.Square") ||
          ChangedFields.Contains("SkyRoof.UserSettings.Altitude"))
        ctx.MainForm.SetLocation();

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.OutputStreamSettings.")))
        ctx.MainForm.ApplyOutputStreamSettings();
      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.IF.IfAfSettings.")))
        ctx.MainForm.ApplyIfAfSettings();

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.KissServerSettings.")))
        ctx.MainForm.ApplyKissServerSettings();

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.AudioSettings.")))
        ctx.MainForm.ApplyAudioSettings();

      if (ChangedFields.Exists(s =>
            s.StartsWith("SkyRoof.CwConsoleSettings.")))
      {
        ctx.CwAudio?.ApplySettings();
        ctx.MainForm.UpdateCwConsoleMenuText();

        if (ctx.CwTransmit != null)
        {
          try
          {
            ctx.CwTransmit
              .ApplySettingsAsync(
                ctx.Settings.CwConsole)
              .GetAwaiter()
              .GetResult();
          }
          catch (Exception ex)
          {
            MessageBox.Show(
              "CW transmit settings were applied, but the active transmitter could not be stopped cleanly:\r\n\r\n" +
              ex.Message,
              "CW TX",
              MessageBoxButtons.OK,
              MessageBoxIcon.Warning);
          }
        }
      }

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.Announcement.Minutes")) ||
          ChangedFields.Exists(s => s.StartsWith("SkyRoof.Announcement.Enabled")))
        ctx.Announcer.RebuildQueue();

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.CatSettings.")) ||
        ChangedFields.Exists(s => s.StartsWith("SkyRoof.CatRadioSettings")) ||
        ChangedFields.Exists(s => s.StartsWith("SkyRoof.EmbeddedSkyCatSettings.")))
      {
        ctx.CatControl.ApplySettings();
        ctx.MainForm.ShowCatStatus();
      }

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.RotatorSettings.")))
        ctx.RotatorControl.ApplySettings(true);

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.WaterfallSettings.")))
        ctx.WaterfallPanel?.ApplySettings();

      if (ChangedFields.Exists(
            s => s.StartsWith(
              "SkyRoof.IcomLanSpectrumSettings.")))
      {
        bool transportChanged =
          ChangedFields.Exists(
            s =>
              s is
                "SkyRoof.IcomLanSpectrumSettings.RadioAddress" or
                "SkyRoof.IcomLanSpectrumSettings.SerialPort" or
                "SkyRoof.IcomLanSpectrumSettings.Source" or
                "SkyRoof.IcomLanSpectrumSettings.SkyCatScopePort" or
                "SkyRoof.IcomLanSpectrumSettings.DirectLanControlPort" or
                "SkyRoof.IcomLanSpectrumSettings.DirectLanUsername" or
                "SkyRoof.IcomLanSpectrumSettings.DirectLanPassword" or
                "SkyRoof.IcomLanSpectrumSettings.DirectLanClientName");

        bool controlChanged =
          ChangedFields.Exists(
            s =>
              s is
                "SkyRoof.IcomLanSpectrumSettings.ControlPath" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeBand" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeEdgeNumber" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeReferenceLevelDb" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeSweepSpeed" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeDuringTx" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeCenterType" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeVbw" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeMarkerPosition");

        bool advancedControlChanged =
          ChangedFields.Exists(
            s =>
              s is
                "SkyRoof.IcomLanSpectrumSettings.ScopeDuringTx" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeCenterType" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeVbw" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeRbw" or
                "SkyRoof.IcomLanSpectrumSettings.ScopeMarkerPosition");

        if (advancedControlChanged)
          ctx.Settings.IcomLanSpectrum
            .ManageAdvancedScopeControls =
            true;

        if (transportChanged)
          ctx.IcomLanSpectrumPanel?.ApplySettings();
        else if (controlChanged)
          ctx.IcomLanSpectrumPanel?.ApplyControlSettings();
        else
          ctx.IcomLanSpectrumPanel?.ApplyDisplaySettings();
      }

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.OrbitSourceSettings.")))
        ctx.SatnogsDb.ConfigureSources(ctx.Settings.OrbitSources);

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.AmsatSettings.")))
        if (ctx.Settings.Amsat.Enabled)
          ctx.AmsatStatusLoader.GetStatusesAsync().DoNotAwait();
        else
          ctx.GroupViewPanel?.ShowAmsatStatuses();

      if (ChangedFields.Exists(s => s.StartsWith("SkyRoof.QsoEntrySettings.")))
        ctx.QsoEntryPanel?.ApplySettings();

      if (ChangedFields.Exists(s => 
        s.StartsWith("SkyRoof.Ft4ConsoleSettings.") ||
        s.StartsWith("SkyRoof.Ft4ReceiveSettings.") ||
        s.StartsWith("SkyRoof.Ft4TransmitSettings.") ||
        s.StartsWith("SkyRoof.Ft4WaterfallSettings.") ||
        s.StartsWith("SkyRoof.Ft4MessagesSettings.") ||
        s.StartsWith("SkyRoof.UdpSenderSettings.") ||
        s.StartsWith("SkyRoof.Ft4BackgroundColors.") ||
        s.StartsWith("SkyRoof.UserSettings.Call")
        ))
        ctx.Ft4ConsolePanel?.ApplySettings();

      if (appliedLanguage != ctx.Settings.Ui.Language)
      {
        SettingsLanguage.Set(ctx.Settings.Ui.Language);
        appliedLanguage = ctx.Settings.Ui.Language;
        ctx.Settings.SaveToFile();
      }
      ChangedFields.Clear();
    }
  }
}
