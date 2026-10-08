using System.Drawing.Drawing2D;
using VE3NEA;

namespace SkyRoof
{
  internal enum RotatorJogDirection
  {
    None,
    AzimuthDown,
    AzimuthUp,
    ElevationUp,
    ElevationDown,
    Stop
  }

  // Compact manual-control card hosted inside the Rotator status drop-down.
  // The directional wheel intentionally uses relative position commands rather
  // than hamlib's continuous-move command so it works with the same broad set
  // of rotctld backends already supported by SkyRoof.
  internal sealed class RotatorControlCard : UserControl
  {
    private readonly RotatorDirectionWheel Wheel = new();
    private readonly Label ConnectionLabel = new();
    private readonly Label ActualLabel = new();
    private readonly Label TargetLabel = new();
    private readonly Label SatelliteLabel = new();
    private readonly NumericUpDown StepSpinner = new();
    private readonly NumericUpDown AzimuthSpinner = new();
    private readonly NumericUpDown ElevationSpinner = new();
    private readonly Button GoButton = new();
    private readonly Button ParkButton = new();
    private readonly Button TrackButton = new();
    private readonly System.Windows.Forms.Timer RefreshTimer = new();

    private Context? ctx;
    private RotatorWidget? rotator;

    internal RotatorControlCard()
    {
      AutoScaleMode = AutoScaleMode.Font;
      BackColor = SystemColors.Control;
      ForeColor = SystemColors.ControlText;
      Margin = Padding.Empty;
      Padding = new Padding(10);
      Size = new Size(318, 386);
      MinimumSize = Size;
      MaximumSize = Size;

      var title = new Label
      {
        AutoSize = false,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        Location = new Point(12, 10),
        Size = new Size(190, 24),
        Text = "Manual Rotator",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(title);

      ConnectionLabel.AutoSize = false;
      ConnectionLabel.Location = new Point(202, 10);
      ConnectionLabel.Size = new Size(102, 24);
      ConnectionLabel.TextAlign = ContentAlignment.MiddleRight;
      Controls.Add(ConnectionLabel);

      ActualLabel.AutoSize = false;
      ActualLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
      ActualLabel.Location = new Point(12, 38);
      ActualLabel.Size = new Size(292, 20);
      Controls.Add(ActualLabel);

      TargetLabel.AutoSize = false;
      TargetLabel.Location = new Point(12, 58);
      TargetLabel.Size = new Size(292, 20);
      Controls.Add(TargetLabel);

      SatelliteLabel.AutoSize = false;
      SatelliteLabel.Location = new Point(12, 78);
      SatelliteLabel.Size = new Size(292, 20);
      Controls.Add(SatelliteLabel);

      Wheel.Location = new Point(18, 104);
      Wheel.Size = new Size(202, 202);
      Wheel.JogRequested += Wheel_JogRequested;
      Wheel.StopRequested += (_, _) => rotator?.StopRotation();
      Controls.Add(Wheel);

      var stepLabel = new Label
      {
        AutoSize = false,
        Location = new Point(232, 112),
        Size = new Size(72, 19),
        Text = "Step",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(stepLabel);

      StepSpinner.DecimalPlaces = 1;
      StepSpinner.Increment = 0.5M;
      StepSpinner.Minimum = 0.1M;
      StepSpinner.Maximum = 30M;
      StepSpinner.Value = 5M;
      StepSpinner.Location = new Point(232, 134);
      StepSpinner.Size = new Size(72, 23);
      Controls.Add(StepSpinner);

      var hint = new Label
      {
        AutoSize = false,
        ForeColor = SystemColors.GrayText,
        Location = new Point(228, 166),
        Size = new Size(80, 68),
        Text = "Hold a direction\nto repeat.\n\nCenter = STOP",
        TextAlign = ContentAlignment.TopLeft
      };
      Controls.Add(hint);

      TrackButton.Location = new Point(232, 247);
      TrackButton.Size = new Size(72, 26);
      TrackButton.Text = "TRACK";
      TrackButton.Click += (_, _) => rotator?.ToggleTracking();
      Controls.Add(TrackButton);

      ParkButton.Location = new Point(232, 280);
      ParkButton.Size = new Size(72, 26);
      ParkButton.Text = "PARK";
      ParkButton.Click += (_, _) => rotator?.ManualPark();
      Controls.Add(ParkButton);

      var divider = new Label
      {
        BorderStyle = BorderStyle.Fixed3D,
        Location = new Point(12, 316),
        Size = new Size(292, 2)
      };
      Controls.Add(divider);

      var azLabel = new Label
      {
        AutoSize = false,
        Location = new Point(12, 326),
        Size = new Size(30, 23),
        Text = "AZ",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(azLabel);

      AzimuthSpinner.DecimalPlaces = 1;
      AzimuthSpinner.Increment = 1M;
      AzimuthSpinner.Location = new Point(42, 326);
      AzimuthSpinner.Size = new Size(68, 23);
      Controls.Add(AzimuthSpinner);

      var elLabel = new Label
      {
        AutoSize = false,
        Location = new Point(119, 326),
        Size = new Size(27, 23),
        Text = "EL",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(elLabel);

      ElevationSpinner.DecimalPlaces = 1;
      ElevationSpinner.Increment = 1M;
      ElevationSpinner.Location = new Point(146, 326);
      ElevationSpinner.Size = new Size(68, 23);
      Controls.Add(ElevationSpinner);

      GoButton.Location = new Point(226, 325);
      GoButton.Size = new Size(78, 25);
      GoButton.Text = "GO";
      GoButton.Click += (_, _) =>
        rotator?.ManualMoveToDegrees(
          (double)AzimuthSpinner.Value,
          (double)ElevationSpinner.Value);
      Controls.Add(GoButton);

      var footer = new Label
      {
        AutoSize = false,
        ForeColor = SystemColors.GrayText,
        Location = new Point(12, 355),
        Size = new Size(292, 20),
        Text = "Right-click Rotator status to open this panel.",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(footer);

      RefreshTimer.Interval = 250;
      RefreshTimer.Tick += (_, _) => RefreshState();
      RefreshTimer.Start();
    }

    internal void Attach(Context context, RotatorWidget widget)
    {
      ctx = context;
      rotator = widget;

      var sett = ctx.Settings.Rotator;
      StepSpinner.Value =
        Math.Clamp(
          (decimal)sett.StepSize,
          StepSpinner.Minimum,
          StepSpinner.Maximum);

      UpdateSpinnerLimits();
      RefreshState();
    }

    internal void RefreshState()
    {
      if (ctx == null || rotator == null) return;

      UpdateSpinnerLimits();

      Bearing? actual = rotator.GetManualActualBearing();
      Bearing? target = rotator.GetManualTargetBearing();
      Bearing? satellite = rotator.SatelliteBearing;

      ActualLabel.Text =
        "Antenna   " +
        FormatBearing(actual);

      TargetLabel.Text =
        "Command  " +
        FormatBearing(target);

      SatelliteLabel.Text =
        "Satellite " +
        FormatBearing(satellite);

      bool enabled = ctx.Settings.Rotator.Enabled;
      bool connected = rotator.IsRunning();

      ConnectionLabel.Text =
        !enabled ? "Disabled" :
        connected ? "Connected" :
        "Connecting";

      ConnectionLabel.ForeColor =
        !enabled ? SystemColors.GrayText :
        connected ? Color.LimeGreen :
        Color.IndianRed;

      Wheel.Enabled = enabled;
      GoButton.Enabled = enabled;
      ParkButton.Enabled = enabled;
      TrackButton.Enabled = enabled && rotator.TrackCheckbox.Enabled;

      TrackButton.Text =
        rotator.IsTracking
          ? "TRACK ✓"
          : "TRACK";

      if (!AzimuthSpinner.Focused)
        SetSpinnerValue(
          AzimuthSpinner,
          target?.AzDeg ?? actual?.AzDeg);

      if (!ElevationSpinner.Focused)
        SetSpinnerValue(
          ElevationSpinner,
          target?.ElDeg ?? actual?.ElDeg);

      Wheel.ActualBearing = actual;
      Wheel.TargetBearing = target;
      Wheel.SatelliteBearing = satellite;
      Wheel.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        RefreshTimer.Stop();
        RefreshTimer.Dispose();
        Wheel.Dispose();
      }

      base.Dispose(disposing);
    }

    private void Wheel_JogRequested(
      RotatorJogDirection direction)
    {
      if (rotator == null) return;

      double step = (double)StepSpinner.Value;

      switch (direction)
      {
        case RotatorJogDirection.AzimuthDown:
          rotator.ManualJog(-step, 0);
          break;
        case RotatorJogDirection.AzimuthUp:
          rotator.ManualJog(step, 0);
          break;
        case RotatorJogDirection.ElevationUp:
          rotator.ManualJog(0, step);
          break;
        case RotatorJogDirection.ElevationDown:
          rotator.ManualJog(0, -step);
          break;
      }

      RefreshState();
    }

    private void UpdateSpinnerLimits()
    {
      if (ctx == null) return;

      SetSpinnerRange(
        AzimuthSpinner,
        ctx.Settings.Rotator.MinAzimuth,
        ctx.Settings.Rotator.MaxAzimuth);

      SetSpinnerRange(
        ElevationSpinner,
        ctx.Settings.Rotator.MinElevation,
        ctx.Settings.Rotator.MaxElevation);
    }

    private static void SetSpinnerRange(
      NumericUpDown spinner,
      decimal min,
      decimal max)
    {
      if (max < min) (min, max) = (max, min);

      if (spinner.Minimum == min &&
          spinner.Maximum == max)
        return;

      decimal value =
        Math.Clamp(
          spinner.Value,
          min,
          max);

      spinner.Minimum = min;
      spinner.Maximum = max;
      spinner.Value = value;
    }

    private static void SetSpinnerValue(
      NumericUpDown spinner,
      double? value)
    {
      if (value == null ||
          !double.IsFinite(value.Value))
        return;

      decimal decimalValue = (decimal)value.Value;
      spinner.Value =
        Math.Clamp(
          decimalValue,
          spinner.Minimum,
          spinner.Maximum);
    }

    private static string FormatBearing(Bearing? bearing) =>
      bearing == null
        ? "AZ ---   EL ---"
        : $"AZ {bearing.AzDeg,6:F1}°   EL {bearing.ElDeg,5:F1}°";
  }


  internal sealed class RotatorDirectionWheel : Control
  {
    private readonly System.Windows.Forms.Timer RepeatTimer = new();

    private RotatorJogDirection HotDirection;
    private RotatorJogDirection PressedDirection;

    internal Bearing? ActualBearing { get; set; }
    internal Bearing? TargetBearing { get; set; }
    internal Bearing? SatelliteBearing { get; set; }

    internal event Action<RotatorJogDirection>? JogRequested;
    internal event EventHandler? StopRequested;

    internal RotatorDirectionWheel()
    {
      DoubleBuffered = true;
      Cursor = Cursors.Hand;
      TabStop = false;
      MinimumSize = new Size(150, 150);

      RepeatTimer.Interval = 350;
      RepeatTimer.Tick += RepeatTimer_Tick;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);

      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

      RectangleF bounds =
        new(
          5,
          5,
          Width - 10,
          Height - 10);

      float diameter =
        Math.Min(
          bounds.Width,
          bounds.Height);

      bounds =
        new RectangleF(
          (Width - diameter) / 2f,
          (Height - diameter) / 2f,
          diameter,
          diameter);

      PointF center =
        new(
          bounds.Left + bounds.Width / 2f,
          bounds.Top + bounds.Height / 2f);

      float radius = bounds.Width / 2f;
      float stopRadius = radius * 0.31f;

      using var outerBrush =
        new SolidBrush(
          Enabled
            ? ControlPaint.Light(BackColor, 0.05f)
            : SystemColors.ControlLight);
      using var outlinePen =
        new Pen(
          ControlPaint.Dark(BackColor, 0.35f),
          1.2f);

      e.Graphics.FillEllipse(outerBrush, bounds);
      e.Graphics.DrawEllipse(outlinePen, bounds);

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.ElevationUp,
        "EL+",
        "▲");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.AzimuthUp,
        "AZ+",
        "▶");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.ElevationDown,
        "EL−",
        "▼");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.AzimuthDown,
        "AZ−",
        "◀");

