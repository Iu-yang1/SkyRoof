using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public partial class TransmittersPanel : DockContent
  {
    private Context ctx;
    private SatnogsDbSatellite Satellite;
    // shared, so we don't leak a GDI font handle per item on every rebuild
    private Font? BoldFont;
    private readonly ContextMenuStrip TransmitterMenu = new();

    public TransmittersPanel()
    {
      InitializeComponent();
    }

    public TransmittersPanel(Context ctx)
    {
      Log.Information("Creating TransmittersPanel");
      this.ctx = ctx;

      InitializeComponent();

      ctx.TransmittersPanel = this;
      ctx.MainForm.TransmittersMNU.Checked = true;
      ctx.Settings.Ui.RestoreColumnWidths("TransmittersPanel", listView1);
      BuildContextMenu();
      SetSatellite();
    }

    internal void SetSatellite(SatnogsDbSatellite? sat = null)
    {
      sat ??= ctx.SatelliteSelector.SelectedSatellite;
      sat.ComputeOrbitDetails();
      Satellite = sat;
      CreateTransmitterItems();

      SatNameLabel.Text = sat.name;
    }

    public void ShowSelectedTransmitter()
    {
      foreach (ListViewItem item in listView1.Items)
        item.ImageIndex = (SatnogsDbTransmitter)item.Tag == ctx.SatelliteSelector.SelectedTransmitter ? 0 : -1;
    }

    private void TransmittersPanel_FormClosing(object sender, FormClosingEventArgs e)
    {
      Log.Information("Closing TransmittersPanel");
      ctx.TransmittersPanel = null;
      ctx.MainForm.TransmittersMNU.Checked = false;
      ctx.Settings.Ui.SaveColumnWidths("TransmittersPanel", listView1);
    }

    private void CreateTransmitterItems()
    {
      BoldFont ??= new Font(listView1.Font, FontStyle.Bold);

      listView1.BeginUpdate();
      listView1.Items.Clear();
      listView1.Groups.Clear();
      listView1.Groups.Add(new ListViewGroup("Local"));
      listView1.Groups.Add(new ListViewGroup("SatNOGS"));
      listView1.Groups.Add(new ListViewGroup("JE9PEL"));

      // satnogs transmitters (pre-sorted: the ListView's own Sorting can't be used with Groups)
      foreach (var tx in Satellite.Transmitters.OrderBy(tx => tx.description))
      {
        // columns
        var item = new ListViewItem([
          tx.description,
          SatnogsDbTransmitter.FormatFrequencyRange(tx.downlink_low, tx.downlink_high, tx.invert),
          SatnogsDbTransmitter.FormatFrequencyRange(tx.uplink_low, tx.uplink_high),
          string.IsNullOrWhiteSpace(tx.DownlinkMode) ? tx.mode : tx.DownlinkMode,
        ]);
        item.Group =
          tx.local_custom
            ? listView1.Groups[0]
            : listView1.Groups[1];
        item.Tag = tx;

        // highlighting
        if (tx.IsVhf()) item.BackColor = Theme.VhfTint;
        if (tx.IsUhf()) item.BackColor = Theme.UhfTint;
        if (tx.service == "Amateur") item.Font = BoldFont;
        item.ForeColor = Theme.RowText(!tx.alive || tx.status != "active"); //item.Font = new(item.Font, FontStyle.Strikeout);

        // tooltip
        item.ToolTipText = tx.GetTooltipText();

        listView1.Items.Add(item);
      }

      // JE9PEL transmitters
      foreach (var t in Satellite.JE9PELtransmitters.OrderBy(t => t.Mode))
      {
        var item = new ListViewItem([t.Name, t.Downlink, t.Uplink, t.Mode]);
        item.Group = listView1.Groups[2];
        item.ToolTipText = t.GetTooltipText();

        // band color
        var match = Regex.Match(t.Downlink, "^[0-9.]+");
        if (match.Success && float.TryParse(match.Groups[0].Value, CultureInfo.InvariantCulture, out float freq))
          if (freq >= 144 && freq <= 148) item.BackColor = Theme.VhfTint;
          else if (freq >= 430 && freq <= 440) item.BackColor = Theme.UhfTint;

        item.ForeColor = Theme.RowText(t.Status != "active");

        listView1.Items.Add(item);
      }

      listView1.EndUpdate();

      ShowSelectedTransmitter();
    }

    private void listView1_SelectedIndexChanged(object sender, EventArgs e)
    {
      if (listView1.SelectedItems.Count == 0) return;

      var tx = listView1.SelectedItems[0].Tag as SatnogsDbTransmitter;
      if (tx == null) return;

      ctx.SatelliteSelector.SetSelectedTransmitter(tx);
    }

    private void BuildContextMenu()
    {
      var add =
        new ToolStripMenuItem(
          "New Transmitter...");
      add.Click += (_, _) =>
        CreateLocalTransmitter();

      TransmitterMenu.Items.Add(
        add);
      listView1.ContextMenuStrip =
        TransmitterMenu;
    }

    private void CreateLocalTransmitter()
    {
      if (Satellite == null)
        return;

      using var dialog =
        new CustomTransmitterDialog(
          Satellite);
      if (dialog.ShowDialog(this) != DialogResult.OK ||
          dialog.Definition == null)
        return;

      try
      {
        SatnogsDbTransmitter tx =
          ctx.SatnogsDb.AddCustomTransmitter(
            Satellite,
            dialog.Definition);

        // Refresh both representations: the selector owns the active radio
        // transmitter, while this panel owns the grouped detail list.
        ctx.SatelliteSelector.RefreshTransmitters(
          tx.uuid);
        CreateTransmitterItems();

        foreach (ListViewItem item in listView1.Items)
          if (ReferenceEquals(item.Tag, tx) ||
              (item.Tag is SatnogsDbTransmitter row &&
               row.uuid == tx.uuid))
          {
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            break;
          }

        ctx.Settings.SaveToFile();
      }
      catch (Exception ex)
      {
        Log.Error(
          ex,
          "Unable to save local transmitter.");

        MessageBox.Show(
          this,
          $"Unable to save the transmitter.\n\n{ex.Message}",
          "New Transmitter",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
    }
  }
}
