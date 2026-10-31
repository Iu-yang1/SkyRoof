using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using SkyRoof.SSTV;
using VE3NEA;
using NAudio.CoreAudioApi;
using VE3NEA.SkySSTV;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// Operator-facing SSTV picture workspace. The RX image source is the
  /// existing satellite decoder; WAV export is explicitly offline and never
  /// invokes a radio PTT/CAT API. Retains the rest of SkyRoof unchanged.
  /// </summary>
  public sealed class SstvConsolePanel : DockContent
  {
    private readonly Context ctx;
    private readonly SstvAudioReceiveSession receiveSession = new();
    private readonly SstvTransmitSession transmitSession;
    private readonly ComboBox txOutputBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 245
    };
    private readonly CheckBox txCwId = new()
    {
      Text = "CW ID", AutoSize = true,
      Padding = new Padding(2, 7, 2, 0)
    };
    private readonly CheckBox txFskId = new()
    {
      Text = "FSK ID", AutoSize = true,
      Padding = new Padding(2, 7, 2, 0)
    };
    private readonly CheckBox txApproval = new()
    {
      Text = "已确认频率 / 功率", AutoSize = true,
      Padding = new Padding(3, 7, 3, 0)
    };
    private readonly Button txSendButton = new()
    {
      Text = "发射图片", Width = 90, Height = 30, Margin = new Padding(4),
      Enabled = false
    };
    private readonly Button txStopButton = new()
    {
      Text = "停止发射", Width = 90, Height = 30, Margin = new Padding(4),
      Enabled = false
    };
    private readonly ComboBox rxModeBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 178
    };
    private readonly ComboBox rxSourceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 152
    };
    private readonly ComboBox rxDeviceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 280
    };
    private readonly CheckBox autoVisCorrection = new()
    {
      Text = "自动校频", AutoSize = true, Checked = true,
      Padding = new Padding(3, 7, 3, 0)
    };
    private readonly NumericUpDown pitchCorrection = new()
    {
      Minimum = -500, Maximum = 500, Increment = 5,
      DecimalPlaces = 0, Width = 72, Height = 30
    };
    private readonly NumericUpDown imageSlantCorrection = new()
    {
      Minimum = -1000, Maximum = 1000, Increment = 25,
      DecimalPlaces = 0, Width = 76, Height = 30
    };
    private readonly Button rxToggleButton = new()
    {
      Text = "开始接收", Width = 96, Height = 30,
      Margin = new Padding(4)
    };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly SstvTxToneReference rxToneReference = new()
    {
      Dock = DockStyle.Bottom, Height = 90
    };
    private readonly PictureBox rxPicture = new()
    {
      Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 33, 40),
      SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly PictureBox txPicture = new()
    {
      Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 33, 40),
      SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly ComboBox modeBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 145
    };
    private readonly TextBox overlayText = new()
    {
      Width = 185, MaxLength = 512,
      PlaceholderText = "呼号 / {MYGRID} / {SAT} / {UTC}"
    };
    private readonly ComboBox templateBox = new()
    {
      Width = 110, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox myCallInput = new()
    {
      Width = 116, MaxLength = 60, PlaceholderText = "己方呼号"
    };
    private readonly TextBox theirCallInput = new()
    {
      Width = 116, MaxLength = 60, PlaceholderText = "对方呼号"
    };
    private readonly ComboBox textLayerBox = new()
    {
      Width = 114, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly NumericUpDown textSizeBox = new()
    {
      Width = 66, Minimum = 2, Maximum = 25, Increment = 0.5m,
      DecimalPlaces = 1
    };
    private readonly Button textColorButton = new()
    {
      Text = "文字颜色", Width = 88, Height = 30,
      Margin = new Padding(4)
    };
    private readonly SstvTextLayer[] textLayers =
    {
      SstvTextLayer.MyCallDefault,
      SstvTextLayer.TheirCallDefault,
      SstvTextLayer.CustomDefault
    };
    private readonly RectangleF[] textBounds = new RectangleF[8];
    private readonly List<SstvAdditionalCaption> extraCaptions = new();
    private readonly TextBox activeCaptionInput = new()
    {
      Width = 185, MaxLength = 512, PlaceholderText = "支持 {MYCALL} 等变量"
    };
    private readonly ComboBox macroSelector = new()
    {
      Width = 185, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private const int MaxTextLayers = 8;
    private bool movingText;
    private bool updatingTextControls;
    private PointF textDragOffset;
    private readonly List<Bitmap> txRecentImages = new();
    private readonly Dictionary<string, (DateTime Utc, long Size, Bitmap Image)>
      archivedThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox txQuickModes = new()
    {
      Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None
    };
    private readonly FlowLayoutPanel txThumbnailStrip = new()
    {
      Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true,
      BackColor = Color.FromArgb(30, 33, 39),
      Padding = new Padding(4)
    };
    private readonly TextBox txQsoNote = new() { MaxLength = 256 };
    private readonly TextBox txRstSent = new() { Text = "595", MaxLength = 8 };
    private readonly TextBox txRstReceived = new() { Text = "595", MaxLength = 8 };
    private readonly TextBox txContactName = new() { MaxLength = 64 };
    private readonly TextBox txContactQth = new() { MaxLength = 96 };
    private bool syncingQsoEntry;
    private readonly ToolTip txThumbnailTips = new();
    private readonly Stack<Bitmap> undoImages = new();
    private readonly Stack<Bitmap> redoImages = new();
    private const int HistoryLimit = 10;
    private readonly ListBox archiveList = new() { Dock = DockStyle.Fill };
    private readonly ListBox qsoList = new() { Dock = DockStyle.Fill };
    private readonly TextBox qsoCall = new() { Width = 130 };
    private readonly TextBox qsoNotes = new() { Width = 250 };
    private readonly Label rxStatus = new()
    {
      Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft,
      Text = "等待接收图像"
    };
    private readonly Label txStatus = new()
    {
      Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft,
      Text = "选择图片和模式，即可保存 SSTV 音频"
    };
    private CancellationTokenSource? replayStop;
    private CancellationTokenSource? txOperationStop;
    private Bitmap? txBase;
    private string? lastReceivedMode;
    private bool closed;

    public SstvConsolePanel(Context ctx)
    {
      this.ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
      ctx.SstvConsolePanel = this;
      transmitSession = new SstvTransmitSession(ctx);
      Text = "SSTV";
      TabText = "SSTV";
      KeyPreview = true;
      KeyDown += (_, e) =>
      {
        if (e.KeyCode == Keys.Escape && txStopButton.Enabled)
        {
          StopTransmit();
          e.Handled = true;
          e.SuppressKeyPress = true;
        }
      };
      Width = 980;
      Height = 720;
      MinimumSize = new Size(650, 420);
      Controls.Add(tabs);
      BuildRxTab();
      BuildTxTab();
      BuildHistoryTab();
      BuildQsoTab();
      foreach (SstvTxModeProfile mode in SstvTxModeProfile.All)
        modeBox.Items.Add(mode);
      modeBox.DisplayMember = nameof(SstvTxModeProfile.Name);
      txQuickModes.Items.AddRange(SstvTxModeProfile.All.Cast<object>().ToArray());
      if (modeBox.Items.Count > 0) modeBox.SelectedIndex = 0;
      txQuickModes.SelectedIndex = modeBox.SelectedIndex;
      modeBox.SelectedIndexChanged += (_, _) =>
      {
        if (txQuickModes.SelectedIndex != modeBox.SelectedIndex)
          txQuickModes.SelectedIndex = modeBox.SelectedIndex;
        RefreshTxPreview();
      };
      txQuickModes.SelectedIndexChanged += (_, _) =>
      {
        if (txQuickModes.SelectedIndex >= 0 &&
            modeBox.SelectedIndex != txQuickModes.SelectedIndex)
          modeBox.SelectedIndex = txQuickModes.SelectedIndex;
      };
      myCallInput.Text = ctx.Settings.User.Call ?? "";
      overlayText.Text = "";
      textLayerBox.Items.AddRange(new object[]
      {
        "己方呼号", "对方呼号", "自定义文字"
      });
      textLayerBox.SelectedIndex = 0;
      textLayerBox.SelectedIndexChanged += (_, _) => UpdateTextEditorControls();
      foreach (var variable in SstvTextMacros.Catalog)
        macroSelector.Items.Add(new MacroEntry(variable.Token, variable.Label));
      if (macroSelector.Items.Count > 0) macroSelector.SelectedIndex = 0;
      activeCaptionInput.TextChanged += (_, _) =>
      {
        if (updatingTextControls || textLayerBox.SelectedIndex < 0) return;
        SetCaptionText(textLayerBox.SelectedIndex, activeCaptionInput.Text);
        RefreshTxPreview();
      };
      textSizeBox.ValueChanged += (_, _) =>
      {
        if (updatingTextControls) return;
        int index = Math.Max(0, textLayerBox.SelectedIndex);
        SetTextLayer(index, GetTextLayer(index) with
          { SizePercent = (float)textSizeBox.Value });
        RefreshTxPreview();
      };
      textColorButton.Click += (_, _) => PickTextColor();
      myCallInput.TextChanged += (_, _) =>
      {
        SyncActiveCaptionText();
        RefreshTxPreview();
      };
      theirCallInput.TextChanged += (_, _) =>
      {
        if (!syncingQsoEntry)
        {
          syncingQsoEntry = true;
          try { qsoCall.Text = theirCallInput.Text; }
          finally { syncingQsoEntry = false; }
        }
        SyncActiveCaptionText();
        RefreshTxPreview();
      };
      txQsoNote.TextChanged += (_, _) =>
      {
        if (syncingQsoEntry) return;
        syncingQsoEntry = true;
        try { qsoNotes.Text = txQsoNote.Text; }
        finally { syncingQsoEntry = false; }
      };
      overlayText.TextChanged += (_, _) => SyncActiveCaptionText();
      qsoNotes.TextChanged += (_, _) =>
      {
        if (syncingQsoEntry) return;
        syncingQsoEntry = true;
        try { txQsoNote.Text = qsoNotes.Text; }
        finally { syncingQsoEntry = false; }
      };
      txPicture.MouseDown += OnTxPictureMouseDown;
      txPicture.MouseMove += OnTxPictureMouseMove;
      txPicture.MouseUp += (_, _) =>
      {
        if (movingText)
        {
          movingText = false;
          txPicture.Cursor = Cursors.Default;
          RefreshTxPreview(); // final bicubic render after fast drag frames
        }
      };
      txPicture.MouseWheel += OnTxPictureMouseWheel;
      txPicture.MouseEnter += (_, _) => txPicture.Focus();
      txPicture.TabStop = true;
      txPicture.Paint += (_, e) => DrawTextSelection(e.Graphics);
      UpdateTextEditorControls();
      rxSourceBox.Items.AddRange(new object[]
      {
        "声卡 AF", "RS-BA1 回环 AF", "声卡 12 kHz IF", "回环 12 kHz IF"
      });
      rxSourceBox.SelectedIndexChanged += (_, _) => PopulateAudioDevices();
      rxSourceBox.SelectedIndex = 0;
      rxModeBox.Items.Add("自动识别 SSTV");
      // Display every TX mode in RX. Robot/PD are decoded by SkySignals;
      // remaining profiles use the manual/live raster receiver.
      foreach (SstvTxModeProfile profile in SstvTxModeProfile.All)
        rxModeBox.Items.Add(profile);
      rxModeBox.SelectedIndex = 0;
      rxToggleButton.Click += (_, _) => ToggleReceive();
      pitchCorrection.ValueChanged += (_, _) =>
        receiveSession.ManualCorrectionHz = (int)pitchCorrection.Value;
      autoVisCorrection.CheckedChanged += (_, _) =>
        receiveSession.AutoVisEnabled = autoVisCorrection.Checked;
      receiveSession.ImageAvailable += OnAudioReceived;
      receiveSession.RasterImageAvailable += OnRasterReceived;
      receiveSession.VisSynchronized += OnVisSynchronized;
      receiveSession.SpectrumAvailable += OnSpectrumAvailable;
      receiveSession.FskStationIdentified += OnLiveFskStationIdentified;
      overlayText.TextChanged += (_, _) => RefreshTxPreview();
      qsoCall.TextChanged += (_, _) =>
      {
        if (!syncingQsoEntry)
        {
          syncingQsoEntry = true;
          try { theirCallInput.Text = qsoCall.Text; }
          finally { syncingQsoEntry = false; }
        }
        RefreshTxPreview();
      };
      templateBox.Items.AddRange(new object[]
      {
        "经典呼号", "QSL 卡片", "卫星横幅", "纯图片"
      });
      templateBox.SelectedIndex = 0;
      templateBox.SelectedIndexChanged += (_, _) =>
      {
        if (templateBox.SelectedItem is SstvUserTemplate selected)
          ApplyUserTemplate(selected);
        RefreshTxPreview();
      };
      ReloadUserTemplates();
      archiveList.DoubleClick += (_, _) => ShowSelectedHistory();
      txSendButton.Click += (_, _) => _ = SendImageAsync();
      txApproval.CheckedChanged += (_, _) => UpdateTxAvailability();
      // A changed audio route invalidates the operator's previous approval.
      txOutputBox.SelectedIndexChanged += (_, _) =>
      {
        txApproval.Checked = false;
        UpdateTxAvailability();
      };
      txStopButton.Click += (_, _) => StopTransmit();

      PopulateTxOutputDevices();
      ReloadHistory();
      RefreshTxThumbnails();
      ReloadQsoLog();
    }

    private static Button Btn(string text, Action action, int width = 95)
    {
      var b = new Button
      {
        Text = text, Width = width, Height = 30,
        Margin = new Padding(4, 4, 4, 4)
      };
      b.Click += (_, _) => action();
      return b;
    }

    private static FlowLayoutPanel Toolbar() => new()
    {
      Dock = DockStyle.Top, Height = 43, WrapContents = false,
      AutoScroll = false, Padding = new Padding(6, 2, 6, 2)
    };

    private void BuildRxTab()
    {
      var page = new TabPage("接收 RX");
      var bar = Toolbar();
      bar.Controls.Add(rxSourceBox);
      bar.Controls.Add(rxDeviceBox);
      bar.Controls.Add(rxToggleButton);
      var tuning = Toolbar();
      tuning.Controls.Add(rxModeBox);
      tuning.Controls.Add(new Label
      {
        Text = "校频 Hz", Width = 57, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      tuning.Controls.Add(pitchCorrection);
      tuning.Controls.Add(autoVisCorrection);
      tuning.Controls.Add(new Label
      {
        Text = "图像倾斜", Width = 66, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      tuning.Controls.Add(imageSlantCorrection);
      var actions = Toolbar();
      actions.Controls.Add(Btn("导入 WAV", () => _ = ReplayWavAsync()));
      actions.Controls.Add(Btn("手动开始", ManualStartReceive, 98));
      actions.Controls.Add(Btn("保存图片", SaveReceivedImage));
      actions.Controls.Add(Btn("复制图片", () =>
      {
        if (rxPicture.Image != null) Clipboard.SetImage(rxPicture.Image);
      }));
      actions.Controls.Add(Btn("接收记录", () => { ReloadHistory(); tabs.SelectedIndex = 2; }));
      page.Controls.Add(rxPicture);
      page.Controls.Add(rxToneReference);
      page.Controls.Add(rxStatus);
      page.Controls.Add(actions);
      page.Controls.Add(tuning);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    /// <summary>
    /// MMSSTV-inspired TX workspace: large picture at left, vertical
    /// mode picker and station/QSO/caption controls at right, image thumbnails
    /// underneath. All controls stay in the same native WinForms tab and
    /// preserve the existing audio/PTT model.
    /// </summary>
    private void BuildTxTab()
    {
      var page = new TabPage("发送 TX");
      var bar = Toolbar();
      bar.Controls.Add(Btn("打开图片", LoadImage));
      bar.Controls.Add(Btn("粘贴", PasteImage, 65));
      bar.Controls.Add(Btn("撤销", UndoImage, 60));
      bar.Controls.Add(Btn("重做", RedoImage, 60));
      bar.Controls.Add(Btn("旋转", RotateImage, 60));
      bar.Controls.Add(Btn("镜像", MirrorImage, 60));
      bar.Controls.Add(Btn("裁剪", CropImage, 60));

      // The photo archive is always visible, as in the original MMSSTV
      // workspace. The height drops at narrow/minimized dock sizes.
      var galleryGroup = new GroupBox
      {
        Text = "图片库 / 最近图片", Dock = DockStyle.Bottom, Height = 146
      };
      galleryGroup.Controls.Add(txThumbnailStrip);
      var galleryBar = new FlowLayoutPanel
      {
        Dock = DockStyle.Right, Width = 65, WrapContents = false,
        FlowDirection = FlowDirection.TopDown, Padding = Padding.Empty
      };
      galleryBar.Controls.Add(Btn("刷新", RefreshTxThumbnails, 56));
      galleryBar.Controls.Add(Btn("图库", () => tabs.SelectedIndex = 2, 56));
      galleryGroup.Controls.Add(galleryBar);
      page.Resize += (_, _) =>
      {
        if (!page.IsDisposed)
          galleryGroup.Height = page.ClientSize.Height < 510 ? 94 : 146;
      };

      var main = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
        Margin = Padding.Empty, Padding = Padding.Empty
      };
      main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
      main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
      main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      var left = new Panel { Dock = DockStyle.Fill };
      var actions = new FlowLayoutPanel
      {
        Dock = DockStyle.Bottom, Height = 78,
        WrapContents = true, AutoScroll = true, Padding = new Padding(2)
      };
      actions.Controls.Add(txApproval);
      actions.Controls.Add(txSendButton);
      actions.Controls.Add(txStopButton);
      actions.Controls.Add(Btn("保存 WAV", () => _ = ExportWaveAsync(), 82));
      left.Controls.Add(txPicture);
      left.Controls.Add(actions);
      main.Controls.Add(left, 0, 0);

      var right = new Panel { Dock = DockStyle.Fill };
      var tone = new SstvTxToneReference { Height = 90 };
      var rightContent = new Panel { Dock = DockStyle.Fill };
      var modeColumn = new Panel { Dock = DockStyle.Left, Width = 130 };
      var modeHeader = new Label
      {
        Dock = DockStyle.Top, Height = 27,
        Text = "TX 模式", TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(Font, FontStyle.Bold)
      };
      modeColumn.Controls.Add(txQuickModes);
      modeColumn.Controls.Add(modeHeader);
      var rightEditor = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
      var settingsGrid = new TableLayoutPanel
      {
        Dock = DockStyle.Top, AutoSize = false,
        ColumnCount = 2, RowCount = 0, Padding = new Padding(5, 2, 5, 6)
      };
      settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
      settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      rightEditor.Controls.Add(settingsGrid);

      void Heading(string text)
      {
        int r = settingsGrid.RowCount++;
        settingsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        var header = new Label
        {
          Text = text, Dock = DockStyle.Fill,
          TextAlign = ContentAlignment.MiddleLeft,
          Font = new Font(Font, FontStyle.Bold)
        };
        settingsGrid.Controls.Add(header, 0, r);
        settingsGrid.SetColumnSpan(header, 2);
      }
      void Field(string name, Control input)
      {
        int r = settingsGrid.RowCount++;
        settingsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        settingsGrid.Controls.Add(new Label
        {
          Text = name, Dock = DockStyle.Fill,
          TextAlign = ContentAlignment.MiddleLeft
        }, 0, r);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(1, 4, 2, 3);
        settingsGrid.Controls.Add(input, 1, r);
      }
      void Buttons(params Control[] controls)
      {
        int r = settingsGrid.RowCount++;
        settingsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        var row = new FlowLayoutPanel
        {
          Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true,
          Margin = Padding.Empty
        };
        row.Controls.AddRange(controls);
        settingsGrid.Controls.Add(row, 0, r);
        settingsGrid.SetColumnSpan(row, 2);
      }

      Heading("模式与模板");
      Field("模式", modeBox);
      Field("模板", templateBox);
      Buttons(Btn("保存模板", SaveUserTemplate, 88),
        Btn("删除模板", DeleteUserTemplate, 88));

      Heading("QSO / 台站信息");
      Field("MY", myCallInput);
      Field("HIS", theirCallInput);
      Field("RSV TX", txRstSent);
      Field("RSV RX", txRstReceived);
      Field("姓名", txContactName);
      Field("QTH", txContactQth);
      Field("备注", txQsoNote);
      Field("文字", overlayText);
      Buttons(Btn("通联日志", () =>
      {
        qsoCall.Text = theirCallInput.Text;
        qsoNotes.Text = txQsoNote.Text;
        tabs.SelectedIndex = 3;
      }, 88));

      Heading("图片文字样式");
      Field("对象", textLayerBox);
      Field("内容", activeCaptionInput);
      Field("字号 %", textSizeBox);
      Buttons(textColorButton, Btn("新增", AddTextLayer, 56),
        Btn("复制", DuplicateTextLayer, 56), Btn("删除", RemoveTextLayer, 56));
      Field("变量", macroSelector);
      Buttons(Btn("插入变量", InsertSelectedMacro, 88));
      Buttons(new Label
      {
        Text = "拖动预览文字定位，滚轮调整大小",
        Height = 30, AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft
      });

      Heading("音频与识别");
      Field("声卡", txOutputBox);
      Buttons(txCwId, txFskId);

      // Fixed-height rows plus a fixed-width grid permit vertical scrolling
      // while each field stretches to the available column width at 650px.
      // AutoSize=true would enlarge the grid beyond the available right pane
      // based on the saved preferred width of long audio device dropdowns.
      settingsGrid.Height = (int)settingsGrid.RowStyles.Cast<RowStyle>()
        .Sum(style => style.Height) + settingsGrid.Padding.Vertical + 8;

      rightContent.Controls.Add(rightEditor);
      rightContent.Controls.Add(modeColumn);
      right.Controls.Add(rightContent);
      right.Controls.Add(tone);
      main.Controls.Add(right, 1, 0);

      // Keep top editing actions discoverable at any window size.
      page.Controls.Add(main);
      page.Controls.Add(galleryGroup);
      page.Controls.Add(txStatus);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    /// <summary>
    /// Keep an MMSSTV-style strip of user images. Session images are capped
    /// and downscaled to SSTV resolution; archived PNGs are read on demand,
    /// never locked or kept at original camera resolution.
    /// </summary>
    private void RememberTxImage(Bitmap source)
    {
      const int maxDimension = 640;
      float scale = Math.Min(1f, Math.Min((float)maxDimension / source.Width,
        (float)maxDimension / source.Height));
      int width = Math.Max(1, (int)Math.Round(source.Width * scale));
      int height = Math.Max(1, (int)Math.Round(source.Height * scale));
      var picture = new Bitmap(width, height);
      using (var g = Graphics.FromImage(picture))
      {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
      }
      txRecentImages.Insert(0, picture);
      while (txRecentImages.Count > 12)
      {
        int last = txRecentImages.Count - 1;
        txRecentImages[last].Dispose();
        txRecentImages.RemoveAt(last);
      }
    }

    private void AddTxThumbnail(Image picture, string title, Action action)
    {
      var small = new Bitmap(112, 72);
      using (var g = Graphics.FromImage(small))
      {
        g.Clear(Color.FromArgb(31, 35, 42));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float zoom = Math.Min(112f / picture.Width, 72f / picture.Height);
        int width = Math.Max(1, (int)Math.Round(picture.Width * zoom));
        int height = Math.Max(1, (int)Math.Round(picture.Height * zoom));
        g.DrawImage(picture, new Rectangle((112 - width) / 2,
          (72 - height) / 2, width, height));
      }
      var tile = new PictureBox
      {
        Size = new Size(112, 72), Image = small,
        SizeMode = PictureBoxSizeMode.StretchImage,
        Margin = new Padding(3), Cursor = Cursors.Hand
      };
      // Mouse Click fires for both left and right buttons. Never treat a
      // right-click as selecting/re-importing a picture.
      tile.MouseUp += (_, e) =>
      {
        if (e.Button == MouseButtons.Left) action();
        // WinForms displays ContextMenuStrip itself on a right click.
        // Do not explicitly Show() here, or the menu opens twice.
      };
      var context = new ContextMenuStrip();
      context.Items.Add("设为当前发送图片", null, (_, _) => action());
      context.Items.Add("打开图库 / 历史", null, (_, _) =>
      {
        ReloadHistory();
        tabs.SelectedIndex = 2;
      });
      tile.ContextMenuStrip = context;
      txThumbnailTips.SetToolTip(tile, title);
      txThumbnailStrip.Controls.Add(tile);
    }

    private void RefreshTxThumbnails()
    {
      if (closed || txThumbnailStrip.IsDisposed) return;
      txThumbnailStrip.SuspendLayout();
      try
      {
        for (int i = txThumbnailStrip.Controls.Count - 1; i >= 0; i--)
        {
          Control child = txThumbnailStrip.Controls[i];
          txThumbnailStrip.Controls.RemoveAt(i);
          if (child is PictureBox thumb)
          {
            thumb.Image?.Dispose();
            thumb.Image = null;
          }
          child.Dispose();
        }
        // Snapshot selection is intentionally by index; the list remains
        // owned by the panel and each image is retained until evicted.
        for (int i = 0; i < txRecentImages.Count; i++)
        {
          Bitmap image = txRecentImages[i];
          AddTxThumbnail(image, i == 0 ? "当前图片" : "近期编辑图片", () =>
          {
            RecallTxImage(new Bitmap(image));
          });
        }
        string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
        if (!Directory.Exists(folder)) return;
        var visibleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*.png")
          .OrderByDescending(File.GetLastWriteTimeUtc).Take(24))
        {
          try
          {
            visibleFiles.Add(file);
            var fileInfo = new FileInfo(file);
            if (!archivedThumbnailCache.TryGetValue(file, out var cached) ||
                cached.Utc != fileInfo.LastWriteTimeUtc || cached.Size != fileInfo.Length)
            {
              if (cached.Image != null)
                cached.Image.Dispose();
              using var source = Image.FromFile(file);
              const int thumbWidth = 112, thumbHeight = 72;
              var preview = new Bitmap(thumbWidth, thumbHeight);
              using (var gr = Graphics.FromImage(preview))
              {
                gr.Clear(Color.FromArgb(31, 35, 42));
                gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float scale = Math.Min((float)thumbWidth / source.Width,
                  (float)thumbHeight / source.Height);
                int width = Math.Max(1, (int)(source.Width * scale));
                int height = Math.Max(1, (int)(source.Height * scale));
                gr.DrawImage(source, new Rectangle(
                  (thumbWidth - width) / 2, (thumbHeight - height) / 2, width, height));
              }
              cached = (fileInfo.LastWriteTimeUtc, fileInfo.Length, preview);
              archivedThumbnailCache[file] = cached;
            }
            AddTxThumbnail(cached.Image, Path.GetFileNameWithoutExtension(file), () =>
            {
              try { RecallTxImage(SstvImageLibrary.LoadForEditing(file)); }
              catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
              {
                txStatus.Text = $"图片无法打开：{ex.Message}";
              }
            });
          }
          catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException) { }
        }
        foreach (string stale in archivedThumbnailCache.Keys
          .Where(key => !visibleFiles.Contains(key)).ToArray())
        {
          archivedThumbnailCache[stale].Image.Dispose();
          archivedThumbnailCache.Remove(stale);
        }
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        txStatus.Text = $"图库无法读取：{ex.Message}";
      }
      finally { txThumbnailStrip.ResumeLayout(); }
    }

    private void BuildHistoryTab()
    {
      var page = new TabPage("图库 / 历史");
      var bar = Toolbar();
      bar.Controls.Add(Btn("刷新", ReloadHistory, 70));
      bar.Controls.Add(Btn("导入图片", ImportHistoryImage, 90));
      bar.Controls.Add(Btn("导出图片", ExportHistoryImage, 90));
      bar.Controls.Add(Btn("删除图片", DeleteHistoryImage, 90));
      bar.Controls.Add(Btn("查看", ShowSelectedHistory, 70));
      bar.Controls.Add(Btn("用作发送图片", UseSelectedHistoryForTx, 125));
      page.Controls.Add(archiveList);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private async Task ReplayWavAsync()
    {
      if (replayStop != null)
      {
        replayStop.Cancel();
        return;
      }
      using var dialog = new OpenFileDialog
      {
        Filter = "WAV 音频文件|*.wav",
        Title = "打开 SSTV 录音"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      receiveSession.Stop();
      rxToggleButton.Text = "开始接收";
      var stop = new CancellationTokenSource();
      replayStop = stop;
      string? normalizedTemporaryFile = null;
      try
      {
        rxStatus.Text = "正在准备 SSTV 录音...";
        string workingWave = dialog.FileName;
        bool convert = await Task.Run(
          () => SstvWavNormalizer.NeedsConversion(dialog.FileName), stop.Token);
        if (convert)
        {
          normalizedTemporaryFile = Path.Combine(Path.GetTempPath(),
            $"SkyRoof_SstvRx_{Guid.NewGuid():N}.wav");
          string output = normalizedTemporaryFile;
          await Task.Run(() => SstvWavNormalizer.Normalize(
            dialog.FileName, output, stop.Token), stop.Token);
          workingWave = output;
        }
        stop.Token.ThrowIfCancellationRequested();
        rxStatus.Text = "正在解析 SSTV 录音...";
        double clockPpm = (double)imageSlantCorrection.Value;
        SstvVisModeIdentity? identified = await Task.Run(
          () => SstvVisModeIdentifier.DetectWave(workingWave), stop.Token);
        SstvTxModeProfile? manual = rxModeBox.SelectedItem as SstvTxModeProfile;
        SstvTxModeProfile? resolved = manual ?? identified?.Profile;
        if (resolved != null && resolved.Native == null)
        {
          using Bitmap image = await Task.Run(
            () => SstvOfflineRasterDecoder.Decode(
              workingWave, resolved,
              identified?.Profile.Name == resolved.Name
                ? identified.Value.LeadingAudio : default,
              identified?.Profile.Name == resolved.Name
                ? identified.Value.FrequencyOffsetHz : 0,
              clockPpm), stop.Token);
          if (!closed)
          {
            string gallery = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
            Directory.CreateDirectory(gallery);
            string name = $"SSTV_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{resolved.Name.Replace(' ', '_')}";
            string pictureFile = Path.Combine(gallery, name + ".png");
            image.Save(pictureFile, ImageFormat.Png);
            File.WriteAllText(Path.Combine(gallery, name + ".json"),
              System.Text.Json.JsonSerializer.Serialize(new
              {
                Utc = DateTime.UtcNow,
                Source = "导入的 WAV",
                Mode = resolved.Name,
                Picture = Path.GetFileName(pictureFile)
              }));
            ShowReceivedSstv(image, resolved.Name, true, pictureFile);
            await IdentifyRecordedFskIdAsync(workingWave, stop.Token);
          }
          return;
        }
        await SstvWaveReceiver.DecodeAsync(workingWave, OnAudioReceived,
          progress =>
          {
            if (closed || !IsHandleCreated) return;
            try
            {
              BeginInvoke((Action)(() =>
              {
                if (!closed)
                  rxStatus.Text = $"正在解析 SSTV 录音 · {progress:P0}";
              }));
            }
            catch (InvalidOperationException) { }
          }, stop.Token);
        if (!closed)
        {
          rxStatus.Text = "录音解析已完成";
          await IdentifyRecordedFskIdAsync(workingWave, stop.Token);
        }
      }
      catch (OperationCanceledException)
      {
        if (!closed) rxStatus.Text = "录音解析已停止";
      }
      catch (Exception ex)
      {
        if (!closed)
          MessageBox.Show(this, ex.Message, "无法解析 SSTV 录音",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
      finally
      {
        if (ReferenceEquals(replayStop, stop)) replayStop = null;
        stop.Dispose();
        if (!string.IsNullOrEmpty(normalizedTemporaryFile))
        {
          try { File.Delete(normalizedTemporaryFile); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }

    /// <summary>
    /// Decoded FSK station ID is a hint to the operator, not proof of
    /// a two-way QSO. QSO records still require explicit manual saving.
    /// </summary>
    private async Task IdentifyRecordedFskIdAsync(
      string path, CancellationToken token)
    {
      string? call = await Task.Run(
        () => SstvFskIdDecoder.DecodeWaveSuffix(path), token);
      if (closed || IsDisposed || string.IsNullOrWhiteSpace(call)) return;
      qsoCall.Text = call;
      rxStatus.Text = $"录音中的台站标识：{call}（请核对）";
    }

    private string QsoLogPath =>
      SstvQsoLogStore.DefaultLogPath(Utils.GetUserDataFolder());

    private void BuildQsoTab()
    {
      var page = new TabPage("通联日志");
      var bar = Toolbar();
      bar.Controls.Add(new Label
      {
        Text = "对方呼号", Width = 73, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(qsoCall);
      bar.Controls.Add(new Label
      {
        Text = "备注", Width = 40, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(qsoNotes);
      var actions = Toolbar();
      actions.Controls.Add(Btn("保存通联", SaveQsoRecord, 96));
      actions.Controls.Add(Btn("导出 ADIF", ExportQsoAdif, 106));
      page.Controls.Add(qsoList);
      page.Controls.Add(actions);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void ReloadQsoLog()
    {
      qsoList.BeginUpdate();
      try
      {
        qsoList.Items.Clear();
        foreach (var record in SstvQsoLogStore.Read(QsoLogPath, 256))
          qsoList.Items.Add(new QsoEntry(record));
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        txStatus.Text = "通联日志暂时无法读取";
      }
      finally { qsoList.EndUpdate(); }
    }

    private void SaveQsoRecord()
    {
      try
      {
        string call = qsoCall.Text.Trim().ToUpperInvariant();
        // Logging and image review must also work before any radio is
        // connected. Prefer the actual received mode over a TX selection.
        RadioLink link = ctx.FrequencyControl?.RadioLink ?? new RadioLink();
        var selected = modeBox.SelectedItem as SstvTxModeProfile;
        var record = new SstvQsoRecord(
          DateTime.UtcNow, call, myCallInput.Text.Trim().ToUpperInvariant(),
          lastReceivedMode ?? selected?.Name ?? "SSTV",
          link.IsTerrestrial ? "" : link.Sat?.name ?? "",
          link.IsTerrestrial ? 0 : link.CorrectedUplinkFrequency,
          link.CorrectedDownlinkFrequency,
          rxPicture.Tag as string ?? "",
          qsoNotes.Text.Trim())
        {
          RstSent = txRstSent.Text.Trim(),
          RstReceived = txRstReceived.Text.Trim(),
          ContactName = txContactName.Text.Trim(),
          ContactQth = txContactQth.Text.Trim()
        };
        SstvQsoLogStore.Append(QsoLogPath, record);
        qsoCall.Clear();
        qsoNotes.Clear();
        ReloadQsoLog();
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法保存通联记录",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ExportQsoAdif()
    {
      using var save = new SaveFileDialog
      {
        Filter = "ADIF 通联日志|*.adi",
        FileName = $"SSTV_{DateTime.Now:yyyyMMdd}.adi"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        File.WriteAllText(save.FileName,
          SstvQsoLogStore.ExportAdif(SstvQsoLogStore.Read(QsoLogPath, 100000)),
          new System.Text.UTF8Encoding(false));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法导出 ADIF",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private sealed record QsoEntry(SstvQsoRecord Record)
    {
      public override string ToString() =>
        $"{Record.Utc:yyyy-MM-dd HH:mm} UTC  {Record.Call}  {Record.SstvMode}  {Record.SatelliteName}";
    }

    private void PopulateTxOutputDevices()
    {
      txOutputBox.Items.Clear();
      try
      {
        foreach (AudioDeviceEntry d in Soundcard.ListDevices(DataFlow.Render))
          txOutputBox.Items.Add(new DeviceEntry(d.Id, d.Name));
        // Never choose Windows' default speaker as the TX audio route.
        // The operator must deliberately select the radio's own AF endpoint.
        txOutputBox.SelectedIndex = -1;
        txStatus.Text = txOutputBox.Items.Count > 0
          ? "请选择实际连接电台的发射声卡；不会自动使用系统默认扬声器。"
          : "未检测到发射声卡；仍可保存 SSTV WAV 音频";
      }
      catch (Exception ex)
      {
        Serilog.Log.Warning(ex, "Could not enumerate SSTV TX audio endpoints");
        txStatus.Text = "发射声卡不可用，请检查 Windows 音频设备";
      }
    }

    private void UpdateTxAvailability()
    {
      if (closed || IsDisposed) return;
      // A timed-out OFF command must remain visible to the operator and
      // prohibit a new TX until a subsequent, fresh hardware RX readback.
      bool releaseWasUnconfirmed = transmitSession.RequiresPttVerification;
      if (!transmitSession.VerifyPreviousPttRelease())
      {
        txSendButton.Enabled = false;
        txStatus.Text = SstvTxPolicy.PttReleaseUnconfirmedMessage;
        if (txApproval.Checked) txApproval.Checked = false;
        return;
      }
      if (releaseWasUnconfirmed)
      {
        txStatus.Text = "CAT 已确认电台返回接收状态，请重新核对发射频率与功率。";
        txApproval.Checked = false;
      }
      txSendButton.Enabled =
        txOperationStop == null &&
        !transmitSession.IsSending &&
        txApproval.Checked &&
        txPicture.Image is Bitmap &&
        txOutputBox.SelectedItem is DeviceEntry &&
        modeBox.SelectedItem is SstvTxModeProfile;
    }

    private void StopTransmit()
    {
      if (txOperationStop == null && !transmitSession.IsSending) return;
      txStopButton.Enabled = false; // prevent duplicate STOP clicks
      txStatus.Text = "正在停止音频并请求 PTT OFF...";
      txOperationStop?.Cancel();
      transmitSession.RequestStop();
    }

    private async Task SendImageAsync()
    {
      if (txOperationStop != null || transmitSession.IsSending ||
          txOutputBox.SelectedItem is not DeviceEntry output ||
          txPicture.Image is not Bitmap image ||
          modeBox.SelectedItem is not SstvTxModeProfile spec)
      {
        if (!transmitSession.IsSending)
          MessageBox.Show(this, "请先选择图片、发送模式及发射声卡。", "SSTV",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (!txApproval.Checked)
      {
        MessageBox.Show(this, "请先核对电台上行频率、发射许可及发射功率。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (MessageBox.Show(this,
        $"确认通过所选声卡输出并启动电台 PTT 发送 {spec.Name}？" +
        "\n\n开始后可随时点击“停止发射”。",
        "确认 SSTV 发射", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning) != DialogResult.Yes) return;

      string temporaryWave = Path.Combine(Path.GetTempPath(),
        $"SkyRoof_SstvTx_{Guid.NewGuid():N}.wav");
      using var operation = new CancellationTokenSource();
      txOperationStop = operation;
      txSendButton.Enabled = false;
      txStopButton.Enabled = true;
      txApproval.Enabled = false;
      txOutputBox.Enabled = false;
      bool criticalPttAlertShown = false;
      try
      {
        // Refresh macros at the actual send instant, before taking a
        // picture snapshot. STOP can also abort the entire WAV preparation.
        RefreshTxPreview();
        using var snapshot = new Bitmap(txPicture.Image!);
        txStatus.Text = "正在准备 SSTV 图片...";
        SstvIdOptions? stationId = txCwId.Checked || txFskId.Checked
          ? new SstvIdOptions(myCallInput.Text.Trim(),
            txCwId.Checked, txFskId.Checked)
          : null;
        await Task.Run(() =>
          SstvRgbWaveExporter.Export(snapshot, spec, temporaryWave,
            operation.Token, stationId),
          operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (closed) return;
        await transmitSession.SendWavAsync(
          temporaryWave, output.Id, txApproval.Checked,
          message => { if (!closed) txStatus.Text = message; },
          operation.Token);
      }
      catch (OperationCanceledException)
      {
        if (!closed)
          txStatus.Text = transmitSession.RequiresPttVerification
            ? SstvTxPolicy.PttReleaseUnconfirmedMessage : "发射已停止";
      }
      catch (Exception ex)
      {
        if (!closed)
        {
          bool pttUnconfirmed = transmitSession.RequiresPttVerification;
          txStatus.Text = pttUnconfirmed
            ? SstvTxPolicy.PttReleaseUnconfirmedMessage : "发射未完成";
          MessageBox.Show(this,
            pttUnconfirmed
              ? SstvTxPolicy.PttReleaseUnconfirmedMessage + "\n\n" + ex.Message
              : ex.Message,
            pttUnconfirmed ? "PTT 状态未确认" : "SSTV 发射已终止",
            MessageBoxButtons.OK,
            pttUnconfirmed ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
          criticalPttAlertShown = pttUnconfirmed;
        }
      }
      finally
      {
        if (ReferenceEquals(txOperationStop, operation)) txOperationStop = null;
        try { File.Delete(temporaryWave); } catch (IOException) { }
        if (!closed)
        {
          txStopButton.Enabled = false;
          txOutputBox.Enabled = true;
          txApproval.Enabled = true;
          txApproval.Checked = false;
          UpdateTxAvailability();
          // Cancellation is not a successful STOP acknowledgement. Preserve
          // this alarm even if the cancellation catch said "stopped".
          if (transmitSession.RequiresPttVerification)
          {
            txStatus.Text = SstvTxPolicy.PttReleaseUnconfirmedMessage;
            if (!criticalPttAlertShown)
              MessageBox.Show(this, SstvTxPolicy.PttReleaseUnconfirmedMessage,
                "PTT 状态未确认", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
          }
        }
      }
    }

    private void PopulateAudioDevices()
    {
      rxDeviceBox.Items.Clear();
      try
      {
        bool loopback = rxSourceBox.SelectedIndex is 1 or 3;
        foreach (AudioDeviceEntry d in Soundcard.ListDevices(
          loopback ? DataFlow.Render : DataFlow.Capture))
          rxDeviceBox.Items.Add(new DeviceEntry(d.Id, d.Name));
        if (rxDeviceBox.Items.Count > 0)
          rxDeviceBox.SelectedIndex = 0;
        else
          rxStatus.Text = "未检测到接收声卡；可使用“导入 WAV”解码录音";
      }
      catch (Exception ex)
      {
        Serilog.Log.Warning(ex, "Could not enumerate SSTV RX audio endpoints");
        rxStatus.Text = "接收声卡不可用，请检查 Windows 音频设备";
      }
    }

    private void ToggleReceive()
    {
      if (receiveSession.IsRunning)
      {
        receiveSession.Stop();
        rxToggleButton.Text = "开始接收";
        rxStatus.Text = "接收已停止";
        return;
      }
      if (rxDeviceBox.SelectedItem is not DeviceEntry device)
      {
        MessageBox.Show(this, "请选择接收音频设备。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      try
      {
        receiveSession.Start((SstvReceiveSource)rxSourceBox.SelectedIndex, device.Id);
        rxToggleButton.Text = "停止接收";
        rxStatus.Text = "正在等待 SSTV 信号...";
      }
      catch (Exception ex)
      {
        receiveSession.Stop();
        MessageBox.Show(this, ex.Message, "无法启动接收",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ManualStartReceive()
    {
      if (rxModeBox.SelectedItem is not SstvTxModeProfile mode)
      {
        MessageBox.Show(this, "请先在接收模式中选定具体模式。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (!receiveSession.IsRunning)
      {
        MessageBox.Show(this, "请先启动接收声卡。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (mode.Native != null)
      {
        rxStatus.Text = $"{mode.Name} 由 SkySignals 自动识别解码，请等待 VIS 同步";
        return;
      }
      receiveSession.ManualStart(mode);
      rxStatus.Text = $"已请求手动开始 {mode.Name}，等待扫描行";
    }

    private void OnLiveFskStationIdentified(string callsign)
    {
      if (closed || IsDisposed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (closed || IsDisposed) return;
          if (string.IsNullOrWhiteSpace(qsoCall.Text))
            qsoCall.Text = callsign;
          rxStatus.Text = $"收到 FSK 台站标识：{callsign}（需核对）";
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnSpectrumAvailable(float[] levels)
    {
      if (closed || IsDisposed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!closed && !IsDisposed)
            rxToneReference.SetLevels(levels);
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnVisSynchronized(SstvVisAfcLock header)
    {
      if (closed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (closed || IsDisposed) return;
          var native = SstvModes.FromVisByte(header.VisByte);
          string mode = native?.Name ??
            SstvRgbTxSpec.All.FirstOrDefault(x =>
              SstvModes.EvenParityByte(x.VisCode7) == header.VisByte)?.Name ??
            "SSTV";
          string status = autoVisCorrection.Checked ?
            $"识别 {mode} · 已自动校正 {receiveSession.AutoVisCorrectionHz:+0;-0;0} Hz" :
            $"识别 {mode} · 自动校频已关闭";
          rxStatus.Text = status;
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnRasterReceived(Bitmap picture,
      string mode, bool final, double correctionHz)
    {
      if (closed || IsDisposed || !IsHandleCreated)
      {
        picture.Dispose();
        return;
      }
      try
      {
        BeginInvoke((Action)(() =>
        {
          using (picture)
          {
            if (closed || IsDisposed) return;
            string? savedFile = null;
            if (final)
            {
              try
              {
                string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
                Directory.CreateDirectory(folder);
                string name =
                  $"SSTV_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{mode.Replace(' ', '_')}";
                savedFile = Path.Combine(folder, name + ".png");
                picture.Save(savedFile, ImageFormat.Png);
                File.WriteAllText(Path.Combine(folder, name + ".json"),
                  System.Text.Json.JsonSerializer.Serialize(new
                  {
                    Utc = DateTime.UtcNow,
                    Mode = mode,
                    Source = "实时声卡 AF/IF",
                    OffsetHz = correctionHz,
                    Picture = Path.GetFileName(savedFile)
                  }));
              }
              catch (Exception ex)
              {
                Serilog.Log.Warning(ex, "Could not archive extended SSTV image");
              }
            }
            ShowReceivedSstv(picture, mode, final, savedFile);
          }
        }));
      }
      catch (InvalidOperationException) { picture.Dispose(); }
    }

    private void OnAudioReceived(SstvImageEvent evt)
    {
      if (closed || !IsHandleCreated ||
          (!evt.Final && evt.ValidRows % 12 != 0)) return;
      Bitmap picture;
      try { picture = evt.Image.ToBitmap(); }
      catch { return; }
      string? savedFile = null;
      if (evt.Final && evt.ValidRows > 0)
      {
        try
        {
          string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
          Directory.CreateDirectory(folder);
          string name = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_Audio_{evt.Mode}_{evt.ImageId}";
          savedFile = Path.Combine(folder, name + ".png");
          picture.Save(savedFile, ImageFormat.Png);
          string json = System.Text.Json.JsonSerializer.Serialize(new
          {
            Utc = DateTime.UtcNow,
            Source = "WASAPI AF/IF",
            Mode = evt.Mode.ToString(),
            evt.ValidRows,
            evt.Image.Width,
            evt.Image.Height,
            evt.FromVis,
            Call = ctx.Settings.User.Call ?? ""
          });
          File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        }
        catch (Exception e)
        {
          Serilog.Log.Warning(e, "SSTV receive image could not be saved");
        }
      }
      try
      {
        string? path = savedFile;
        BeginInvoke((Action)(() =>
        {
          using (picture)
          {
            if (!closed && !IsDisposed)
              ShowReceivedSstv(picture, evt.Mode.ToString(), evt.Final, path);
          }
        }));
      }
      catch (InvalidOperationException) { picture.Dispose(); }
    }

    private void LoadImage()
    {
      using var dialog = new OpenFileDialog
      {
        Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
        Title = "选择发送图片"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        SetTxImage(SstvImageLibrary.LoadForEditing(dialog.FileName));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法打开图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void PasteImage()
    {
      try
      {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image image)
        {
          using (image)
          {
            using var temporary = new Bitmap(image);
            SetTxImage(SstvImageLibrary.ScaleForEditing(temporary));
          }
        }
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法粘贴图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private static void ClearHistory(Stack<Bitmap> history)
    {
      while (history.TryPop(out Bitmap? entry)) entry.Dispose();
    }

    private static void PushHistory(Stack<Bitmap> stack, Bitmap image)
    {
      // Transfer ownership of the previous bitmap instead of cloning
      // multi-megabyte images for every edit/undo operation.
      stack.Push(image);
      if (stack.Count <= HistoryLimit) return;
      Bitmap[] newestFirst = stack.ToArray();
      stack.Clear();
      for (int i = HistoryLimit; i < newestFirst.Length; i++)
        newestFirst[i].Dispose();
      for (int i = HistoryLimit - 1; i >= 0; i--)
        stack.Push(newestFirst[i]);
    }

    private void SetTxImage(Bitmap image) => ReplaceTxImage(image, remember: true);

    // Selecting an already-present thumbnail changes the editor selection;
    // it is not another import and must not append a history entry.
    private void RecallTxImage(Bitmap image) => ReplaceTxImage(image, remember: false);

    private void ReplaceTxImage(Bitmap image, bool remember)
    {
      var old = txBase;
      if (old != null) PushHistory(undoImages, old);
      txBase = image;
      if (remember) RememberTxImage(image);
      ClearHistory(redoImages);
      RefreshTxPreview();
      txStatus.Text = "图片已加载，可以编辑或保存 SSTV 音频";
      if (remember) RefreshTxThumbnails();
      tabs.SelectedIndex = 1;
    }

    private string TemplatePath =>
      SstvTemplateStore.DefaultPath(Utils.GetUserDataFolder());

    private void ReloadUserTemplates()
    {
      try
      {
        for (int i = templateBox.Items.Count - 1; i >= 0; i--)
          if (templateBox.Items[i] is SstvUserTemplate)
            templateBox.Items.RemoveAt(i);
        foreach (SstvUserTemplate entry in SstvTemplateStore.Read(TemplatePath))
          templateBox.Items.Add(entry);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        txStatus.Text = "图片模板无法读取";
      }
    }

    private void SaveUserTemplate()
    {
      using var dialog = new Form
      {
        Text = "保存图片模板", Size = new Size(380, 175),
        MinimumSize = new Size(380, 175), MaximumSize = new Size(380, 175),
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterParent,
        MaximizeBox = false, MinimizeBox = false
      };
      var label = new Label
      {
        Text = "模板名称", Left = 18, Top = 18, Width = 75, Height = 28
      };
      var input = new TextBox
      {
        Left = 98, Top = 15, Width = 255,
        Text = (templateBox.SelectedItem as SstvUserTemplate)?.Name ?? ""
      };
      var ok = new Button
      {
        Text = "保存", Left = 172, Top = 65, Width = 88,
        DialogResult = DialogResult.OK
      };
      var cancel = new Button
      {
        Text = "取消", Left = 265, Top = 65, Width = 88,
        DialogResult = DialogResult.Cancel
      };
      dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
      dialog.AcceptButton = ok;
      dialog.CancelButton = cancel;
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        string style = templateBox.SelectedItem is SstvUserTemplate source
          ? source.Style : templateBox.SelectedItem?.ToString() ?? "经典呼号";
        var record = new SstvUserTemplate(input.Text.Trim(), style,
          overlayText.Text, DateTime.UtcNow)
        {
          MyCallText = myCallInput.Text,
          TheirCallText = theirCallInput.Text,
          MyCallStyle = textLayers[0].Normalized(),
          TheirCallStyle = textLayers[1].Normalized(),
          OverlayStyle = textLayers[2].Normalized(),
          ExtraCaptions = extraCaptions.Select(x =>
            x with { Style = x.Style.Normalized() }).ToArray()
        };
        SstvTemplateStore.Save(TemplatePath, record);
        ReloadUserTemplates();
        for (int i = 0; i < templateBox.Items.Count; i++)
          if (templateBox.Items[i] is SstvUserTemplate candidate &&
              candidate.Name == record.Name)
          {
            templateBox.SelectedIndex = i;
            break;
          }
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法保存图片模板",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ApplyUserTemplate(SstvUserTemplate selected)
    {
      // Older templates contain only OverlayText. Missing positions/colors
      // are intentionally restored from defaults rather than guessed.
      myCallInput.Text = selected.MyCallText ?? ctx.Settings.User.Call ?? "";
      theirCallInput.Text = selected.TheirCallText ?? "";
      overlayText.Text = selected.OverlayText;
      textLayers[0] = (selected.MyCallStyle ?? SstvTextLayer.MyCallDefault).Normalized();
      textLayers[1] = (selected.TheirCallStyle ?? SstvTextLayer.TheirCallDefault).Normalized();
      textLayers[2] = (selected.OverlayStyle ?? SstvTextLayer.CustomDefault).Normalized();
      textLayerBox.SelectedIndex = 0;
      extraCaptions.Clear();
      extraCaptions.AddRange((selected.ExtraCaptions ?? Array.Empty<SstvAdditionalCaption>())
        .Take(5).Select(x => x with { Style = x.Style.Normalized() }));
      while (textLayerBox.Items.Count > 3) textLayerBox.Items.RemoveAt(3);
      for (int i = 0; i < extraCaptions.Count; i++)
        textLayerBox.Items.Add("文字图层 " + (i + 4));
      if (textLayerBox.SelectedIndex < 0) textLayerBox.SelectedIndex = 0;
      UpdateTextEditorControls();
    }

    private void DeleteUserTemplate()
    {
      if (templateBox.SelectedItem is not SstvUserTemplate selected)
      {
        txStatus.Text = "内置模板不可删除，请先选择已保存的自定义模板";
        return;
      }
      if (MessageBox.Show(this, $"删除自定义模板“{selected.Name}”？",
        "删除 SSTV 模板", MessageBoxButtons.YesNo,
        MessageBoxIcon.Question) != DialogResult.Yes) return;
      try
      {
        if (SstvTemplateStore.Delete(TemplatePath, selected.Name))
        {
          ReloadUserTemplates();
          templateBox.SelectedIndex = 0;
          txStatus.Text = $"已删除模板：{selected.Name}";
        }
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "删除模板失败",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private sealed record MacroEntry(string Token, string Label)
    {
      public override string ToString() => Label + "  " + Token;
    }

    private int TextLayerCount => 3 + extraCaptions.Count;

    private SstvTextLayer GetTextLayer(int index) =>
      index < 3 ? textLayers[index] : extraCaptions[index - 3].Style;

    private void SetTextLayer(int index, SstvTextLayer style)
    {
      if (index < 3) textLayers[index] = style.Normalized();
      else extraCaptions[index - 3] =
        extraCaptions[index - 3] with { Style = style.Normalized() };
    }

    private string GetCaptionText(int index) => index switch
    {
      0 => myCallInput.Text,
      1 => theirCallInput.Text,
      2 => overlayText.Text,
      _ => extraCaptions[index - 3].Text
    };

    private void SetCaptionText(int index, string text)
    {
      switch (index)
      {
        case 0: myCallInput.Text = text; break;
        case 1: theirCallInput.Text = text; break;
        case 2: overlayText.Text = text; break;
        default:
          extraCaptions[index - 3] =
            extraCaptions[index - 3] with { Text = text };
          break;
      }
    }

    private void SyncActiveCaptionText()
    {
      if (textLayerBox.SelectedIndex < 0 || updatingTextControls) return;
      updatingTextControls = true;
      try { activeCaptionInput.Text = GetCaptionText(textLayerBox.SelectedIndex); }
      finally { updatingTextControls = false; }
    }

    private void AddTextLayer()
    {
      if (TextLayerCount >= MaxTextLayers)
      {
        txStatus.Text = "最多可创建 8 个文字图层";
        return;
      }
      int next = extraCaptions.Count;
      extraCaptions.Add(new SstvAdditionalCaption("",
        SstvTextLayer.CustomDefault with
        {
          Y = Math.Clamp(.12f + .11f * next, 0, .9f),
          X = .08f
        }));
      textLayerBox.Items.Add("文字图层 " + (next + 4));
      textLayerBox.SelectedIndex = TextLayerCount - 1;
      RefreshTxPreview();
    }

    private void DuplicateTextLayer()
    {
      if (TextLayerCount >= MaxTextLayers)
      {
        txStatus.Text = "最多可创建 8 个文字图层";
        return;
      }
      int selected = Math.Max(0, textLayerBox.SelectedIndex);
      SstvTextLayer source = GetTextLayer(selected);
      extraCaptions.Add(new SstvAdditionalCaption(GetCaptionText(selected),
        (source with { X = source.X + .025f, Y = source.Y + .035f }).Normalized()));
      textLayerBox.Items.Add("文字图层 " + TextLayerCount);
      textLayerBox.SelectedIndex = TextLayerCount - 1;
      RefreshTxPreview();
    }

    private void RemoveTextLayer()
    {
      int selected = textLayerBox.SelectedIndex;
      if (selected < 3)
      {
        txStatus.Text = "三个基础图层不可删除，可清空文字或删除附加图层";
        return;
      }
      textLayerBox.SelectedIndex = 0;
      extraCaptions.RemoveAt(selected - 3);
      textLayerBox.Items.RemoveAt(selected);
      for (int i = 3; i < textLayerBox.Items.Count; i++)
        textLayerBox.Items[i] = "文字图层 " + (i + 1);
      textLayerBox.SelectedIndex = Math.Min(selected, TextLayerCount - 1);
      RefreshTxPreview();
    }

    private void InsertSelectedMacro()
    {
      if (macroSelector.SelectedItem is not MacroEntry macro) return;
      int index = textLayerBox.SelectedIndex;
      if (index < 0) return;
      string current = GetCaptionText(index);
      if (current.Length + macro.Token.Length > 512)
      {
        txStatus.Text = "文字长度已达上限";
        return;
      }
      int position = Math.Clamp(activeCaptionInput.SelectionStart, 0, current.Length);
      SetCaptionText(index, current.Insert(position, macro.Token));
      SyncActiveCaptionText();
      activeCaptionInput.Focus();
      activeCaptionInput.SelectionStart = position + macro.Token.Length;
      RefreshTxPreview();
    }

    private void UpdateTextEditorControls()
    {
      if (textLayerBox.SelectedIndex < 0 || textLayerBox.SelectedIndex >= TextLayerCount)
        return;
      updatingTextControls = true;
      try
      {
        var style = GetTextLayer(textLayerBox.SelectedIndex).Normalized();
        activeCaptionInput.Text = GetCaptionText(textLayerBox.SelectedIndex);
        textSizeBox.Value = Math.Clamp((decimal)style.SizePercent,
          textSizeBox.Minimum, textSizeBox.Maximum);
        textColorButton.BackColor = Color.FromArgb(style.ColorArgb);
        Color c = textColorButton.BackColor;
        textColorButton.ForeColor = (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 140
          ? Color.Black : Color.White;
      }
      finally { updatingTextControls = false; }
      txPicture.Invalidate();
    }

    private void PickTextColor()
    {
      int index = Math.Max(0, textLayerBox.SelectedIndex);
      using var dialog = new ColorDialog
      {
        FullOpen = true,
        Color = Color.FromArgb(GetTextLayer(index).ColorArgb)
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      SetTextLayer(index, GetTextLayer(index) with
        { ColorArgb = dialog.Color.ToArgb() });
      UpdateTextEditorControls();
      RefreshTxPreview();
    }

    // PictureBox Zoom preserves the SSTV bitmap aspect ratio. Use exactly
    // the same transform for drawing the selection and interpreting drag.
    private RectangleF TxPictureClientBounds()
    {
      if (txPicture.Image == null) return RectangleF.Empty;
      float scale = Math.Min((float)txPicture.ClientSize.Width / txPicture.Image.Width,
        (float)txPicture.ClientSize.Height / txPicture.Image.Height);
      if (!float.IsFinite(scale) || scale <= 0) return RectangleF.Empty;
      float width = txPicture.Image.Width * scale;
      float height = txPicture.Image.Height * scale;
      return new RectangleF((txPicture.ClientSize.Width - width) / 2f,
        (txPicture.ClientSize.Height - height) / 2f, width, height);
    }

    private bool TryPicturePoint(Point location, out PointF point)
    {
      point = PointF.Empty;
      RectangleF bounds = TxPictureClientBounds();
      if (bounds.IsEmpty || !bounds.Contains(location)) return false;
      Bitmap preview = (Bitmap)txPicture.Image!;
      point = new PointF((location.X - bounds.Left) * preview.Width / bounds.Width,
        (location.Y - bounds.Top) * preview.Height / bounds.Height);
      return true;
    }

    private void OnTxPictureMouseDown(object? sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left || !TryPicturePoint(e.Location, out PointF pixel))
        return;
      if (txPicture.Image is not Bitmap preview) return;
      for (int i = TextLayerCount - 1; i >= 0; i--)
      {
        if (!textBounds[i].Contains(pixel)) continue;
        textLayerBox.SelectedIndex = i;
        SstvTextLayer layer = GetTextLayer(i);
        textDragOffset = new PointF(layer.X - pixel.X / preview.Width,
          layer.Y - pixel.Y / preview.Height);
        movingText = true;
        txPicture.Cursor = Cursors.SizeAll;
        return;
      }
    }

    private void OnTxPictureMouseMove(object? sender, MouseEventArgs e)
    {
      if (!movingText || e.Button != MouseButtons.Left ||
          !TryPicturePoint(e.Location, out PointF pixel) ||
          txPicture.Image is not Bitmap preview) return;
      int index = Math.Max(0, textLayerBox.SelectedIndex);
      SetTextLayer(index, GetTextLayer(index) with
      {
        X = textDragOffset.X + pixel.X / preview.Width,
        Y = textDragOffset.Y + pixel.Y / preview.Height
      });
      RefreshTxPreview();
    }

    private void OnTxPictureMouseWheel(object? sender, MouseEventArgs e)
    {
      if (e.Delta == 0) return;
      decimal target = textSizeBox.Value + (e.Delta > 0 ? 0.5m : -0.5m);
      textSizeBox.Value = Math.Clamp(target, textSizeBox.Minimum, textSizeBox.Maximum);
    }

    private void DrawTextSelection(Graphics g)
    {
      if (txPicture.Image is not Bitmap preview || textLayerBox.SelectedIndex < 0)
        return;
      RectangleF bounds = TxPictureClientBounds();
      RectangleF text = textBounds[textLayerBox.SelectedIndex];
      if (bounds.IsEmpty || text.IsEmpty) return;
      float scale = bounds.Width / preview.Width;
      using var pen = new Pen(Color.Gold, 1.1f) { DashStyle = DashStyle.Dash };
      g.DrawRectangle(pen, bounds.Left + text.Left * scale,
        bounds.Top + text.Top * scale, text.Width * scale, text.Height * scale);
    }

    private void UndoImage()
    {
      if (txBase == null || !undoImages.TryPop(out Bitmap? prior)) return;
      PushHistory(redoImages, txBase);
      txBase = prior;
      RefreshTxPreview();
    }

    private void RedoImage()
    {
      if (txBase == null || !redoImages.TryPop(out Bitmap? next)) return;
      PushHistory(undoImages, txBase);
      txBase = next;
      RefreshTxPreview();
    }

    private void RotateImage()
    {
      if (txBase == null) return;
      var bitmap = new Bitmap(txBase);
      bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
      SetTxImage(bitmap);
    }

    private void MirrorImage()
    {
      if (txBase == null) return;
      var bitmap = new Bitmap(txBase);
      bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
      SetTxImage(bitmap);
    }

    private void CropImage()
    {
      if (txBase == null) return;
      var mode = modeBox.SelectedItem as SstvTxModeProfile;
      double aspect = mode == null ? 4.0 / 3 : (double)mode.Width / mode.Height;
      int w = txBase.Width, h = txBase.Height;
      if ((double)w / h > aspect) w = Math.Max(1, (int)(h * aspect));
      else h = Math.Max(1, (int)(w / aspect));
      Rectangle crop = new((txBase.Width - w) / 2,
        (txBase.Height - h) / 2, w, h);
      var result = new Bitmap(w, h);
      using (var g = Graphics.FromImage(result))
        g.DrawImage(txBase, new Rectangle(0, 0, w, h), crop, GraphicsUnit.Pixel);
      SetTxImage(result);
    }

    private Bitmap? RenderTxPreview()
    {
      if (txBase == null) return null;
      SstvTxModeProfile spec = modeBox.SelectedItem as SstvTxModeProfile ?? SstvTxModeProfile.All[0];
      var canvas = new Bitmap(spec.Width, spec.Height, PixelFormat.Format32bppArgb);
      using var g = Graphics.FromImage(canvas);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = movingText ? InterpolationMode.Bilinear :
        InterpolationMode.HighQualityBicubic;
      g.Clear(Color.Black);
      g.DrawImage(txBase, new Rectangle(0, 0, canvas.Width, canvas.Height));
      string template = templateBox.SelectedItem is SstvUserTemplate saved
        ? saved.Style : templateBox.SelectedItem?.ToString() ?? "经典呼号";
      if (template == "QSL 卡片")
      {
        using var panel = new SolidBrush(Color.FromArgb(205, 16, 27, 43));
        g.FillRectangle(panel, 0, canvas.Height * .77f,
          canvas.Width, canvas.Height * .23f);
        using var captionFont = new Font(FontFamily.GenericSansSerif,
          Math.Max(9f, canvas.Width / 38f), FontStyle.Bold);
        g.DrawString("SSTV QSL", captionFont, Brushes.LightSkyBlue,
          canvas.Width * .035f, canvas.Height * .785f);
      }
      else if (template == "卫星横幅")
      {
        using var panel = new SolidBrush(Color.FromArgb(200, 9, 30, 57));
        g.FillRectangle(panel, 0, 0, canvas.Width, canvas.Height * .16f);
        using var captionFont = new Font(FontFamily.GenericSansSerif,
          Math.Max(9f, canvas.Width / 40f), FontStyle.Bold);
        g.DrawString("SATELLITE SSTV", captionFont, Brushes.White,
          canvas.Width * .035f, canvas.Height * .035f);
      }
      // Picture editing and offline WAV export work even when the radio
      // widget has not yet been initialized or no satellite is selected.
      RadioLink radio = ctx.FrequencyControl?.RadioLink ?? new RadioLink();
      var macros = new SstvMacroContext(
        myCallInput.Text.Trim().ToUpperInvariant(), ctx.Settings.User.Square ?? "",
        (string.IsNullOrWhiteSpace(theirCallInput.Text)
          ? qsoCall.Text : theirCallInput.Text).Trim().ToUpperInvariant(),
        radio.IsTerrestrial ? "" : radio.Sat?.name ?? "",
        (modeBox.SelectedItem as SstvTxModeProfile)?.Name ?? "SSTV",
        radio.IsTerrestrial ? 0 : radio.CorrectedUplinkFrequency,
        radio.CorrectedDownlinkFrequency,
        DateTime.UtcNow)
      {
        RstSent = txRstSent.Text,
        RstReceived = txRstReceived.Text,
        ContactName = txContactName.Text,
        ContactQth = txContactQth.Text,
        Notes = txQsoNote.Text
      };
      for (int i = 0; i < textBounds.Length; i++)
      {
        textBounds[i] = RectangleF.Empty;
        if (i >= TextLayerCount || template == "纯图片") continue;
        string caption = SstvTextMacros.Expand(GetCaptionText(i), macros);
        if (string.IsNullOrWhiteSpace(caption)) continue;
        SstvTextLayer layout = GetTextLayer(i).Normalized();
        float fontSize = Math.Max(7, canvas.Width * layout.SizePercent / 100f);
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold,
          GraphicsUnit.Pixel);
        SizeF size = g.MeasureString(caption, font);
        float x = Math.Clamp(canvas.Width * layout.X,
          0, Math.Max(0, canvas.Width - size.Width));
        float y = Math.Clamp(canvas.Height * layout.Y,
          0, Math.Max(0, canvas.Height - size.Height));
        textBounds[i] = new RectangleF(x, y, size.Width, size.Height);
        using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        using var foreground = new SolidBrush(Color.FromArgb(layout.ColorArgb));
        g.DrawString(caption, font, shadow, x + 1.5f, y + 1.5f);
        g.DrawString(caption, font, foreground, x, y);
      }
      return canvas;
    }

    private void RefreshTxPreview()
    {
      if (closed) return;
      var newPicture = RenderTxPreview();
      var old = txPicture.Image;
      txPicture.Image = newPicture;
      old?.Dispose();
      txPicture.Invalidate();
      UpdateTxAvailability();
    }

    private async Task ExportWaveAsync()
    {
      if (txPicture.Image is not Bitmap preview ||
          modeBox.SelectedItem is not SstvTxModeProfile spec)
      {
        MessageBox.Show(this, "请先打开或粘贴图片。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      using var save = new SaveFileDialog
      {
        Title = "保存 SSTV 音频",
        Filter = "WAV 音频文件|*.wav",
        FileName = $"SSTV_{spec.Name.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.wav"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      using var snapshot = new Bitmap(preview);
      txStatus.Text = "正在生成 SSTV 音频...";
      try
      {
        SstvIdOptions? stationId = txCwId.Checked || txFskId.Checked
          ? new SstvIdOptions(myCallInput.Text.Trim(),
            txCwId.Checked, txFskId.Checked)
          : null;
        await Task.Run(() => SstvRgbWaveExporter.Export(
          snapshot, spec, save.FileName, default, stationId));
        if (!closed) txStatus.Text = "音频已保存";
      }
      catch (Exception ex)
      {
        if (!closed)
        {
          txStatus.Text = "保存失败";
          MessageBox.Show(this, ex.Message, "SSTV",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      }
    }

    /// <summary>Called by TelemetryPanel on the WinForms UI thread.</summary>
    public void ShowReceivedSstv(Bitmap picture, string mode, bool final, string? savedFile)
    {
      if (closed || IsDisposed || picture == null) return;
      var next = new Bitmap(picture);
      var old = rxPicture.Image;
      rxPicture.Image = next;
      old?.Dispose();
      rxStatus.Text = final ? $"接收完成 · {mode}" : $"正在接收 · {mode}";
      // Never associate a new partial picture with the previous received
      // image in QSO logging. Keep the actual RX mode for the next log.
      rxPicture.Tag = final ? savedFile : null;
      if (final)
      {
        lastReceivedMode = mode;
        if (!string.IsNullOrWhiteSpace(savedFile))
          ReloadHistory();
      }
    }

    private void SaveReceivedImage()
    {
      if (rxPicture.Image == null) return;
      using var save = new SaveFileDialog
      {
        Filter = "PNG 图片|*.png",
        FileName = $"SSTV_{DateTime.Now:yyyyMMdd_HHmmss}.png"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      rxPicture.Image.Save(save.FileName, ImageFormat.Png);
    }

    private string HistoryFolder =>
      Path.Combine(Utils.GetUserDataFolder(), "SstvImages");

    private void ImportHistoryImage()
    {
      using var dialog = new OpenFileDialog
      {
        Title = "导入 SSTV 图库图片",
        Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
        Multiselect = false, CheckFileExists = true
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        string imported = SstvImageLibrary.Import(HistoryFolder, dialog.FileName);
        ReloadHistory();
        for (int i = 0; i < archiveList.Items.Count; i++)
        {
          if (archiveList.Items[i] is HistoryImage item &&
              string.Equals(item.Path, imported, StringComparison.OrdinalIgnoreCase))
          {
            archiveList.SelectedIndex = i;
            break;
          }
        }
        RefreshTxThumbnails();
        txStatus.Text = "图片已导入图库";
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or System.Runtime.InteropServices.ExternalException)
      {
        MessageBox.Show(this, ex.Message, "导入图片失败",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ExportHistoryImage()
    {
      if (archiveList.SelectedItem is not HistoryImage item)
      {
        MessageBox.Show(this, "请在图库中选中要导出的图片。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      using var dialog = new SaveFileDialog
      {
        Title = "导出图库图片",
        Filter = "PNG 图片|*.png",
        FileName = Path.GetFileName(item.Path),
        AddExtension = true, DefaultExt = "png"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        SstvImageLibrary.Export(item.Path, dialog.FileName);
        txStatus.Text = "图片已导出";
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException)
      {
        MessageBox.Show(this, ex.Message, "导出图片失败",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void DeleteHistoryImage()
    {
      if (archiveList.SelectedItem is not HistoryImage item)
      {
        MessageBox.Show(this, "请在图库中选中要删除的图片。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (MessageBox.Show(this, $"确定删除图库图片“{Path.GetFileName(item.Path)}”？",
        "删除 SSTV 图片", MessageBoxButtons.YesNo,
        MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
          != DialogResult.Yes) return;
      try
      {
        if (!SstvImageLibrary.Delete(HistoryFolder, item.Path))
        {
          txStatus.Text = "图片已经不存在";
        }
        else
        {
          txStatus.Text = "图片已从图库删除";
        }
        ReloadHistory();
        RefreshTxThumbnails();
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException)
      {
        MessageBox.Show(this, ex.Message, "删除图片失败",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ReloadHistory()
    {
      if (closed) return;
      archiveList.BeginUpdate();
      try
      {
        archiveList.Items.Clear();
        string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.EnumerateFiles(folder, "*.png")
          .OrderByDescending(File.GetLastWriteTimeUtc).Take(64))
          archiveList.Items.Add(new HistoryImage(path));
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
      finally { archiveList.EndUpdate(); }
    }

    private void ShowSelectedHistory()
    {
      if (archiveList.SelectedItem is not HistoryImage item) return;
      try
      {
        using var loaded = Image.FromFile(item.Path);
        using var preview = new Bitmap(loaded);
        ShowReceivedSstv(preview, "历史记录", true, null);
        tabs.SelectedIndex = 0;
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法读取图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void UseSelectedHistoryForTx()
    {
      if (archiveList.SelectedItem is not HistoryImage item) return;
      try
      {
        RecallTxImage(SstvImageLibrary.LoadForEditing(item.Path));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法读取图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private sealed record DeviceEntry(string Id, string Name)
    {
      public override string ToString() => Name;
    }

    private sealed record HistoryImage(string Path)
    {
      public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
      closed = true;
      StopTransmit();
      transmitSession.Dispose();
      replayStop?.Cancel();
      receiveSession.RasterImageAvailable -= OnRasterReceived;
      receiveSession.VisSynchronized -= OnVisSynchronized;
      receiveSession.SpectrumAvailable -= OnSpectrumAvailable;
      receiveSession.FskStationIdentified -= OnLiveFskStationIdentified;
      receiveSession.Dispose();
      if (ctx.SstvConsolePanel == this) ctx.SstvConsolePanel = null;
      var oldRx = rxPicture.Image;
      rxPicture.Image = null;
      oldRx?.Dispose();
      var oldTx = txPicture.Image;
      txPicture.Image = null;
      oldTx?.Dispose();
      txBase?.Dispose();
      foreach (Control child in txThumbnailStrip.Controls)
        if (child is PictureBox thumb) { thumb.Image?.Dispose(); thumb.Image = null; }
      foreach (var cached in archivedThumbnailCache.Values) cached.Image.Dispose();
      archivedThumbnailCache.Clear();
      foreach (Bitmap thumb in txRecentImages) thumb.Dispose();
      txRecentImages.Clear();
      txThumbnailTips.Dispose();
      ClearHistory(undoImages);
      ClearHistory(redoImages);
      base.OnFormClosed(e);
    }
  }
}
