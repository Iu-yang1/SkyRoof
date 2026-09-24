using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public class RsBa1SpectrumPanel : DockContent
  {
    private Context? ctx;
    private readonly ComboBox WindowCombo = new();
    private readonly Button RefreshBtn = new();
    private readonly Button CopyReportBtn = new();
    private readonly CheckBox ShowAllCheckbox = new();
    private readonly CheckBox AutoRefreshCheckbox = new();
    private readonly Label StatusLabel = new();
    private readonly TreeView WindowTree = new();
    private readonly TextBox DetailsBox = new();
    private readonly SplitContainer InspectorSplit = new();
    private readonly TabControl ViewTabs = new();
    private readonly TabPage PreviewTab = new("Preview");
    private readonly TabPage InspectorTab = new("Inspector");
    private readonly Panel PreviewHost = new();
    private readonly Label PreviewMessage = new();
    private readonly System.Windows.Forms.Timer RefreshTimer = new() { Interval = 1500 };
    private IntPtr PreviewThumbnail;
    private IntPtr PreviewSource;
    private IntPtr PreviewDestinationRoot;
    private Rectangle PreviewContentRect = Rectangle.Empty;
    private Size PreviewSourceClientSize = Size.Empty;
    private readonly List<RsBa1WindowInspector.WindowInfo> Windows = new();

    public RsBa1SpectrumPanel()
    {
      InitializeUi();
    }

    public RsBa1SpectrumPanel(Context ctx) : this()
    {
      this.ctx = ctx;
      Log.Information("Creating RS-BA1 Spectrum panel");

      ctx.RsBa1SpectrumPanel = this;
      ctx.MainForm.RsBa1SpectrumMNU.Checked = true;

      Shown += (_, _) =>
      {
        RestoreInspectorSplitter();
        RefreshWindows();
        UpdatePreviewDestination();
      };

      LocationChanged += (_, _) => UpdatePreviewDestination();
      SizeChanged += (_, _) => UpdatePreviewDestination();
    }

    private void InitializeUi()
    {
      Text = "RS-BA1 Spectrum";
      Name = "RsBa1SpectrumPanel";
      ClientSize = new Size(900, 560);
      MinimumSize = new Size(640, 420);
      FormClosing += RsBa1SpectrumPanel_FormClosing;

      var root = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 3,
        Padding = new Padding(8)
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

      var toolbar = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Margin = new Padding(0, 0, 0, 6)
      };

      var windowCaption = new Label
      {
        Text = "Window:",
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 7, 5, 0)
      };
      toolbar.Controls.Add(windowCaption);

      WindowCombo.DropDownStyle = ComboBoxStyle.DropDownList;
      WindowCombo.Width = 420;
      WindowCombo.SelectedIndexChanged += (_, _) => LoadSelectedWindow();
      toolbar.Controls.Add(WindowCombo);

      RefreshBtn.Text = "Refresh";
      RefreshBtn.AutoSize = true;
      RefreshBtn.Click += (_, _) => RefreshWindows();
      toolbar.Controls.Add(RefreshBtn);

      CopyReportBtn.Text = "Copy Report";
      CopyReportBtn.AutoSize = true;
      CopyReportBtn.Enabled = false;
      CopyReportBtn.Click += (_, _) => CopyReport();
      toolbar.Controls.Add(CopyReportBtn);

      AutoRefreshCheckbox.Text = "Auto refresh";
      AutoRefreshCheckbox.AutoSize = true;
      AutoRefreshCheckbox.CheckedChanged += (_, _) =>
        RefreshTimer.Enabled = AutoRefreshCheckbox.Checked;
      toolbar.Controls.Add(AutoRefreshCheckbox);

      ShowAllCheckbox.Text = "Show all top-level windows";
      ShowAllCheckbox.AutoSize = true;
      ShowAllCheckbox.CheckedChanged += (_, _) => RefreshWindows();
      toolbar.Controls.Add(ShowAllCheckbox);

      root.Controls.Add(toolbar, 0, 0);

      StatusLabel.Dock = DockStyle.Fill;
      StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatusLabel.ForeColor = SystemColors.GrayText;
      StatusLabel.Text =
        "Inspector only: no RS-BA1 window is re-parented in this build.";
      root.Controls.Add(StatusLabel, 0, 1);

      // Do not assign SplitterDistance here. At construction time the control still has
      // its tiny default size, so a fixed distance such as 420 px can be outside the legal
      // range and SplitContainer throws before the dock layout is even established.
      InspectorSplit.Dock = DockStyle.Fill;
      InspectorSplit.Orientation = Orientation.Vertical;
      InspectorSplit.Panel1MinSize = 0;
      InspectorSplit.Panel2MinSize = 0;
      InspectorSplit.SizeChanged += (_, _) => ClampInspectorSplitter();

      WindowTree.Dock = DockStyle.Fill;
      WindowTree.HideSelection = false;
      WindowTree.AfterSelect += (_, e) => ShowWindowDetails(e.Node);
      InspectorSplit.Panel1.Controls.Add(WindowTree);

      DetailsBox.Dock = DockStyle.Fill;
      DetailsBox.Multiline = true;
      DetailsBox.ReadOnly = true;
      DetailsBox.ScrollBars = ScrollBars.Both;
      DetailsBox.WordWrap = false;
      DetailsBox.Font = new Font(FontFamily.GenericMonospace, 9F);
      InspectorSplit.Panel2.Controls.Add(DetailsBox);

      PreviewHost.Dock = DockStyle.Fill;
      PreviewHost.BackColor = Color.Black;
      PreviewHost.Margin = new Padding(0);
      PreviewHost.Resize += (_, _) => UpdatePreviewDestination();
      PreviewHost.LocationChanged += (_, _) => UpdatePreviewDestination();
      PreviewHost.VisibleChanged += (_, _) => UpdatePreviewDestination();
      PreviewHost.MouseDown += PreviewHost_MouseDown;
      PreviewHost.MouseUp += PreviewHost_MouseUp;

      PreviewMessage.Dock = DockStyle.Fill;
      PreviewMessage.TextAlign = ContentAlignment.MiddleCenter;
      PreviewMessage.ForeColor = SystemColors.GrayText;
      PreviewMessage.BackColor = Color.Black;
      PreviewMessage.Text = "Select a visible RS-BA1 Spectrum Scope.";
      PreviewHost.Controls.Add(PreviewMessage);

      PreviewTab.Padding = new Padding(0);
      PreviewTab.Controls.Add(PreviewHost);

      InspectorTab.Padding = new Padding(0);
      InspectorTab.Controls.Add(InspectorSplit);

      ViewTabs.Dock = DockStyle.Fill;
      ViewTabs.TabPages.Add(PreviewTab);
      ViewTabs.TabPages.Add(InspectorTab);
      ViewTabs.SelectedIndexChanged += (_, _) => UpdatePreviewDestination();

      root.Controls.Add(ViewTabs, 0, 2);
      Controls.Add(root);

      RefreshTimer.Tick += (_, _) => RefreshWindows(preserveSelection: true);
    }

    private void RestoreInspectorSplitter()
    {
      int extent = InspectorSplit.ClientSize.Width;
      if (extent <= InspectorSplit.SplitterWidth) return;

      int available = extent - InspectorSplit.SplitterWidth;
      int target = available / 2;

      // Keep both panes usable when possible, but gracefully allow very narrow dock widths.
      int minPane = available >= 240 ? 80 : 0;
      int max = Math.Max(minPane, available - minPane);
      int safe = Math.Clamp(target, minPane, max);

      if (InspectorSplit.SplitterDistance != safe)
        InspectorSplit.SplitterDistance = safe;
    }

    private void ClampInspectorSplitter()
    {
      int extent = InspectorSplit.ClientSize.Width;
      if (extent <= InspectorSplit.SplitterWidth) return;

      int available = extent - InspectorSplit.SplitterWidth;
      int minPane = available >= 240 ? 80 : 0;
      int max = Math.Max(minPane, available - minPane);
      int safe = Math.Clamp(InspectorSplit.SplitterDistance, minPane, max);

      if (InspectorSplit.SplitterDistance != safe)
        InspectorSplit.SplitterDistance = safe;
    }

    private void RefreshWindows(bool preserveSelection = true)
    {
      IntPtr previous = IntPtr.Zero;
      if (preserveSelection && WindowCombo.SelectedItem is RsBa1WindowInspector.WindowInfo selected)
        previous = selected.Handle;

      Windows.Clear();
      Windows.AddRange(
        RsBa1WindowInspector.EnumerateTopLevelWindows(ShowAllCheckbox.Checked));

      WindowCombo.BeginUpdate();
      try
      {
        WindowCombo.Items.Clear();
        foreach (var info in Windows)
          WindowCombo.Items.Add(info);
      }
      finally
      {
        WindowCombo.EndUpdate();
      }

      if (Windows.Count == 0)
      {
        WindowTree.Nodes.Clear();
        DetailsBox.Clear();
        CopyReportBtn.Enabled = false;
        DetachPreview();
        PreviewMessage.Text = "No RS-BA1 Spectrum Scope candidate found.";
        StatusLabel.Text = ShowAllCheckbox.Checked
          ? "No top-level windows were enumerated."
          : "No RS-BA1/Spectrum Scope candidate found. Open the RS-BA1 Spectrum Scope, then Refresh.";
        return;
      }

      int index = -1;

      if (previous != IntPtr.Zero)
      {
        int previousIndex = Windows.FindIndex(x => x.Handle == previous);
        if (previousIndex >= 0 &&
            RsBa1WindowInspector.IsSpectrumScope(Windows[previousIndex]) &&
            Windows[previousIndex].Visible)
          index = previousIndex;
      }

      if (index < 0)
      {
        var best = Windows
          .Select((info, i) => new { info, i })
          .Where(x =>
            RsBa1WindowInspector.IsSpectrumScope(x.info) &&
            x.info.Visible &&
            x.info.ClassName.Equals("TFormScope", StringComparison.OrdinalIgnoreCase) &&
            x.info.ProcessName.Equals("RemoteCtrl", StringComparison.OrdinalIgnoreCase))
          .OrderByDescending(x => x.info.Bounds.Width * x.info.Bounds.Height)
          .FirstOrDefault();

        if (best != null) index = best.i;
      }

      if (index < 0)
      {
        var visibleScope = Windows
          .Select((info, i) => new { info, i })
          .Where(x => RsBa1WindowInspector.IsSpectrumScope(x.info) && x.info.Visible)
          .OrderByDescending(x => x.info.Bounds.Width * x.info.Bounds.Height)
          .FirstOrDefault();

        if (visibleScope != null) index = visibleScope.i;
      }

      if (index < 0)
        index = Windows.FindIndex(RsBa1WindowInspector.IsSpectrumScope);

      if (index < 0) index = 0;

      WindowCombo.SelectedIndex = index;
      CopyReportBtn.Enabled = true;

      int spectrumCount = Windows.Count(RsBa1WindowInspector.IsSpectrumScope);
      StatusLabel.Text = ShowAllCheckbox.Checked
        ? $"{Windows.Count} top-level windows shown; {spectrumCount} Spectrum Scope candidate(s)."
        : $"{Windows.Count} RS-BA1-related candidate(s); {spectrumCount} Spectrum Scope candidate(s).";
    }

    private void LoadSelectedWindow()
    {
      WindowTree.BeginUpdate();
      try
      {
        WindowTree.Nodes.Clear();

        if (WindowCombo.SelectedItem is not RsBa1WindowInspector.WindowInfo rootInfo)
        {
          DetailsBox.Clear();
          return;
        }

        var descendants = RsBa1WindowInspector.EnumerateDescendants(rootInfo.Handle);
        var byParent = descendants
          .GroupBy(x => x.Parent)
          .ToDictionary(g => g.Key, g => g.ToList());

        TreeNode root = MakeNode(rootInfo);
        WindowTree.Nodes.Add(root);
        AddChildren(root, rootInfo.Handle, byParent, new HashSet<IntPtr>(), 0);
        root.Expand();

        WindowTree.SelectedNode = root;
        AttachPreview(rootInfo);
      }
      finally
      {
        WindowTree.EndUpdate();
      }
    }

    private void AttachPreview(RsBa1WindowInspector.WindowInfo info)
    {
      if (!RsBa1WindowInspector.IsSpectrumScope(info) || !info.Visible)
      {
        DetachPreview();
        PreviewMessage.Text = info.Visible
          ? "Selected window is not a Spectrum Scope."
          : "Selected Spectrum Scope is not visible.";
        return;
      }

      if (!PreviewHost.IsHandleCreated)
      {
        PreviewHost.CreateControl();
        if (!PreviewHost.IsHandleCreated)
        {
          PreviewMessage.Text = "Preview host is not ready.";
          return;
        }
      }

      IntPtr destinationRoot = GetAncestor(PreviewHost.Handle, GA_ROOT);
      if (destinationRoot == IntPtr.Zero)
      {
        PreviewMessage.Text = "Unable to resolve the preview destination window.";
        return;
      }

      if (PreviewThumbnail != IntPtr.Zero &&
          PreviewSource == info.Handle &&
          PreviewDestinationRoot == destinationRoot)
      {
        UpdatePreviewDestination();
        return;
      }

      DetachPreview();

      int compositionHr = DwmIsCompositionEnabled(out bool compositionEnabled);
      if (compositionHr < 0 || !compositionEnabled)
      {
        PreviewMessage.Text = "Desktop Window Manager composition is unavailable.";
        return;
      }

      int hr = DwmRegisterThumbnail(destinationRoot, info.Handle, out PreviewThumbnail);
      if (hr < 0 || PreviewThumbnail == IntPtr.Zero)
      {
        PreviewThumbnail = IntPtr.Zero;
        PreviewMessage.Text = $"DWM preview registration failed (0x{hr:X8}).";
        StatusLabel.Text = PreviewMessage.Text;
        Log.Warning(
          "Failed to register RS-BA1 DWM thumbnail for {Hwnd}: HRESULT 0x{Hr:X8}",
          RsBa1WindowInspector.FormatHandle(info.Handle),
          hr);
        return;
      }

      PreviewSource = info.Handle;
      PreviewDestinationRoot = destinationRoot;
      PreviewMessage.Visible = false;

      StatusLabel.Text =
        $"Live DWM preview: {RsBa1WindowInspector.FormatHandle(info.Handle)} · " +
        $"{info.Bounds.Width}×{info.Bounds.Height} · DPI {info.Dpi}.";
      Log.Information(
        "Attached RS-BA1 DWM preview: source={Source}, destination={Destination}",
        RsBa1WindowInspector.FormatHandle(PreviewSource),
        RsBa1WindowInspector.FormatHandle(PreviewDestinationRoot));

      UpdatePreviewDestination();
    }

    private void DetachPreview()
    {
      if (PreviewThumbnail != IntPtr.Zero)
      {
        _ = DwmUnregisterThumbnail(PreviewThumbnail);
        PreviewThumbnail = IntPtr.Zero;
      }

      PreviewSource = IntPtr.Zero;
      PreviewDestinationRoot = IntPtr.Zero;
      PreviewContentRect = Rectangle.Empty;
      PreviewSourceClientSize = Size.Empty;
      PreviewMessage.Visible = true;
    }

    private void UpdatePreviewDestination()
    {
      if (PreviewThumbnail == IntPtr.Zero ||
          PreviewSource == IntPtr.Zero ||
          PreviewDestinationRoot == IntPtr.Zero ||
          !PreviewHost.IsHandleCreated)
        return;

      bool visible =
        ViewTabs.SelectedTab == PreviewTab &&
        PreviewHost.Visible &&
        PreviewHost.ClientSize.Width > 1 &&
        PreviewHost.ClientSize.Height > 1;

      Rectangle screenRect = PreviewHost.RectangleToScreen(PreviewHost.ClientRectangle);
      var topLeft = new POINT(screenRect.Left, screenRect.Top);
      var bottomRight = new POINT(screenRect.Right, screenRect.Bottom);

      if (!ScreenToClient(PreviewDestinationRoot, ref topLeft) ||
          !ScreenToClient(PreviewDestinationRoot, ref bottomRight))
        return;

      int availableWidth = Math.Max(0, bottomRight.X - topLeft.X);
      int availableHeight = Math.Max(0, bottomRight.Y - topLeft.Y);

      RECT destination = new()
      {
        Left = topLeft.X,
        Top = topLeft.Y,
        Right = bottomRight.X,
        Bottom = bottomRight.Y
      };

      PreviewContentRect = Rectangle.Empty;
      PreviewSourceClientSize = Size.Empty;

      if (availableWidth > 0 && availableHeight > 0 &&
          GetClientRect(PreviewSource, out RECT sourceClient))
      {
        int sourceWidth = Math.Max(1, sourceClient.Right - sourceClient.Left);
        int sourceHeight = Math.Max(1, sourceClient.Bottom - sourceClient.Top);

        double scale = Math.Min(
          availableWidth / (double)sourceWidth,
          availableHeight / (double)sourceHeight);

        int drawWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        int localX = (availableWidth - drawWidth) / 2;
        int localY = (availableHeight - drawHeight) / 2;
        int x = topLeft.X + localX;
        int y = topLeft.Y + localY;

        PreviewContentRect = new Rectangle(localX, localY, drawWidth, drawHeight);
        PreviewSourceClientSize = new Size(sourceWidth, sourceHeight);

        destination = new RECT
        {
          Left = x,
          Top = y,
          Right = x + drawWidth,
          Bottom = y + drawHeight
        };
      }

      var props = new DWM_THUMBNAIL_PROPERTIES
      {
        dwFlags =
          DWM_TNP_RECTDESTINATION |
          DWM_TNP_OPACITY |
          DWM_TNP_VISIBLE |
          DWM_TNP_SOURCECLIENTAREAONLY,
        rcDestination = destination,
        opacity = 255,
        fVisible = visible,
        fSourceClientAreaOnly = true
      };

      int hr = DwmUpdateThumbnailProperties(PreviewThumbnail, ref props);
      if (hr < 0)
      {
        Log.Warning(
          "Failed to update RS-BA1 DWM thumbnail {Thumbnail}: HRESULT 0x{Hr:X8}",
          RsBa1WindowInspector.FormatHandle(PreviewThumbnail),
          hr);
      }
    }

    private void PreviewHost_MouseDown(object? sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left) return;
      ForwardPreviewLeftButton(e.Location, down: true);
    }

    private void PreviewHost_MouseUp(object? sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left) return;
      ForwardPreviewLeftButton(e.Location, down: false);
    }

    private void ForwardPreviewLeftButton(Point previewPoint, bool down)
    {
      if (PreviewSource == IntPtr.Zero ||
          PreviewContentRect.IsEmpty ||
          PreviewSourceClientSize.Width <= 0 ||
          PreviewSourceClientSize.Height <= 0 ||
          !PreviewContentRect.Contains(previewPoint))
        return;

      int x = (int)Math.Round(
        (previewPoint.X - PreviewContentRect.Left) *
        PreviewSourceClientSize.Width /
        (double)PreviewContentRect.Width);
      int y = (int)Math.Round(
        (previewPoint.Y - PreviewContentRect.Top) *
        PreviewSourceClientSize.Height /
        (double)PreviewContentRect.Height);

      x = Math.Clamp(x, 0, PreviewSourceClientSize.Width - 1);
      y = Math.Clamp(y, 0, PreviewSourceClientSize.Height - 1);

      nint lParam = (nint)((y << 16) | (x & 0xFFFF));
      uint message = down ? WM_LBUTTONDOWN : WM_LBUTTONUP;
      nuint wParam = down ? MK_LBUTTON : 0;

      if (!PostMessage(PreviewSource, message, wParam, lParam))
      {
        Log.Warning(
          "Failed to forward RS-BA1 preview click to {Hwnd} at {X},{Y}",
          RsBa1WindowInspector.FormatHandle(PreviewSource),
          x,
          y);
      }
    }

    private static TreeNode MakeNode(RsBa1WindowInspector.WindowInfo info)
    {
      string title = string.IsNullOrWhiteSpace(info.Title) ? "<untitled>" : info.Title;
      string className = string.IsNullOrWhiteSpace(info.ClassName) ? "?" : info.ClassName;
      return new TreeNode(
        $"{title}  [{className}]  {RsBa1WindowInspector.FormatHandle(info.Handle)}")
      {
        Tag = info
      };
    }

    private static void AddChildren(
      TreeNode parentNode,
      IntPtr parentHandle,
      Dictionary<IntPtr, List<RsBa1WindowInspector.WindowInfo>> byParent,
      HashSet<IntPtr> visited,
      int depth)
    {
      if (depth > 12) return;
      if (!byParent.TryGetValue(parentHandle, out var children)) return;

      foreach (var child in children.OrderBy(x => x.Handle.ToInt64()))
      {
        if (!visited.Add(child.Handle)) continue;

        TreeNode node = MakeNode(child);
        parentNode.Nodes.Add(node);
        AddChildren(node, child.Handle, byParent, visited, depth + 1);
      }
    }

    private void ShowWindowDetails(TreeNode? node)
    {
      if (node?.Tag is not RsBa1WindowInspector.WindowInfo info)
      {
        DetailsBox.Clear();
        return;
      }

      DetailsBox.Text = RsBa1WindowInspector.DescribeWindow(info);
    }

    private void CopyReport()
    {
      if (WindowCombo.SelectedItem is not RsBa1WindowInspector.WindowInfo info) return;

      try
      {
        string report = RsBa1WindowInspector.BuildWindowReport(info.Handle);
        Clipboard.SetText(report);
        StatusLabel.Text =
          $"Window report copied for {RsBa1WindowInspector.FormatHandle(info.Handle)}.";
        Log.Information("RS-BA1 window inspector report copied:\n{Report}", report);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to copy RS-BA1 window report");
        MessageBox.Show(
          this,
          ex.Message,
          "RS-BA1 Window Inspector",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
    }

    private void RsBa1SpectrumPanel_FormClosing(object? sender, FormClosingEventArgs e)
    {
      RefreshTimer.Stop();
      DetachPreview();

      if (ctx == null) return;

      Log.Information("Closing RS-BA1 Spectrum panel");
      ctx.RsBa1SpectrumPanel = null;
      ctx.MainForm.RsBa1SpectrumMNU.Checked = false;
    }
    private const uint GA_ROOT = 2;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const nuint MK_LBUTTON = 0x0001;
    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_OPACITY = 0x00000004;
    private const uint DWM_TNP_VISIBLE = 0x00000008;
    private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
      internal int X;
      internal int Y;

      internal POINT(int x, int y)
      {
        X = x;
        Y = y;
      }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
      internal int Left;
      internal int Top;
      internal int Right;
      internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
      internal uint dwFlags;
      internal RECT rcDestination;
      internal RECT rcSource;
      internal byte opacity;

      [MarshalAs(UnmanagedType.Bool)]
      internal bool fVisible;

      [MarshalAs(UnmanagedType.Bool)]
      internal bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(
      IntPtr hwndDestination,
      IntPtr hwndSource,
      out IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(
      IntPtr thumbnailId,
      ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(
      [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
      IntPtr hwnd,
      uint message,
      nuint wParam,
      nint lParam);
  }
}
