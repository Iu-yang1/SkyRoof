using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SkyRoof;

namespace VE3NEA.Clock
{
  public partial class ClockWidget : UserControl
  {
    // the display mode is application-wide: every panel that shows a wall-clock time formats it
    // through the static helpers below, so the field is static and the widget is just its view
    private static bool utcMode;

    public Context ctx;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    [DisplayName("UTC Mode")]
    public bool UtcMode { get => utcMode; set => SetUtcMode(value); }

    public ClockWidget()
    {
      InitializeComponent();
      ConfigureAdaptiveLayout();
    }

    private void ConfigureAdaptiveLayout()
    {
      SuspendLayout();
      Controls.Clear();
      // Replace the designer's pixel offsets with three resizable rows. At
      // 125%/150% scaling the date and time no longer run beneath the badges.
      var root = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
        Margin = Padding.Empty, Padding = new Padding(2, 0, 2, 0)
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 23));
      timeLabel.AutoSize = false;
      timeLabel.Dock = DockStyle.Fill;
      timeLabel.TextAlign = ContentAlignment.MiddleCenter;
      timeLabel.Margin = Padding.Empty;
      timeLabel.Font = new Font("Segoe UI", 14F);
      dateLabel.AutoSize = false;
      dateLabel.Dock = DockStyle.Fill;
      dateLabel.AutoEllipsis = true;
      dateLabel.TextAlign = ContentAlignment.MiddleCenter;
      dateLabel.Margin = Padding.Empty;

      var modes = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
        Margin = Padding.Empty, Padding = Padding.Empty
      };
      modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      foreach (var badge in new[] { localLabel, utcLabel })
      {
        badge.Dock = DockStyle.Fill;
        badge.Margin = new Padding(3, 0, 3, 0);
        badge.AutoSize = false;
        badge.TextAlign = ContentAlignment.MiddleCenter;
      }
      modes.Controls.Add(localLabel, 0, 0);
      modes.Controls.Add(utcLabel, 1, 0);
      root.Controls.Add(timeLabel, 0, 0);
      root.Controls.Add(dateLabel, 0, 1);
      root.Controls.Add(modes, 0, 2);
      Controls.Add(root);
      ResumeLayout(true);
    }

    private void utcLabel_Click(object sender, EventArgs e)
    {
      SetUtcMode(!utcMode);
    }

    public void SetUtcMode(bool value)
    {
      utcMode = value;
      utcLabel.BackColor = utcMode ? Color.Aqua : Color.Teal;
      localLabel.BackColor = utcMode ? Color.Teal : Color.Aqua;
      ShowTime();

      // the designer assigns UtcMode before MainForm hands the widget its context
      ctx?.MainForm?.RefreshTimeDisplay();
    }

    public void ShowTime()
    {
      DateTime now = Now;
      timeLabel.Text = now.ToString("HH:mm:ss");
      dateLabel.Text = now.ToString("MMMM dd, yyyy");
    }

    private void dateLabel_Click(object sender, EventArgs e)
    {

    }




    //----------------------------------------------------------------------------------------------
    //                                  time display mode
    //----------------------------------------------------------------------------------------------
    public static bool IsUtc => utcMode;

    // the clock as the user reads it. For display only: all arithmetic stays in UTC
    public static DateTime Now => utcMode ? DateTime.UtcNow : DateTime.Now;

    // the mode as the two clickable labels of this widget name it, for spelling the mode out
    // where there is room for it
    public static string ModeName => utcMode ? "UTC" : "Local";

    // short marker naming the zone a displayed time is in. Never empty, so that text kept
    // verbatim after the mode changes, such as the telemetry tree, remains unambiguous
    public static string Suffix => utcMode ? "Z" : "LT";

    // converts an instant for display. Times taken from the satellite library carry
    // DateTimeKind.Unspecified but are UTC, so the kind is stated before converting
    public static DateTime FromUtc(DateTime utc)
    {
      if (utc == DateTime.MinValue || utc == DateTime.MaxValue) return utc;

      utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
      return utcMode ? utc : utc.ToLocalTime();
    }

    // formats an instant for display and appends the zone marker
    public static string Stamp(DateTime utc, string format)
    {
      return $"{FromUtc(utc).ToString(format)} {Suffix}";
    }
  }
}
