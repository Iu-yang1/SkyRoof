using System.ComponentModel;
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

    private void RefreshLanguageCaptions()
    {
      Text = SettingsLanguage.Text("设置", "Settings");
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
