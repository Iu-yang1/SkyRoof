using SkyRoof.CW;

namespace SkyRoof
{
  /// <summary>
  /// Compact eight-lane skimmer overview. Complete/wrapped RX messages
  /// remain in the existing Selected Lane pane below the waterfall;
  /// the overview must not force a 1000+ px tall window.
  /// </summary>
  internal sealed class CwPileupLaneList : UserControl
  {
    internal const int LaneCount = 8;
    internal const int MinimumRowHeight = 34;
    internal const int MaximumRowHeight = 52;

    private readonly Panel scrolling = new()
    {
      Dock = DockStyle.Fill,
      AutoScroll = true
    };
    private readonly TableLayoutPanel laneTable = new()
    {
      ColumnCount = 1,
      RowCount = LaneCount,
      Dock = DockStyle.Top,
      AutoSize = false,
      Margin = new Padding(0),
      Padding = new Padding(2)
    };
    private readonly Label header = new()
    {
      Dock = DockStyle.Top,
      Height = 22,
      Text = "PILEUP · 8 lanes",
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
      Padding = new Padding(5, 3, 0, 0)
    };
    private readonly CwPileupLaneCard[] cards =
      Enumerable.Range(0, LaneCount)
        .Select(i => new CwPileupLaneCard(i)).ToArray();

    internal event Action<CwConsoleLaneIdentity>? LaneSelected;

    internal CwPileupLaneList()
    {
      Dock = DockStyle.Fill;
      MinimumSize = new Size(160, 120);
      laneTable.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      foreach (var card in cards)
      {
        card.Selected += identity => LaneSelected?.Invoke(identity);
        laneTable.RowStyles.Add(
          new RowStyle(SizeType.Absolute, MinimumRowHeight));
        laneTable.Controls.Add(card, 0, card.Index);
      }

      scrolling.Controls.Add(laneTable);
      Controls.Add(scrolling);
      Controls.Add(header);
      scrolling.Resize += (_, _) => ResizeRows();
      ResizeRows();
    }

    /// <summary>
    /// Fill all eight rows when possible. Scroll only if the physical pane
    /// is shorter than eight readable rows, including at high Windows DPI.
    /// </summary>
    internal static int CalculateRowHeight(int viewportHeight)
    {
      return Math.Clamp(viewportHeight / LaneCount,
        MinimumRowHeight, MaximumRowHeight);
    }

    private void ResizeRows()
    {
      int rowHeight = CalculateRowHeight(scrolling.ClientSize.Height);
      laneTable.SuspendLayout();
      for (int i = 0; i < LaneCount; i++)
        laneTable.RowStyles[i].Height = rowHeight;
      laneTable.Height = LaneCount * rowHeight + laneTable.Padding.Vertical;
      bool needsScroll = laneTable.Height > scrolling.ClientSize.Height;
      laneTable.Width = Math.Max(1, scrolling.ClientSize.Width -
        (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0));
      laneTable.ResumeLayout(true);
    }

    internal void UpdateLanes(
      IReadOnlyList<CwConsoleLaneSlot> slots,
      IReadOnlyDictionary<CwConsoleLaneIdentity, CwTranscriptSnapshot> transcripts,
      CwConsoleLaneIdentity? selected)
    {
      foreach (CwConsoleLaneSlot slot in slots)
      {
        if (slot.Index < 0 || slot.Index >= cards.Length)
          continue;

        CwTranscriptSnapshot? transcript = null;
        if (slot.Identity is CwConsoleLaneIdentity id)
          transcripts.TryGetValue(id, out transcript);
        cards[slot.Index].UpdateLane(slot, transcript, selected);
      }
    }
  }

  internal sealed class CwPileupLaneCard : Panel
  {
    internal int Index { get; }
    private CwConsoleLaneIdentity? identity;
    private string copyText = string.Empty;
    private readonly ToolTip tip = new();
    private readonly Label title = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
      TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label preview = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      TextAlign = ContentAlignment.MiddleLeft,
      Font = new Font(FontFamily.GenericMonospace, 9.0f),
      Padding = new Padding(3, 0, 1, 0)
    };
    private readonly Button copy = new()
    {
      Text = "Copy",
      Dock = DockStyle.Fill,
      AutoSize = false,
      Margin = new Padding(0),
      Padding = new Padding(1, 0, 1, 0),
      TabStop = false
    };

    internal event Action<CwConsoleLaneIdentity>? Selected;

    internal CwPileupLaneCard(int index)
    {
      Index = index;
      Dock = DockStyle.Fill;
      Margin = new Padding(1);
      Padding = new Padding(1);
      BorderStyle = BorderStyle.FixedSingle;
      Cursor = Cursors.Hand;

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        RowCount = 2,
        Margin = new Padding(0),
        Padding = new Padding(0)
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 49));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
      layout.Controls.Add(title, 0, 0);
      layout.Controls.Add(copy, 1, 0);
      layout.SetRowSpan(copy, 2);
      layout.Controls.Add(preview, 0, 1);
      Controls.Add(layout);

      Click += (_, _) => SelectCard();
      title.Click += (_, _) => SelectCard();
      preview.Click += (_, _) => SelectCard();
      copy.Click += (_, _) =>
      {
        if (copyText.Length > 0)
          Clipboard.SetText(copyText);
      };
      UpdateLane(new CwConsoleLaneSlot(index, null, null, false),
        null, null);
    }

    internal void UpdateLane(
      CwConsoleLaneSlot slot,
      CwTranscriptSnapshot? transcript,
      CwConsoleLaneIdentity? selected)
    {
      identity = slot.Identity;
      bool isSelected = identity.HasValue && identity == selected;
      BackColor = isSelected
        ? Color.FromArgb(216, 239, 247)
        : SystemColors.Window;
      title.BackColor = BackColor;
      preview.BackColor = BackColor;

      if (slot.Track is not CwSignalTrack track)
      {
        title.Text = $"{Index + 1}  —";
        preview.Text = string.Empty;
        copyText = string.Empty;
        copy.Enabled = false;
        return;
      }

      string state = slot.Present
        ? CwConsolePresentation.StateText(track) : "Grace";
      title.Text = $"{Index + 1}  {CwConsolePresentation.LaneLabel(track)} " +
        $"{track.FrequencyHz:F0} Hz · {track.SnrDb:F0}dB · {state}";
      title.ForeColor = !slot.Present ? SystemColors.GrayText :
        track.Ambiguous ? Color.DarkOrange : SystemColors.ControlText;
      preview.ForeColor = title.ForeColor;
      tip.SetToolTip(title,
        $"{title.Text} · drift {track.DriftHzPerSecond:+0.0;-0.0;0.0} Hz/s");

      string committed = transcript?.CommittedText ?? string.Empty;
      string provisional = transcript?.ProvisionalText ?? string.Empty;
      copyText = transcript?.Text ?? string.Empty;
      // Single-line summary is intentionally ellipsized. Selecting a lane
      // exposes its full scrollable transcript, including provisional text.
      string shown = copyText.Length > 0
        ? committed + (provisional.Length > 0 ? " ⟦" + provisional + "⟧" : "")
        : "(listening)";
      if (preview.Text != shown)
        preview.Text = shown;
      tip.SetToolTip(preview, shown);
      copy.Enabled = copyText.Length > 0;
    }

    private void SelectCard()
    {
      if (identity is CwConsoleLaneIdentity id)
        Selected?.Invoke(id);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
        tip.Dispose();
      base.Dispose(disposing);
    }
  }
}
