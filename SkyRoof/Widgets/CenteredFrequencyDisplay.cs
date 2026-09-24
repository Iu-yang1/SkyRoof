using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Frequency readout centered by the actual rendered pixel bounds rather than font metrics.
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

      if (string.IsNullOrEmpty(Text) || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        return;

      TextFormatFlags flags =
        TextFormatFlags.SingleLine |
        TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix;

      Size measured = TextRenderer.MeasureText(Text, Font, Size.Empty, flags);
      int pad = Math.Max(8, DeviceDpi / 12);
      int scratchWidth = Math.Max(1, measured.Width + pad * 2);
      int scratchHeight = Math.Max(1, measured.Height + pad * 2);

      using var scratch = new Bitmap(scratchWidth, scratchHeight);
      using (Graphics sg = Graphics.FromImage(scratch))
      {
        sg.Clear(BackColor);
        TextRenderer.DrawText(
          sg,
          Text,
          Font,
          new Point(pad, pad),
          ForeColor,
          BackColor,
          flags);
      }

      Rectangle ink = FindInkBounds(scratch);
      if (ink.IsEmpty) return;

      int x = (ClientSize.Width - ink.Width) / 2;
      int y = (ClientSize.Height - ink.Height) / 2;

      // ClientSize and the detected ink bounds are device pixels. DrawImage(destinationRect, ...)
      // applies the target Graphics page-unit/DPI transform again on high-DPI displays, which
      // shifts/scales the supposedly centered coordinates. Draw the bitmap unscaled instead and
      // offset the scratch origin so the measured ink rectangle lands exactly at (x, y).
      e.Graphics.DrawImageUnscaled(
        scratch,
        x - ink.X,
        y - ink.Y);
    }

    private Rectangle FindInkBounds(Bitmap bitmap)
    {
      int left = bitmap.Width;
      int top = bitmap.Height;
      int right = -1;
      int bottom = -1;
      int background = BackColor.ToArgb();

      for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
          if (bitmap.GetPixel(x, y).ToArgb() != background)
          {
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
          }

      return right < left || bottom < top
        ? Rectangle.Empty
        : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
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
