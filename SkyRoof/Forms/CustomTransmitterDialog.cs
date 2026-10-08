using System.Globalization;
using VE3NEA;

namespace SkyRoof
{
  internal sealed class CustomTransmitterDialog : Form
  {
    private readonly TextBox NameBox = new();
    private readonly TextBox DownlinkBox = new();
    private readonly TextBox UplinkBox = new();
    private readonly ComboBox ModeBox = new();
    private readonly Button SaveButton = new();
    private readonly Button CancelBtn = new();

    internal CustomTransmitterDefinition? Definition { get; private set; }

    internal CustomTransmitterDialog(
      SatnogsDbSatellite satellite)
    {
      Text = $"New Transmitter — {satellite.name}";
      FormBorderStyle = FormBorderStyle.FixedToolWindow;
      StartPosition = FormStartPosition.Manual;
      ShowInTaskbar = false;
      MinimizeBox = false;
      MaximizeBox = false;
      ClientSize = new Size(356, 222);
      AutoScaleMode = AutoScaleMode.Font;

      var info = new Label
      {
        AutoSize = false,
        Location = new Point(12, 10),
        Size = new Size(332, 32),
        Text = "Create a local transmitter. Leave either frequency blank when only one direction exists.",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(info);

      AddLabel("Name", 12, 52, 96);
      NameBox.Location = new Point(116, 50);
      NameBox.Size = new Size(228, 23);
      NameBox.PlaceholderText = "e.g. Linear transponder / Beacon";
      Controls.Add(NameBox);

      AddLabel("Downlink (MHz)", 12, 84, 96);
      DownlinkBox.Location = new Point(116, 82);
      DownlinkBox.Size = new Size(110, 23);
      DownlinkBox.PlaceholderText = "optional";
      Controls.Add(DownlinkBox);

      AddLabel("Uplink (MHz)", 12, 116, 96);
      UplinkBox.Location = new Point(116, 114);
      UplinkBox.Size = new Size(110, 23);
      UplinkBox.PlaceholderText = "optional";
      Controls.Add(UplinkBox);

      AddLabel("Mode", 12, 148, 96);
      ModeBox.Location = new Point(116, 146);
      ModeBox.Size = new Size(110, 23);
      ModeBox.DropDownStyle = ComboBoxStyle.DropDown;
      ModeBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
      ModeBox.AutoCompleteSource = AutoCompleteSource.ListItems;
      ModeBox.Items.AddRange(
        new object[]
        {
          "FM",
          "FMN",
          "USB",
          "LSB",
          "CW",
          "AFSK",
          "FSK",
          "GFSK",
          "GMSK",
          "BPSK",
          "QPSK",
          "FT4",
          "FT8",
          "SSTV"
        });
      ModeBox.Text = "FM";
      Controls.Add(ModeBox);

      var unitsHint = new Label
      {
        AutoSize = false,
        ForeColor = SystemColors.GrayText,
        Location = new Point(236, 82),
        Size = new Size(108, 55),
        Text = "Examples:\n145.900000\n435.250000",
        TextAlign = ContentAlignment.TopLeft
      };
      Controls.Add(unitsHint);

      SaveButton.Location = new Point(185, 184);
      SaveButton.Size = new Size(75, 28);
      SaveButton.Text = "Save";
      SaveButton.Click += SaveButton_Click;
      Controls.Add(SaveButton);

      CancelBtn.Location = new Point(269, 184);
      CancelBtn.Size = new Size(75, 28);
      CancelBtn.Text = "Cancel";
      CancelBtn.DialogResult = DialogResult.Cancel;
      Controls.Add(CancelBtn);

      AcceptButton = SaveButton;
      CancelButton = CancelBtn;

      Location = Cursor.Position;
      Shown += (_, _) =>
      {
        Utils.EnsureFormVisible(this);
        NameBox.Focus();
      };
    }

    private void AddLabel(
      string text,
      int x,
      int y,
      int width)
    {
      Controls.Add(
        new Label
        {
          AutoSize = false,
          Location = new Point(x, y),
          Size = new Size(width, 23),
          Text = text,
          TextAlign = ContentAlignment.MiddleLeft
        });
    }

    private void SaveButton_Click(
      object? sender,
      EventArgs e)
    {
      string name =
        NameBox.Text.Trim();
      if (name.Length == 0)
      {
        MessageBox.Show(
          this,
          "Enter a transmitter name.",
          "New Transmitter",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
        NameBox.Focus();
        return;
      }

      if (!TryParseFrequency(
            DownlinkBox.Text,
            out long? downlink) ||
          !TryParseFrequency(
            UplinkBox.Text,
            out long? uplink))
      {
        MessageBox.Show(
          this,
          "Enter frequencies in MHz, for example 145.900000 or 435.250000.\nLeave a field blank when that direction is not used.",
          "New Transmitter",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
        return;
      }

      if (downlink == null &&
          uplink == null)
      {
        MessageBox.Show(
          this,
          "Enter at least one frequency: downlink, uplink, or both.",
          "New Transmitter",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
        return;
      }

      string mode =
        ModeBox.Text.Trim();
      if (mode.Length == 0)
        mode = "FM";

      Definition =
        new CustomTransmitterDefinition
        {
          description = name,
          downlink_hz = downlink,
          uplink_hz = uplink,
          mode = mode
        };

      DialogResult = DialogResult.OK;
      Close();
    }

    internal static bool TryParseFrequency(
      string? text,
      out long? frequencyHz)
    {
      frequencyHz = null;
      if (string.IsNullOrWhiteSpace(text))
        return true;

      string value = text.Trim();
      bool parsed =
        decimal.TryParse(
          value,
          NumberStyles.Float,
          CultureInfo.CurrentCulture,
          out decimal mhz) ||
        decimal.TryParse(
          value,
          NumberStyles.Float,
          CultureInfo.InvariantCulture,
          out mhz);

      if (!parsed ||
          mhz <= 0 ||
          mhz > 100000)
        return false;

      decimal hz =
        decimal.Round(
          mhz * 1_000_000M,
          0,
          MidpointRounding.AwayFromZero);

      if (hz > long.MaxValue)
        return false;

      frequencyHz = (long)hz;
      return true;
    }
  }
}
