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
