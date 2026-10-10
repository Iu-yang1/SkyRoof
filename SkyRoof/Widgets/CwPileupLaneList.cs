using SkyRoof.CW;

namespace SkyRoof
{
  /// <summary>
  /// Eight identity-stable, vertically scrollable skimmer message lanes.
  /// One card owns one slot: no frequency-sort reordering, no DataGridView,
  /// no transcript text boxes stealing space from the waterfall.
  /// </summary>
  internal sealed class CwPileupLaneList : UserControl
  {
    private readonly Panel scrolling = new()
    {
      Dock = DockStyle.Fill,
      AutoScroll = true
    };
    private readonly TableLayoutPanel laneTable = new()
    {
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      RowCount = 8,
      Dock = DockStyle.Top,
      Padding = new Padding(3)
    };
    private readonly Label header = new()
    {
      Dock = DockStyle.Top,
      Height = 26,
      Text = "PILEUP · 8 stable lanes",
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
      Padding = new Padding(6, 5, 0, 0)
    };
    private readonly CwPileupLaneCard[] cards =
      Enumerable.Range(0, 8)
        .Select(index => new CwPileupLaneCard(index))
        .ToArray();

    internal event Action<CwConsoleLaneIdentity>? LaneSelected;

    internal CwPileupLaneList()
    {
      Dock = DockStyle.Fill;
      MinimumSize = new Size(230, 120);
      laneTable.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));

      for (int i = 0; i < cards.Length; i++)
      {
        var card = cards[i];
        card.Selected += OnCardSelected;
        laneTable.RowStyles.Add(
          new RowStyle(SizeType.Absolute, 91));
        laneTable.Controls.Add(card, 0, i);
      }

      scrolling.Controls.Add(laneTable);
      Controls.Add(scrolling);
      Controls.Add(header);
      scrolling.Resize += (_, _) =>
      {
        laneTable.Width = Math.Max(
          210, scrolling.ClientSize.Width - 18);
      };
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

        cards[slot.Index].UpdateLane(
          slot, transcript, selected);
      }
    }

    private void OnCardSelected(CwConsoleLaneIdentity id) =>
      LaneSelected?.Invoke(id);
  }

  internal sealed class CwPileupLaneCard : Panel
  {
    private readonly int index;
    private CwConsoleLaneIdentity? identity;
    private string copyText = string.Empty;
    private readonly Label title = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
    };
    private readonly Label committed = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(FontFamily.GenericMonospace, 9.0f)
    };
    private readonly Label provisional = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(FontFamily.GenericMonospace, 9.0f, FontStyle.Italic)
    };
    private readonly Button copy = new()
    {
      Text = "Copy",
      Size = new Size(49, 24),
      Dock = DockStyle.Fill
    };

    internal event Action<CwConsoleLaneIdentity>? Selected;

    internal CwPileupLaneCard(int slot)
    {
      index = slot;
      Dock = DockStyle.Fill;
      Margin = new Padding(2);
      Padding = new Padding(4);
      BorderStyle = BorderStyle.FixedSingle;
      Cursor = Cursors.Hand;

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        RowCount = 3,
        Margin = new Padding(0),
        Padding = new Padding(0)
      };
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.Absolute, 51));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 26));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Percent, 50));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Percent, 50));

      layout.Controls.Add(title, 0, 0);
      layout.Controls.Add(copy, 1, 0);
      layout.Controls.Add(committed, 0, 1);
      layout.SetColumnSpan(committed, 2);
      layout.Controls.Add(provisional, 0, 2);
      layout.SetColumnSpan(provisional, 2);
      Controls.Add(layout);

      Click += (_, _) => SelectCard();
      title.Click += (_, _) => SelectCard();
      committed.Click += (_, _) => SelectCard();
      provisional.Click += (_, _) => SelectCard();
      copy.Click += (_, _) =>
      {
        if (!string.IsNullOrWhiteSpace(copyText))
          Clipboard.SetText(copyText);
      };

      UpdateLane(
        new CwConsoleLaneSlot(slot, null, null, false),
        null, null);
    }

    internal void UpdateLane(
      CwConsoleLaneSlot slot,
      CwTranscriptSnapshot? transcript,
      CwConsoleLaneIdentity? selected)
    {
      identity = slot.Identity;
      bool selectedNow =
        identity.HasValue && identity == selected;
      BackColor = selectedNow
        ? Color.FromArgb(216, 239, 247)
        : SystemColors.Window;
      title.BackColor = BackColor;
      committed.BackColor = BackColor;
      provisional.BackColor = BackColor;

      if (slot.Track is not CwSignalTrack track)
      {
        title.Text = $"{index + 1}  —";
        committed.Text = string.Empty;
        provisional.Text = string.Empty;
        copyText = string.Empty;
        copy.Enabled = false;
        return;
      }

      string state = slot.Present
        ? CwConsolePresentation.StateText(track)
        : "Grace";
      title.Text =
        $"{index + 1}  {CwConsolePresentation.LaneLabel(track)}  " +
        $"{track.FrequencyHz:F0} Hz  {track.SnrDb:F1} dB  {state}";
      committed.Text =
        "C  " + (transcript?.CommittedText ?? "");
      provisional.Text =
        "P  " + (transcript?.ProvisionalText ?? "");
      provisional.ForeColor = Color.FromArgb(120, 78, 28);
      title.ForeColor = !slot.Present
        ? SystemColors.GrayText
        : track.Ambiguous
          ? Color.DarkOrange
          : SystemColors.ControlText;
      copyText = transcript?.Text ?? string.Empty;
      copy.Enabled = !string.IsNullOrWhiteSpace(copyText);
    }

    private void SelectCard()
    {
      if (identity is CwConsoleLaneIdentity id)
        Selected?.Invoke(id);
    }
  }
}
