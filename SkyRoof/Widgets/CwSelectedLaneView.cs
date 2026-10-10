using SkyRoof.CW;

namespace SkyRoof
{
  /// <summary>
  /// Receive-only CW Skimmer-style detail for the selected Pileup slot.
  /// It deliberately shares the CW TX GroupBox/composer visual language,
  /// but owns no keyer, CAT, audio, or model resource.
  /// </summary>
  internal sealed class CwSelectedLaneView : GroupBox
  {
    private readonly Label status = new()
    {
      Dock = DockStyle.Fill,
      AutoEllipsis = true,
      TextAlign = ContentAlignment.MiddleLeft,
      UseMnemonic = false,
      Margin = new Padding(1, 0, 4, 0)
    };

    private readonly RichTextBox transcript = new()
    {
      Dock = DockStyle.Fill,
      ReadOnly = true,
      Multiline = true,
      WordWrap = true,
      ScrollBars = RichTextBoxScrollBars.Vertical,
      DetectUrls = false,
      HideSelection = false,
      BorderStyle = BorderStyle.FixedSingle,
      Font = new Font(FontFamily.GenericMonospace, 10.5f),
      Margin = new Padding(1, 1, 1, 1)
    };

    private readonly Button copy = new()
    {
      Text = "Copy RX",
      Dock = DockStyle.Fill,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      MinimumSize = new Size(76, 25),
      Padding = new Padding(6, 0, 6, 0),
      Margin = new Padding(3, 0, 1, 0)
    };

    private CwConsoleLaneIdentity? currentIdentity;
    private string copyText = string.Empty;
    private int committedLength;
    private int provisionalLength;

    internal string StatusText => status.Text;
    internal string DisplayedText => transcript.Text;
    internal string CopyText => copyText;
    internal CwConsoleLaneIdentity? SelectedIdentity => currentIdentity;
    internal RichTextBox TranscriptControl => transcript;
    internal Button CopyButton => copy;

    internal CwSelectedLaneView()
    {
      Text = "CW RX — Selected Lane";
      Dock = DockStyle.Fill;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;
      MinimumSize = new Size(0, 112);
      Margin = new Padding(0, 2, 0, 0);
      Padding = new Padding(5);

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2,
        RowCount = 2,
        Margin = new Padding(0)
      };
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 29));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 60));

      layout.Controls.Add(status, 0, 0);
      layout.Controls.Add(copy, 1, 0);
      layout.Controls.Add(transcript, 0, 1);
      layout.SetColumnSpan(transcript, 2);
      Controls.Add(layout);

      copy.Click += (_, _) =>
      {
        if (copyText.Length > 0)
          Clipboard.SetText(copyText);
      };

      UpdateLane(null, null);
    }

    /// <summary>
    /// Refresh one selected slot. Ignore unchanged text so 250-ms UI polls
    /// do not steal selection, cursor, or scroll position from the operator.
    /// Temporary (provisional) characters are visually distinguished and
    /// are never mistaken for committed text.
    /// </summary>
    internal void UpdateLane(
      CwConsoleLaneSlot? selectedSlot,
      CwTranscriptSnapshot? selectedTranscript)
    {
      if (selectedSlot is not CwConsoleLaneSlot slot ||
          slot.Identity is not CwConsoleLaneIdentity identity ||
          slot.Track is not CwSignalTrack track)
      {
        bool changed = currentIdentity.HasValue;
        currentIdentity = null;
        status.Text = "RX: select a Pileup lane or waterfall track";
        copyText = string.Empty;
        committedLength = 0;
        provisionalLength = 0;
        copy.Enabled = false;
        if (changed || transcript.Text != "Select a lane to see CW text.")
        {
          transcript.Text = "Select a lane to see CW text.";
          transcript.Select(0, 0);
        }
        return;
      }

      bool laneChanged = currentIdentity != identity;
      currentIdentity = identity;
      string state = slot.Present
        ? CwConsolePresentation.StateText(track)
        : "Grace";
      status.Text =
        $"Slot {slot.Index + 1}/8 · {identity} · " +
        $"{track.FrequencyHz:F0} Hz · {track.SnrDb:F1} dB · {state}";

      string committed = selectedTranscript?.CommittedText ?? string.Empty;
      string provisional = selectedTranscript?.ProvisionalText ?? string.Empty;
      copyText = committed + provisional;
      copy.Enabled = copyText.Length > 0;

      string shown = copyText.Length > 0
        ? copyText : "Listening for CW…";
      // A provisional suffix can become committed while the combined
      // characters remain identical; repaint its styling in that case.
      bool stylingChanged =
        committedLength != committed.Length ||
        provisionalLength != provisional.Length;
      if (laneChanged || transcript.Text != shown || stylingChanged)
      {
        transcript.Text = shown;
        transcript.SelectAll();
        transcript.SelectionColor = SystemColors.WindowText;
        if (provisional.Length > 0)
        {
          transcript.Select(committed.Length, provisional.Length);
          transcript.SelectionColor = Color.FromArgb(155, 93, 32);
        }
        transcript.Select(transcript.TextLength, 0);
        transcript.ScrollToCaret();
      }
      committedLength = committed.Length;
      provisionalLength = provisional.Length;
    }
  }
}
