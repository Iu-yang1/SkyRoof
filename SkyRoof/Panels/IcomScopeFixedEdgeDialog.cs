namespace SkyRoof
{
  internal sealed class IcomScopeFixedEdgeDialog : Form
  {
    private readonly NumericUpDown LowerBox = new();
    private readonly NumericUpDown UpperBox = new();
    private readonly Button OkBtn = new();
    private readonly Button CancelBtn = new();

    internal long LowerHz =>
      checked(
        (long)Math.Round(
          LowerBox.Value *
          1_000_000m,
          MidpointRounding.AwayFromZero));

    internal long UpperHz =>
      checked(
        (long)Math.Round(
          UpperBox.Value *
          1_000_000m,
          MidpointRounding.AwayFromZero));

    internal IcomScopeFixedEdgeDialog(
      int frequencyRange,
      int edgeNumber,
      long lowerHz,
      long upperHz)
    {
      Text =
        $"Edit IC-9700 Fixed Edge {edgeNumber}";
      FormBorderStyle =
        FormBorderStyle.FixedDialog;
      StartPosition =
        FormStartPosition.CenterParent;
      MaximizeBox = false;
      MinimizeBox = false;
      ShowInTaskbar = false;
      AutoSize = true;
      AutoSizeMode =
        AutoSizeMode.GrowAndShrink;
      Padding = new Padding(10);

      (decimal minimumMHz,
       decimal maximumMHz,
       string bandLabel) =
        frequencyRange switch
        {
          1 =>
            (144m, 148m, "144 MHz"),
          2 =>
            (430m, 450m, "430 MHz"),
          3 =>
            (1240m, 1300m, "1200 MHz"),
          _ =>
            throw new ArgumentOutOfRangeException(
              nameof(frequencyRange))
        };

      var layout =
        new TableLayoutPanel
        {
          AutoSize = true,
          AutoSizeMode =
            AutoSizeMode.GrowAndShrink,
          ColumnCount = 2,
          RowCount = 4,
          Dock = DockStyle.Fill
        };

      layout.ColumnStyles.Add(
        new ColumnStyle(
          SizeType.AutoSize));
      layout.ColumnStyles.Add(
        new ColumnStyle(
          SizeType.AutoSize));

      var info =
        new Label
        {
          AutoSize = true,
          Text =
            $"{bandLabel} · Edge {edgeNumber} · 1 kHz resolution",
          Margin =
            new Padding(3, 3, 3, 10)
        };

      layout.Controls.Add(
        info,
        0,
        0);
      layout.SetColumnSpan(
        info,
        2);

      ConfigureFrequencyBox(
        LowerBox,
        minimumMHz,
        maximumMHz,
        lowerHz);

      ConfigureFrequencyBox(
        UpperBox,
        minimumMHz,
        maximumMHz,
        upperHz);

      layout.Controls.Add(
        MakeLabel(
          "Lower edge (MHz):"),
        0,
        1);
      layout.Controls.Add(
        LowerBox,
        1,
        1);

      layout.Controls.Add(
        MakeLabel(
          "Upper edge (MHz):"),
        0,
        2);
      layout.Controls.Add(
        UpperBox,
        1,
        2);

      var buttons =
        new FlowLayoutPanel
        {
          AutoSize = true,
          FlowDirection =
            FlowDirection.RightToLeft,
          Dock = DockStyle.Fill,
          Margin =
            new Padding(0, 10, 0, 0)
        };

      OkBtn.Text = "OK";
      OkBtn.AutoSize = true;
      OkBtn.DialogResult =
        DialogResult.None;
      OkBtn.Click +=
        (_, _) =>
        {
          if (UpperBox.Value <=
              LowerBox.Value)
          {
            MessageBox.Show(
              this,
              "Upper edge must be greater than lower edge.",
              "Invalid fixed edge",
              MessageBoxButtons.OK,
              MessageBoxIcon.Warning);
            return;
          }

          DialogResult =
            DialogResult.OK;
          Close();
        };

      CancelBtn.Text = "Cancel";
      CancelBtn.AutoSize = true;
      CancelBtn.DialogResult =
        DialogResult.Cancel;

      buttons.Controls.Add(
        CancelBtn);
      buttons.Controls.Add(
        OkBtn);

      layout.Controls.Add(
        buttons,
        0,
        3);
      layout.SetColumnSpan(
        buttons,
        2);

      Controls.Add(
        layout);

      AcceptButton =
        OkBtn;
      CancelButton =
        CancelBtn;
    }

    private static Label MakeLabel(
      string text) =>
      new()
      {
        AutoSize = true,
        Text = text,
        Margin =
          new Padding(3, 7, 8, 3)
      };

    private static void ConfigureFrequencyBox(
      NumericUpDown box,
      decimal minimumMHz,
      decimal maximumMHz,
      long valueHz)
    {
      box.DecimalPlaces = 3;
      box.Increment = 0.001m;
      box.Minimum = minimumMHz;
      box.Maximum = maximumMHz;
      box.Width = 110;
      box.ThousandsSeparator = false;

      decimal valueMHz =
        valueHz /
        1_000_000m;

      box.Value =
        Math.Clamp(
          valueMHz,
          minimumMHz,
          maximumMHz);
    }
  }
}
