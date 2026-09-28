using VE3NEA;
using Newtonsoft.Json;
using Serilog;
using System.ComponentModel;
using System.Text;

namespace SkyRoof
{
  // outcome of reading the settings file, reported to the user by MainForm.ReportSettingsLoadResult
  public enum SettingsLoadResult { Loaded, RestoredFromBackup, Reset }

  public class Settings
  {
    public UiSettings Ui = new();
    public SatelliteSettings Satellites = new();
    public SdrSettings Sdr = new();
    public LatestVersionInfo LatestVersion = new();

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public UserSettings User { get; set; } = new();


    [TypeConverter(typeof(ExpandableObjectConverter))]
    public AudioSettings Audio { get; set; } = new();

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public OutputStreamSettings OutputStream { get; set; } = new();

    [DisplayName("Telemetry")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public TelemetrySettings Telemetry { get; set; } = new();

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public AnnouncerSettings Announcements { get; set; } = new();


    [DisplayName("Waterfall")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public WaterfallSettings Waterfall { get; set; } = new();

    [DisplayName("CAT Control")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public CatSettings Cat { get; set; } = new();

    [DisplayName("Icom LAN Spectrum")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public IcomLanSpectrumSettings IcomLanSpectrum { get; set; } = new();

    [DisplayName("Rotator Control")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public RotatorSettings Rotator { get; set; } = new();

    [Description("Use SDR on a remote computer via the SoapyRemote protocol")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public SoapyRemoteSettings SoapyRemote { get; set; } = new();

    [DisplayName("Amsat Satellite Status")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public AmsatSettings Amsat { get; set; } = new();

    [DisplayName("QSO Entry")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public QsoEntrySettings QsoEntry { get; set; } = new();


    [DisplayName("FT4 Console")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public Ft4ConsoleSettings Ft4Console { get; set; } = new ();


    [DisplayName("Transverter")]
    [Description("Settings for HF rigs and SDRs connected via a VHF/UHF transverter")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public TransverterSettings Transverter { get; set; } = new();


    // where ReadSettingsFile kept the damaged settings file, for the message that MainForm shows
    internal string? DamagedFileName { get; private set; }

    // internal: Theme.Initialize reads the theme setting from this file before the settings are loaded
    internal static string GetFileName()
    {
      return Path.Combine(Utils.GetUserDataFolder(), "Settings.json");
    }

    // the generation of the settings file that SaveToFile replaced, read when the current one is damaged
    private static string GetBackupFileName()
    {
      return GetFileName() + ".bak";
    }

    public SettingsLoadResult LoadFromFile()
    {
      var result = ReadSettingsFile();
      SetDefaults();
      return result;
    }

    // A settings file damaged by an unclean shutdown must not keep SkyRoof from starting: fall back to the
    // backup that SaveToFile left behind, and to the built-in defaults if that one is damaged too. The
    // defaults leave User.Square empty, so MainForm.EnsureUserDetails asks for the required fields exactly
    // as it does on a first run.
    private SettingsLoadResult ReadSettingsFile()
    {
      string fileName = GetFileName();
      if (!File.Exists(fileName)) return SettingsLoadResult.Loaded; // first run

      var error = TryReadFrom(fileName);
      if (error == null) return SettingsLoadResult.Loaded;
      Log.Error(error, "Failed to read the settings file");
      DamagedFileName = SetDamagedFileAside(fileName);

      string backupFileName = GetBackupFileName();
      if (!File.Exists(backupFileName)) return SettingsLoadResult.Reset;

      var backupError = TryReadFrom(backupFileName);
      if (backupError == null) return SettingsLoadResult.RestoredFromBackup;

      Log.Error(backupError, "Failed to read the backup settings file");
      return SettingsLoadResult.Reset;
    }

    // Read into a throwaway object first: PopulateObject applies each member as it reads it, so a file that
    // fails halfway would leave this instance half-populated, with no way back to the defaults. The second
    // call reads the same text into the same type and cannot fail where the first one succeeded.
    private Exception? TryReadFrom(string fileName)
    {
      try
      {
        string json = File.ReadAllText(fileName);
        JsonConvert.PopulateObject(json, new Settings());
        JsonConvert.PopulateObject(json, this);
        return null;
      }
      catch (Exception ex)
      {
        return ex;
      }
    }

    // keep the damaged file for a post-mortem, and out of the way of the next start
    private static string? SetDamagedFileAside(string fileName)
    {
      try
      {
        string damagedFileName = Path.ChangeExtension(fileName, $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(fileName, damagedFileName, true);
        return damagedFileName;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to set the damaged settings file aside");
        return null;
      }
    }

    // Write a temporary file, force it to the disk, and swap it in. File.Replace is atomic at the directory
    // level, so a power loss leaves either the old settings file or the new one, never a half-written or a
    // zero-filled one, and it keeps the file it replaces as the backup that ReadSettingsFile falls back to.
    public void SaveToFile()
    {
      Satellites.AutoSelection.SyncToGroups(Satellites);
      string json = JsonConvert.SerializeObject(this, Formatting.Indented);

      string fileName = GetFileName();
      string tempFileName = fileName + ".tmp";

      using (var stream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None))
      using (var writer = new StreamWriter(stream))
      {
        writer.Write(json);
        writer.Flush();
        stream.Flush(true);
      }

      if (File.Exists(fileName))
        File.Replace(tempFileName, fileName, GetBackupFileName(), true);
      else
        File.Move(tempFileName, fileName);
    }

    private void SetDefaults()
    {
      if (Ui.DockingLayoutString == null)
        Ui.DockingLayoutString = Ui.DefaultDockingString;

      Satellites.Sanitize(true);
      Satellites.AutoSelection.SyncToGroups(Satellites);
      Transverter.SetDefaults();
    }
  }
}