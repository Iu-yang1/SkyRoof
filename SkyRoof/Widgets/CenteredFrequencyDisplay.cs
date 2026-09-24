using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Black frequency readout whose text is explicitly centered by TextRenderer.
  /// This avoids the baseline/DPI-dependent vertical placement of a large WinForms Label.
  /// </summary>
  internal sealed class CenteredFrequencyDisplay : Control
  {
    public CenteredFrequencyDisplay()
    {
      DoubleBuffered = true;
      BackColor = Color.Black;
      ForeColor = Color.Aqua;
      Font = new Font("Microsoft Sans Serif", 14F);
      SetStyle(
        ControlStyles.UserPaint |
        ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer,
        true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      e.Graphics.Clear(BackColor);

      TextRenderer.DrawText(
        e.Graphics,
        Text,
        Font,
        ClientRectangle,
        ForeColor,
        BackColor,
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine |
        TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix);
    }

    protected override void OnTextChanged(EventArgs e)
    {
      base.OnTextChanged(e);
      Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
      base.OnForeColorChanged(e);
      Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
      base.OnBackColorChanged(e);
      Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
      base.OnFontChanged(e);
      Invalidate();
    }
  }
}
