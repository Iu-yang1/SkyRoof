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
    private readonly System.Windows.Forms.Timer RefreshTimer = new() { Interval = 1500 };
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

      Shown += (_, _) => RefreshWindows();
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

      var split = new SplitContainer
      {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterDistance = 420,
        Panel1MinSize = 260,
        Panel2MinSize = 260
      };

      WindowTree.Dock = DockStyle.Fill;
      WindowTree.HideSelection = false;
      WindowTree.AfterSelect += (_, e) => ShowWindowDetails(e.Node);
      split.Panel1.Controls.Add(WindowTree);

      DetailsBox.Dock = DockStyle.Fill;
      DetailsBox.Multiline = true;
      DetailsBox.ReadOnly = true;
      DetailsBox.ScrollBars = ScrollBars.Both;
      DetailsBox.WordWrap = false;
      DetailsBox.Font = new Font(FontFamily.GenericMonospace, 9F);
      split.Panel2.Controls.Add(DetailsBox);

      root.Controls.Add(split, 0, 2);
      Controls.Add(root);

      RefreshTimer.Tick += (_, _) => RefreshWindows(preserveSelection: true);
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
        StatusLabel.Text = ShowAllCheckbox.Checked
          ? "No top-level windows were enumerated."
          : "No RS-BA1/Spectrum Scope candidate found. Open the RS-BA1 Spectrum Scope, then Refresh.";
        return;
      }

      int index = -1;
      if (previous != IntPtr.Zero)
        index = Windows.FindIndex(x => x.Handle == previous);

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
      }
      finally
      {
        WindowTree.EndUpdate();
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

      if (ctx == null) return;

      Log.Information("Closing RS-BA1 Spectrum panel");
      ctx.RsBa1SpectrumPanel = null;
      ctx.MainForm.RsBa1SpectrumMNU.Checked = false;
    }
  }
}
