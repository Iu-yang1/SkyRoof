using System.ComponentModel;
using Serilog;

namespace SkyRoof
{
  public partial class DownloadDialog : Form
  {
    private enum DownloadMode
    {
      SatelliteData,
      JplKernel
    }

    private Context ctx = null!;
    private SatnogsDb? db;
    private DownloadMode Mode;
    private JplEphemerisKernel JplKernel;
    private CancellationTokenSource? JplCancellation;

    internal string? DownloadedJplPath { get; private set; }

    public DownloadDialog()
    {
      InitializeComponent();

      // The unfilled part of the bar paints the control's own BackColor, which inherits the form
      // and so is invisible against it in the dark theme: a bar at 0% shows nothing at all.
      // Unconditional - under visual styles the light theme draws its own trough and ignores
      // BackColor, so this changes nothing there.
      progressBar1.BackColor = SystemColors.ControlDark;

      // Keep the form open until the async operation has actually stopped.
      // This avoids disposing controls while a cancellation is still unwinding.
      Button.DialogResult = DialogResult.None;
    }

    public static bool Download(Form parent, Context ctx)
    {
      using var dlg = new DownloadDialog
      {
        ctx = ctx,
        Mode = DownloadMode.SatelliteData
      };

      var rc = dlg.ShowDialog(parent);
      return rc == DialogResult.OK;
    }

    internal static bool DownloadJpl(
      Form parent,
      Context ctx,
      JplEphemerisKernel kernel,
      out string? path)
    {
      string name =
        kernel == JplEphemerisKernel.DE421
          ? "DE421"
          : "DE440s";

      using var dlg = new DownloadDialog
      {
        ctx = ctx,
        Mode = DownloadMode.JplKernel,
        JplKernel = kernel,
        Text = $"JPL {name}",
      };

      dlg.label1.Text =
        $"Downloading JPL {name} ephemeris...";
      dlg.ErrorLabel.ForeColor =
        SystemColors.ControlText;
      dlg.ErrorLabel.Text =
        "Connecting...";

      var rc = dlg.ShowDialog(parent);
      path = dlg.DownloadedJplPath;
      return rc == DialogResult.OK;
    }

    private void Button_Click(object sender, EventArgs e)
    {
      if (Button.Text == "Close")
      {
        DialogResult = DialogResult.Cancel;
        Close();
        return;
      }

      Button.Enabled = false;
      Button.Text = "Cancelling...";

      if (Mode == DownloadMode.JplKernel)
        JplCancellation?.Cancel();
      else
        db?.AbortDownload();
    }

    private async void DownloadDialog_Shown(
      object sender,
      EventArgs e)
    {
      if (Mode == DownloadMode.JplKernel)
      {
        await DownloadJplKernelAsync();
        return;
      }

      await DownloadSatelliteDataAsync();
    }

    private async Task DownloadSatelliteDataAsync()
    {
      db = new();
      db.ConfigureSources(ctx.Settings.OrbitSources);
      db.DownloadProgress += SatnogsDb_DownloadProgress;

      // download files
      try
      {
        await db.DownloadAll();
      }
      catch (OperationCanceledException)
      {
        DialogResult = DialogResult.Cancel;
        return;
      }
      catch (Exception ex)
      {
        ErrorLabel.Text = "Download Failed";
        Button.Text = "Close";
        Log.Error(ex, ErrorLabel.Text);
        db = null;
        DialogResult = DialogResult.None;
        return;
      }

      // import files
      try
      {
        db.ImportAll();

        // Preserve any still-valid manual file override across a full SatNOGS
        // database rebuild. Automatic sources are then refreshed underneath it.
        db.CopyActiveManualOrbitOverridesFrom(
          ctx.SatnogsDb,
          DateTime.UtcNow);

        await db.LoadAutomaticOrbitOverlaysAsync();

        db.ConfigureSolarSystem(ctx.Settings.OrbitSources);
        ctx.Settings.Satellites.EnsureSolarSystemGroup(db);
        ctx.SatnogsDb.ReplaceSatelliteList(db);
        DialogResult = DialogResult.OK;
      }
      catch (Exception ex)
      {
        ErrorLabel.Text = "Data Import Failed";
        Button.Text = "Close";
        Log.Error(ex, ErrorLabel.Text);
      }
    }

    private async Task DownloadJplKernelAsync()
    {
      db = ctx.SatnogsDb;
      JplCancellation = new CancellationTokenSource();

      var progress =
        new Progress<JplDownloadProgress>(
          ShowJplDownloadProgress);

      try
      {
        DownloadedJplPath =
          await db.DownloadJplKernelAsync(
            JplKernel,
            progress,
            JplCancellation.Token);

        progressBar1.Style = ProgressBarStyle.Blocks;
        progressBar1.Value = 100;
        ErrorLabel.Text = "Download complete. Validated SPK/BSP file.";
        DialogResult = DialogResult.OK;
      }
      catch (OperationCanceledException)
      {
        Log.Information(
          $"JPL {JplKernel} download cancelled by user.");
        DialogResult = DialogResult.Cancel;
      }
      catch (Exception ex)
      {
        progressBar1.Style = ProgressBarStyle.Blocks;
        progressBar1.Value = 0;
        ErrorLabel.ForeColor = Color.Red;
        ErrorLabel.Text = "Download Failed";
        Button.Text = "Close";
        Log.Error(
          ex,
          $"JPL {JplKernel} download failed.");
        MessageBox.Show(
          this,
          ex.Message,
          "JPL Ephemeris",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
        DialogResult = DialogResult.None;
      }
      finally
      {
        JplCancellation.Dispose();
        JplCancellation = null;
      }
    }

    private void ShowJplDownloadProgress(
      JplDownloadProgress progress)
    {
      string name =
        JplKernel == JplEphemerisKernel.DE421
          ? "DE421"
          : "DE440s";

      string source =
        Uri.TryCreate(
          progress.SourceUrl,
          UriKind.Absolute,
          out Uri? uri)
          ? uri.Host
          : progress.SourceUrl;

      label1.Text =
        $"Downloading JPL {name} — source {progress.SourceIndex}/{progress.SourceCount}";

      double receivedMiB =
        progress.BytesReceived /
        1024d /
        1024d;

      if (progress.Percent is int percent &&
          progress.TotalBytes is long totalBytes)
      {
        progressBar1.Style = ProgressBarStyle.Blocks;
        progressBar1.Value =
          Math.Clamp(
            percent,
            progressBar1.Minimum,
            progressBar1.Maximum);

        double totalMiB =
          totalBytes /
          1024d /
          1024d;

        ErrorLabel.Text =
          $"{receivedMiB:F1} / {totalMiB:F1} MiB  ({percent}%)  {source}";
      }
      else
      {
        progressBar1.Style = ProgressBarStyle.Marquee;
        ErrorLabel.Text =
          $"{receivedMiB:F1} MiB received  {source}";
      }
    }

    private void SatnogsDb_DownloadProgress(
      object? sender,
      ProgressChangedEventArgs e)
    {
      progressBar1.Value = e.ProgressPercentage;
    }
  }
}