      Color stopColor =
        PressedDirection == RotatorJogDirection.Stop
          ? Color.Firebrick
          : HotDirection == RotatorJogDirection.Stop
            ? Color.IndianRed
            : Color.FromArgb(150, 72, 72);

      using var stopBrush = new SolidBrush(stopColor);
      RectangleF stopBounds =
        new(
          center.X - stopRadius,
          center.Y - stopRadius,
          stopRadius * 2,
          stopRadius * 2);

      e.Graphics.FillEllipse(stopBrush, stopBounds);
      e.Graphics.DrawEllipse(outlinePen, stopBounds);

      using var stopFont =
        new Font(
          "Segoe UI",
          Math.Max(8f, Font.Size),
          FontStyle.Bold);
      TextRenderer.DrawText(
        e.Graphics,
        "STOP",
        stopFont,
        Rectangle.Round(stopBounds),
        Color.White,
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter);

      DrawBearingTicks(
        e.Graphics,
        bounds,
        center,
        radius);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
      base.OnMouseMove(e);

      RotatorJogDirection hit =
        HitTest(e.Location);

      if (hit != HotDirection)
      {
        HotDirection = hit;
        Invalidate();
      }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
      base.OnMouseLeave(e);

      if (!Capture)
      {
        HotDirection = RotatorJogDirection.None;
        Invalidate();
      }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
      base.OnMouseDown(e);

