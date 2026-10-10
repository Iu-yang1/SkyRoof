using SkyRoof.CW;

namespace SkyRoof
{
  /// <summary>
  /// Eight identity-stable, vertically scrollable skimmer message lanes.
  /// Every card has an independent wrapped/scrollable decoded transcript,
  /// without sacrificing the large left-hand frequency/time waterfall.
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
          new RowStyle(SizeType.Absolute, 126));
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
    private readonly ToolTip detailTip = new()
    {
      AutoPopDelay = 15000,
      InitialDelay = 400,
      ReshowDelay = 150
    };
    private CwConsoleLaneIdentity? identity;
    private string copyText = string.Empty;
    private readonly Label title = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
    };
    private readonly Label signalStatus = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      UseMnemonic = false,
      Font = new Font(SystemFonts.DefaultFont, FontStyle.Regular)
    };
    private readonly RichTextBox decodedText = new()
    {
      Dock = DockStyle.Fill,
      ReadOnly = true,
      WordWrap = true,
      Multiline = true,
      ScrollBars = RichTextBoxScrollBars.Vertical,
      BorderStyle = BorderStyle.None,
      DetectUrls = false,
      HideSelection = false,
      TabStop = false,
      Font = new Font(FontFamily.GenericMonospace, 9.5f),
      Margin = new Padding(0, 3, 0, 0)
    };
    private readonly Button copy = new()
    {
      Text = "Copy",
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      MinimumSize = new Size(70, 28),
      Padding = new Padding(7, 1, 7, 1),
      Margin = new Padding(3, 1, 3, 1),
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
      // Auto-size this column to the button's *preferred* text width at
      // the current Windows DPI. A fixed 51px cell clipped Copy to Cop.
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 31));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 20));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Percent, 100));

      layout.Controls.Add(title, 0, 0);
      layout.Controls.Add(copy, 1, 0);
      layout.Controls.Add(signalStatus, 0, 1);
      layout.SetColumnSpan(signalStatus, 2);
      layout.Controls.Add(decodedText, 0, 2);
      layout.SetColumnSpan(decodedText, 2);
      Controls.Add(layout);

      Click += (_, _) => SelectCard();
      title.Click += (_, _) => SelectCard();
      signalStatus.Click += (_, _) => SelectCard();
      decodedText.MouseDown += (_, e) =>
      {
        if (e.Button == MouseButtons.Left)
          SelectCard();
      };
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
      signalStatus.BackColor = BackColor;
      decodedText.BackColor = BackColor;

      if (slot.Track is not CwSignalTrack track)
      {
        title.Text = $"{index + 1}  —";
        signalStatus.Text = string.Empty;
        if (decodedText.TextLength > 0)
          decodedText.Clear();
        copyText = string.Empty;
        copy.Enabled = false;
        detailTip.SetToolTip(title, string.Empty);
        detailTip.SetToolTip(signalStatus, string.Empty);
        return;
      }

      string state = slot.Present
        ? CwConsolePresentation.StateText(track)
        : "Grace";
      title.Text =
        $"{index + 1}  {CwConsolePresentation.LaneLabel(track)}  " +
        $"{track.FrequencyHz:F0} Hz";
      signalStatus.Text = $"{track.SnrDb:F1} dB · {state}" +
        (Math.Abs(track.DriftHzPerSecond) >= 0.1
          ? $" · {track.DriftHzPerSecond:+0.0;-0.0} Hz/s"
          : "");
      title.ForeColor = !slot.Present
        ? SystemColors.GrayText
        : track.Ambiguous ? Color.DarkOrange
        : SystemColors.ControlText;
      signalStatus.ForeColor = title.ForeColor;
      detailTip.SetToolTip(title, title.Text);
      detailTip.SetToolTip(signalStatus,
        $"{signalStatus.Text} · {track.DriftHzPerSecond:+0.0;-0.0;0.0} Hz/s");

      // RichTextBox wraps arbitrarily long decoded messages and keeps its
      // own vertical scrollbar. Only mutate it when the text changes, so
      // the 250-ms UI refresh cannot reset the user's selection/scroll.
      string committed = transcript?.CommittedText ?? string.Empty;
      string provisional = transcript?.ProvisionalText ?? string.Empty;
      string stableLine = committed.Length > 0
        ? committed : "(waiting for committed text)";
      string rendered = stableLine +
        (provisional.Length > 0 ? "\n⟦" + provisional + "⟧" : "");
      if (decodedText.Text != rendered)
      {
        decodedText.Text = rendered;
        int provisionalOffset = stableLine.Length + 1;
        if (provisional.Length > 0)
        {
          decodedText.Select(
            provisionalOffset, provisional.Length + 2);
          decodedText.SelectionColor = Color.FromArgb(155, 93, 32);
        }
        decodedText.Select(decodedText.TextLength, 0);
        decodedText.ScrollToCaret();
      }
      copyText = transcript?.Text ?? string.Empty;
      copy.Enabled = !string.IsNullOrWhiteSpace(copyText);
    }

    private void SelectCard()
    {
      if (identity is CwConsoleLaneIdentity id)
        Selected?.Invoke(id);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
        detailTip.Dispose();
      base.Dispose(disposing);
    }
  }
}
