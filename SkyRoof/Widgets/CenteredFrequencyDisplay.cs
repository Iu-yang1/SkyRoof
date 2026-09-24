using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Black frequency readout whose visible glyphs are geometrically centered.
  /// This deliberately does not rely on Label.TextAlign/TextRenderer font metrics,
  /// because those center the font layout box rather than the actual digit outlines.
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

      Graphics g = e.Graphics;
      g.Clear(BackColor);

      if (string.IsNullOrEmpty(Text) || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        return;

      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;

      using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
      format.FormatFlags |= StringFormatFlags.NoWrap;

      float emSize = Font.SizeInPoints * g.DpiY / 72F;
      using var path = new GraphicsPath();
      path.AddString(
        Text,
        Font.FontFamily,
        (int)Font.Style,
        emSize,
        PointF.Empty,
        format);

      RectangleF ink = path.GetBounds();

      // Center the actual visible glyph outline, not the font's ascent/descent layout box.
      float offsetX = (ClientSize.Width - ink.Width) / 2F - ink.X;
      float offsetY = (ClientSize.Height - ink.Height) / 2F - ink.Y;

      using var matrix = new Matrix();
      matrix.Translate(offsetX, offsetY);
      path.Transform(matrix);

      using var brush = new SolidBrush(ForeColor);
      g.FillPath(brush, path);
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