      if (!Enabled ||
          e.Button != MouseButtons.Left)
        return;

      PressedDirection =
        HitTest(e.Location);

      if (PressedDirection == RotatorJogDirection.None)
        return;

      Capture = true;
      Invalidate();

      if (PressedDirection == RotatorJogDirection.Stop)
      {
        StopRequested?.Invoke(this, EventArgs.Empty);
        return;
      }

      JogRequested?.Invoke(
        PressedDirection);

      RepeatTimer.Interval = 350;
      RepeatTimer.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
      base.OnMouseUp(e);

      StopRepeat();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
      base.OnMouseCaptureChanged(e);

      if (!Capture)
        StopRepeat();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        RepeatTimer.Stop();
        RepeatTimer.Dispose();
      }

      base.Dispose(disposing);
    }

    private void RepeatTimer_Tick(
      object? sender,
      EventArgs e)
    {
      if (PressedDirection == RotatorJogDirection.None ||
          PressedDirection == RotatorJogDirection.Stop)
      {
        RepeatTimer.Stop();
        return;
      }

      RepeatTimer.Interval = 130;

      JogRequested?.Invoke(
        PressedDirection);
    }

    private void StopRepeat()
    {
      RepeatTimer.Stop();
      Capture = false;

      if (PressedDirection != RotatorJogDirection.None)
      {
        PressedDirection = RotatorJogDirection.None;
        Invalidate();
      }
    }

    private RotatorJogDirection HitTest(Point point)
    {
      float cx = Width / 2f;
      float cy = Height / 2f;
      float dx = point.X - cx;
      float dy = point.Y - cy;
      float radius = Math.Min(Width, Height) / 2f - 5f;
      float distance =
        MathF.Sqrt(dx * dx + dy * dy);

      if (distance > radius)
        return RotatorJogDirection.None;

      if (distance <= radius * 0.31f)
        return RotatorJogDirection.Stop;

      if (Math.Abs(dx) > Math.Abs(dy))
        return dx >= 0
          ? RotatorJogDirection.AzimuthUp
          : RotatorJogDirection.AzimuthDown;

      return dy <= 0
        ? RotatorJogDirection.ElevationUp
        : RotatorJogDirection.ElevationDown;
    }

    private void DrawDirection(
      Graphics graphics,
      RectangleF wheelBounds,
      PointF center,
      RotatorJogDirection direction,
      string label,
      string arrow)
    {
      float radius = wheelBounds.Width / 2f;
      float x = center.X;
      float y = center.Y;

      RectangleF sector = direction switch
      {
        RotatorJogDirection.ElevationUp =>
          new(
            center.X - radius * 0.28f,
            wheelBounds.Top + radius * 0.12f,
            radius * 0.56f,
            radius * 0.42f),

        RotatorJogDirection.ElevationDown =>
          new(
            center.X - radius * 0.28f,
            wheelBounds.Bottom - radius * 0.54f,
            radius * 0.56f,
            radius * 0.42f),

        RotatorJogDirection.AzimuthDown =>
          new(
            wheelBounds.Left + radius * 0.12f,
            center.Y - radius * 0.28f,
            radius * 0.42f,
            radius * 0.56f),

        _ =>
          new(
            wheelBounds.Right - radius * 0.54f,
            center.Y - radius * 0.28f,
            radius * 0.42f,
            radius * 0.56f)
      };

      bool hot =
        HotDirection == direction;
      bool pressed =
        PressedDirection == direction;

      if (hot || pressed)
      {
        using var fill =
          new SolidBrush(
            Color.FromArgb(
              pressed ? 95 : 55,
              SystemColors.Highlight));

        graphics.FillEllipse(
          fill,
          sector);
      }

      using var arrowFont =
        new Font(
          "Segoe UI Symbol",
          Math.Max(15f, Font.Size + 6f),
          FontStyle.Bold);
      using var labelFont =
        new Font(
          "Segoe UI",
          Math.Max(7.5f, Font.Size - 1f),
          FontStyle.Regular);

      Rectangle arrowRect =
        Rectangle.Round(sector);

      TextRenderer.DrawText(
        graphics,
        arrow,
        arrowFont,
        arrowRect,
        Enabled ? ForeColor : SystemColors.GrayText,
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter);

      Rectangle labelRect =
        direction switch
        {
          RotatorJogDirection.ElevationUp =>
            new(
              (int)sector.Left,
              (int)sector.Bottom - 14,
              (int)sector.Width,
              14),

          RotatorJogDirection.ElevationDown =>
            new(
              (int)sector.Left,
              (int)sector.Top,
              (int)sector.Width,
              14),

          RotatorJogDirection.AzimuthDown =>
            new(
              (int)sector.Left,
              (int)sector.Bottom - 14,
              (int)sector.Width,
              14),

          _ =>
            new(
              (int)sector.Left,
              (int)sector.Bottom - 14,
              (int)sector.Width,
              14)
        };

      TextRenderer.DrawText(
        graphics,
        label,
        labelFont,
        labelRect,
        Enabled ? ForeColor : SystemColors.GrayText,
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter);
    }

    private void DrawBearingTicks(
      Graphics graphics,
      RectangleF wheelBounds,
      PointF center,
      float radius)
    {
      DrawBearingTick(
        graphics,
        center,
        radius,
        SatelliteBearing,
        Color.Gold,
        3f);

      DrawBearingTick(
        graphics,
        center,
        radius,
        TargetBearing,
        Color.DeepSkyBlue,
        4f);

      DrawBearingTick(
        graphics,
        center,
        radius,
        ActualBearing,
        Color.LimeGreen,
        5f);
    }

    private static void DrawBearingTick(
      Graphics graphics,
      PointF center,
      float radius,
      Bearing? bearing,
      Color color,
      float width)
    {
      if (bearing == null)
        return;

      double az = bearing.Az;
      float inner = radius * 0.82f;
      float outer = radius * 0.97f;

      float x1 =
        center.X +
        inner * (float)Math.Sin(az);
      float y1 =
        center.Y -
        inner * (float)Math.Cos(az);
      float x2 =
        center.X +
        outer * (float)Math.Sin(az);
      float y2 =
        center.Y -
        outer * (float)Math.Cos(az);

      using var pen =
        new Pen(
          color,
          width)
        {
          StartCap = LineCap.Round,
          EndCap = LineCap.Round
        };

      graphics.DrawLine(
        pen,
        x1,
        y1,
        x2,
        y2);
    }
  }
}
