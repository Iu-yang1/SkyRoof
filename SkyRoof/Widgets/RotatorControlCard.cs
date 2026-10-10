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
  // Direction sectors use Hamlib continuous move while held and STOP on
  // release, matching hardware/cloud-satellite direction-pad semantics.
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
    private readonly Label ManualHintLabel = new();
    private readonly ToolTip UiToolTip = new();
    private readonly System.Windows.Forms.Timer RefreshTimer = new();

    private Context? ctx;
    private RotatorWidget? rotator;
    private bool updatingStepUi;

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
      ActualLabel.ForeColor = Color.LimeGreen;
      ActualLabel.Location = new Point(12, 38);
      ActualLabel.Size = new Size(292, 20);
      Controls.Add(ActualLabel);

      TargetLabel.AutoSize = false;
      TargetLabel.ForeColor = Color.DeepSkyBlue;
      TargetLabel.Location = new Point(12, 58);
      TargetLabel.Size = new Size(292, 20);
      Controls.Add(TargetLabel);

      SatelliteLabel.AutoSize = false;
      SatelliteLabel.ForeColor = Color.Goldenrod;
      SatelliteLabel.Location = new Point(12, 78);
      SatelliteLabel.Size = new Size(292, 20);
      Controls.Add(SatelliteLabel);

      Wheel.Location = new Point(18, 104);
      Wheel.Size = new Size(202, 202);
      Wheel.MoveStarted +=
        direction =>
          rotator?.BeginManualContinuousMove(
            direction);
      Wheel.MoveStopped +=
        (_, _) =>
          rotator?.EndManualContinuousMove();
      Wheel.StopRequested +=
        (_, _) =>
          rotator?.StopRotation();
      Controls.Add(Wheel);

      var stepLabel = new Label
      {
        AutoSize = false,
        Location = new Point(232, 112),
        Size = new Size(72, 19),
        Text = "Track step",
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
      StepSpinner.ValueChanged +=
        StepSpinner_ValueChanged;
      Controls.Add(StepSpinner);
      UiToolTip.SetToolTip(
        StepSpinner,
        "Automatic satellite-tracking step size. This changes the active tracking path immediately; manual hold movement is continuous.");

      ManualHintLabel.AutoSize = false;
      ManualHintLabel.ForeColor = SystemColors.GrayText;
      ManualHintLabel.Location = new Point(228, 166);
      ManualHintLabel.Size = new Size(80, 76);
      ManualHintLabel.Text = "Hold = move\nRelease = STOP\n\nCenter = STOP";
      ManualHintLabel.TextAlign = ContentAlignment.TopLeft;
      Controls.Add(ManualHintLabel);

      TrackButton.Location = new Point(232, 247);
      TrackButton.Size = new Size(72, 26);
      TrackButton.Text = "TRACK";
      TrackButton.Click += (_, _) => rotator?.ToggleTracking();
      Controls.Add(TrackButton);

      ParkButton.Location = new Point(232, 280);
      ParkButton.Size = new Size(72, 26);
      ParkButton.Text = "PARK";
      ParkButton.MouseUp += ParkButton_MouseUp;
      Controls.Add(ParkButton);

      UiToolTip.SetToolTip(
        ParkButton,
        "Left-click: follow ordered PARK route\nRight-click: edit PARK waypoints");

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
      updatingStepUi = true;
      try
      {
        StepSpinner.Value =
          Math.Clamp(
            (decimal)sett.StepSize,
            StepSpinner.Minimum,
            StepSpinner.Maximum);
      }
      finally
      {
        updatingStepUi = false;
      }

      UpdateSpinnerLimits();
      RefreshState();
    }

    internal void RefreshState()
    {
      if (ctx == null || rotator == null) return;

      UpdateSpinnerLimits();

      if (!StepSpinner.Focused)
      {
        updatingStepUi = true;
        try
        {
          StepSpinner.Value =
            Math.Clamp(
              (decimal)ctx.Settings.Rotator.StepSize,
              StepSpinner.Minimum,
              StepSpinner.Maximum);
        }
        finally
        {
          updatingStepUi = false;
        }
      }

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
        rotator.IsManualControlLocked ? "Live Track" :
        connected ? "Connected" :
        "Connecting";

      ConnectionLabel.ForeColor =
        !enabled ? SystemColors.GrayText :
        rotator.IsManualControlLocked ? Color.Goldenrod :
        connected ? Color.LimeGreen :
        Color.IndianRed;

      bool manualLocked =
        rotator.IsManualControlLocked;
      bool manualEnabled =
        enabled &&
        !manualLocked;

      Wheel.Enabled = manualEnabled;
      // Track step is a tracking parameter, not a manual-jog increment, so it
      // remains adjustable while live tracking is active.
      StepSpinner.Enabled = enabled;
      AzimuthSpinner.Enabled = manualEnabled;
      ElevationSpinner.Enabled = manualEnabled;
      GoButton.Enabled = manualEnabled;
      // PARK stays clickable while live tracking is locked so right-click can
      // edit the saved preset. ManualPark itself still rejects movement while
      // the live-pass lock is active.
      ParkButton.Enabled = enabled;
      ParkButton.Text = rotator.ParkProgressText;

      // TRACK remains available while manual controls are locked so the
      // operator can intentionally leave live-pass tracking. As soon as Track
      // is cleared the wheel becomes available on the next refresh tick.
      TrackButton.Enabled =
        enabled &&
        rotator.TrackCheckbox.Enabled;

      TrackButton.Text =
        rotator.IsTracking
          ? "TRACK ✓"
          : "TRACK";

      ManualHintLabel.Text =
        rotator.IsParking
          ? "PARK ACTIVE\n\nSTOP cancels\nthe route."
          : manualLocked
          ? "LIVE TRACK\n\nManual locked.\nClear TRACK first."
          : rotator.IsTracking
            ? "PRE-POSITION\n\nManual input will\nclear TRACK first."
            : "Hold = move\nRelease = STOP\n\nCenter = STOP";

      ManualHintLabel.ForeColor =
        manualLocked
          ? Color.IndianRed
          : SystemColors.GrayText;

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
        UiToolTip.Dispose();
        Wheel.Dispose();
      }

      base.Dispose(disposing);
    }

    private void ParkButton_MouseUp(
      object? sender,
      MouseEventArgs e)
    {
      if (rotator == null ||
          ctx == null)
        return;

      if (e.Button == MouseButtons.Left)
      {
        rotator.ManualPark();
        RefreshState();
        return;
      }

      if (e.Button != MouseButtons.Right)
        return;

      using var dialog =
        new ParkPositionDialog(
          ctx,
          rotator.GetManualActualBearing());

      // A modal owner is required for CenterParent to center on the
      // application rather than the mouse position or a temporary popup.
      dialog.ShowDialog(ctx.MainForm);
      RefreshState();
    }

    private void StepSpinner_ValueChanged(
      object? sender,
      EventArgs e)
    {
      if (updatingStepUi ||
          rotator == null)
        return;

      rotator.SetTrackingStepSize(
        (double)StepSpinner.Value);
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
    private RotatorJogDirection HotDirection;
    private RotatorJogDirection PressedDirection;

    internal Bearing? ActualBearing;
    internal Bearing? TargetBearing;
    internal Bearing? SatelliteBearing;

    internal event Action<RotatorJogDirection>? MoveStarted;
    internal event EventHandler? MoveStopped;
    internal event EventHandler? StopRequested;

    internal RotatorDirectionWheel()
    {
      DoubleBuffered = true;
      Cursor = Cursors.Hand;
      TabStop = false;
      MinimumSize = new Size(150, 150);
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
        "▲");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.AzimuthUp,
        "▶");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.ElevationDown,
        "▼");

      DrawDirection(
        e.Graphics,
        bounds,
        center,
        RotatorJogDirection.AzimuthDown,
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

      // Continuous-motion protocols are edge triggered: press starts the
      // direction and the hardware keeps moving until release sends STOP.
      MoveStarted?.Invoke(
        PressedDirection);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
      base.OnMouseUp(e);

      FinishPress();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
      base.OnMouseCaptureChanged(e);

      if (!Capture)
        FinishPress();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing &&
          PressedDirection != RotatorJogDirection.None &&
          PressedDirection != RotatorJogDirection.Stop)
      {
        MoveStopped?.Invoke(
          this,
          EventArgs.Empty);
      }

      base.Dispose(disposing);
    }

    private void FinishPress()
    {
      RotatorJogDirection released =
        PressedDirection;

      PressedDirection =
        RotatorJogDirection.None;

      if (Capture)
        Capture = false;

      if (released != RotatorJogDirection.None)
        Invalidate();

      if (released != RotatorJogDirection.None &&
          released != RotatorJogDirection.Stop)
      {
        MoveStopped?.Invoke(
          this,
          EventArgs.Empty);
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
      string arrow)
    {
      float radius = wheelBounds.Width / 2f;

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
          Math.Max(20f, Font.Size + 10f),
          FontStyle.Bold);

      TextRenderer.DrawText(
        graphics,
        arrow,
        arrowFont,
        Rectangle.Round(sector),
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
