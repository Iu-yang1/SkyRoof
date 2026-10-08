using System.ComponentModel;
using System.DirectoryServices.ActiveDirectory;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Serilog;
using SGPdotNET.TLE;
using SGPdotNET.Parsers;
using SkyRoof.Satellites;
using VE3NEA;
using VE3NEA.SkyTlm.Core;   // SignalParams / Framing, for the save-to-overrides writer (§6.1)

namespace SkyRoof
{
  internal readonly record struct JplDownloadProgress(
    int SourceIndex,
    int SourceCount,
    string SourceUrl,
    long BytesReceived,
    long? TotalBytes)
  {
    internal int? Percent =>
      TotalBytes is > 0
        ? (int)Math.Clamp(
            BytesReceived * 100L / TotalBytes.Value,
            0,
            100)
        : null;
  }

  public class SatnogsDb
  {
    internal const string MoonSatId = "MOON";
    internal const string SunSatId = "SUN";
    internal const string VenusSatId = "VENUS";

    private readonly string DataFolder, DownloadsFolder;
    private JplSpkKernel? PlanetaryKernel;
    private Dictionary<string, SatnogsDbSatellite> SatelliteList = new();
    private readonly HttpClient DownloadHttpClient = new();
    private OrbitSourceSettings OrbitSources = new();
    private CancellationTokenSource cts;
    private JsonSerializerSettings JsonSettings = new();

    internal static readonly TimeSpan ManualOrbitPriorityLifetime = TimeSpan.FromDays(3);

    public IEnumerable<SatnogsDbSatellite> Satellites { get => SatelliteList.Values; }
    public bool Loaded { get => loaded; }
    private bool loaded;

    public event EventHandler<ProgressChangedEventArgs>? DownloadProgress;
    public event EventHandler? TleUpdated;
    public event EventHandler? ListUpdated;


    public SatnogsDb()
    {
      DataFolder = Utils.GetUserDataFolder();
      DownloadsFolder = Path.Combine(DataFolder, "Downloads");
      Directory.CreateDirectory(DownloadsFolder);

      // merge the embedded default transmitter overrides into the user-editable file, keeping user edits
      string txOverrideFile = Path.Combine(DataFolder, "transmitters-override.json");
      MergeOverrideFile(txOverrideFile);

      JsonSettings.Converters.Add(new IsoDateTimeConverter { DateTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ssK" });

      // GitHub's codeload host is happier with an explicit User-Agent. Large
      // JPL kernels can also take longer than HttpClient's 100-second default
      // timeout on slow links, so allow a realistic transfer window.
      DownloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SkyRoof");
      DownloadHttpClient.Timeout = TimeSpan.FromMinutes(15);
    }

    internal void ConfigureSources(OrbitSourceSettings settings)
    {
      OrbitSources = settings ?? new OrbitSourceSettings();
    }

    public void LoadFromFile()
    {
      SatelliteList.Clear();

      try
      {
        loaded = false;
        string path = Path.Combine(DataFolder, "Satellites.json");
        if (!File.Exists(path)) return;

        string json = File.ReadAllText(path);
        var satellites = JsonConvert.DeserializeObject<SatnogsDbSatelliteList>(json);
        SatelliteList = satellites.ToDictionary(s => s.sat_id);

        bool orbitLayersChanged = false;
        DateTime now = DateTime.UtcNow;
        foreach (var sat in satellites)
        {
          foreach (var tx in sat.Transmitters)
            tx.Satellite = sat;

          // Migrate pre-layered Satellites.json files and release any manual
          // file override whose 72-hour priority window expired while SkyRoof
          // was not running.
          orbitLayersChanged |= sat.InitializeOrbitLayers(now);
        }

        // the override file is live configuration, not a build-time artifact: apply it on every load so a
        // record saved from the Signal Params dialog takes effect at the next start without a database
        // import (discover_params_plan.md §6.2). ApplyTransmitterOverrides is idempotent, so re-applying
        // over params already carrying a manual layer from Satellites.json changes nothing.
        ApplyTransmitterOverrides();

        loaded = SatelliteList.Count > 0;

        if (orbitLayersChanged)
          SaveToFile();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load satellite list");
        SatelliteList.Clear();
      }
    }

    public void SaveToFile()
    {
      string path = Path.Combine(DataFolder, "Satellites.json");

      // Solar-system ephemeris targets are synthetic objects rebuilt from the
      // configured JPL SPK kernel at startup. Keep the SatNOGS/cache file free
      // of transient tracker instances and kernel-specific metadata.
      File.WriteAllText(
        path,
        JsonConvert.SerializeObject(
          Satellites.Where(s => !s.IsEphemerisTarget)));
    }

    internal string GetJplKernelPath(
      OrbitSourceSettings settings)
    {
      if (settings.JplKernel == JplEphemerisKernel.CustomFile)
        return Environment.ExpandEnvironmentVariables(
          settings.JplKernelFile ?? string.Empty);

      string fileName =
        settings.JplKernel == JplEphemerisKernel.DE421
          ? "de421.bsp"
          : "de440s.bsp";

      string folder =
        Path.Combine(
          DataFolder,
          "Ephemeris");
      Directory.CreateDirectory(folder);

      return Path.Combine(
        folder,
        fileName);
    }

    internal Task<string> DownloadJplKernelAsync(
      JplEphemerisKernel kernel,
      CancellationToken cancellationToken = default) =>
      DownloadJplKernelAsync(
        kernel,
        progress: null,
        cancellationToken);

    internal async Task<string> DownloadJplKernelAsync(
      JplEphemerisKernel kernel,
      IProgress<JplDownloadProgress>? progress,
      CancellationToken cancellationToken = default)
    {
      if (kernel == JplEphemerisKernel.CustomFile)
        throw new ArgumentException(
          "CustomFile kernels must be selected from disk.",
          nameof(kernel));

      string[] urls =
        SplitSourceList(
          kernel == JplEphemerisKernel.DE421
            ? OrbitSources.De421Sources
            : OrbitSources.De440sSources);

      if (urls.Length == 0)
        throw new InvalidOperationException(
          $"No download source is configured for {kernel}.");

      string fileName =
        kernel == JplEphemerisKernel.DE421
          ? "de421.bsp"
          : "de440s.bsp";

      string folder =
        Path.Combine(
          DataFolder,
          "Ephemeris");
      Directory.CreateDirectory(folder);

      string destination =
        Path.Combine(
          folder,
          fileName);
      string temporary =
        destination + ".download";

      var failures = new List<string>();

      foreach (string url in urls)
      {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
          Log.Information(
            $"Downloading {kernel} ephemeris from {url}");

          using HttpResponseMessage response =
            await DownloadHttpClient.GetAsync(
              url,
              HttpCompletionOption.ResponseHeadersRead,
              cancellationToken);
          response.EnsureSuccessStatusCode();

          long? totalBytes =
            response.Content.Headers.ContentLength;
          long bytesReceived = 0;

          progress?.Report(
            new JplDownloadProgress(
              Array.IndexOf(urls, url) + 1,
              urls.Length,
              url,
              bytesReceived,
              totalBytes));

          await using (
            Stream source =
              await response.Content.ReadAsStreamAsync(cancellationToken))
          await using (
            FileStream output =
              new(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true))
          {
            byte[] buffer =
              new byte[1024 * 1024];

            while (true)
            {
              int read =
                await source.ReadAsync(
                  buffer.AsMemory(0, buffer.Length),
                  cancellationToken);
              if (read == 0)
                break;

              await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
              bytesReceived += read;

              progress?.Report(
                new JplDownloadProgress(
                  Array.IndexOf(urls, url) + 1,
                  urls.Length,
                  url,
                  bytesReceived,
                  totalBytes));
            }

            await output.FlushAsync(cancellationToken);
          }

          // Validate only after closing the download handle; FileShare.None is
          // intentional so incomplete kernels cannot be opened concurrently.
          _ = new JplSpkKernel(temporary);

          File.Move(
            temporary,
            destination,
            overwrite: true);

          progress?.Report(
            new JplDownloadProgress(
              Array.IndexOf(urls, url) + 1,
              urls.Length,
              url,
              new FileInfo(destination).Length,
              new FileInfo(destination).Length));

          return destination;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          try
          {
            if (File.Exists(temporary))
              File.Delete(temporary);
          }
          catch { }

          throw;
        }
        catch (Exception ex)
        {
          failures.Add($"{url}: {ex.Message}");
          Log.Warning(
            ex,
            $"JPL {kernel} source failed: {url}");

          try
          {
            if (File.Exists(temporary))
              File.Delete(temporary);
          }
          catch { }
        }
      }

      throw new IOException(
        $"All configured {kernel} download sources failed.\r\n" +
        string.Join("\r\n", failures));
    }

    internal int ConfigureSolarSystem(
      OrbitSourceSettings settings)
    {
      SatelliteList.Remove(MoonSatId);
      SatelliteList.Remove(SunSatId);
      SatelliteList.Remove(VenusSatId);
      PlanetaryKernel = null;

      if (!settings.ShowSolarSystemTargets)
        return 0;

      string path =
        GetJplKernelPath(settings);
      if (string.IsNullOrWhiteSpace(path) ||
          !File.Exists(path))
      {
        Log.Information(
          $"JPL ephemeris kernel not found: {path}");
        return 0;
      }

      try
      {
        PlanetaryKernel =
          new JplSpkKernel(path);

        int count = 0;
        count += AddEphemerisTarget(
          MoonSatId,
          "Moon",
          "Luna",
          "Natural satellite / EME",
          JplBody.Moon);
        count += AddEphemerisTarget(
          SunSatId,
          "Sun",
          "Sol",
          "Solar system",
          JplBody.Sun);
        count += AddEphemerisTarget(
          VenusSatId,
          "Venus",
          string.Empty,
          "Planet",
          JplBody.Venus);

        Log.Information(
          $"JPL ephemeris loaded: {Path.GetFileName(path)} ({count} targets)");
        return count;
      }
      catch (Exception ex)
      {
        PlanetaryKernel = null;
        Log.Error(
          ex,
          $"Unable to load JPL ephemeris kernel: {path}");
        return 0;
      }
    }

    private int AddEphemerisTarget(
      string id,
      string name,
      string alternateNames,
      string objectType,
      JplBody body)
    {
      if (PlanetaryKernel == null ||
          !PlanetaryKernel.Supports(
            body,
            DateTime.UtcNow))
        return 0;

      var target = new SatnogsDbSatellite
      {
        sat_id = id,
        norad_cat_id = null,
        name = name,
        names = alternateNames,
        image = string.Empty,
        status = "in orbit",
        website = string.Empty,
        @operator = objectType,
        countries = string.Empty,
        telemetries = new SatnogsDbSatellite.Telemetries(),
        citation =
          $"JPL/NAIF {Path.GetFileName(PlanetaryKernel.FileName)}",
        associated_satellites = new List<string>(),
        updated = File.GetLastWriteTimeUtc(PlanetaryKernel.FileName),
        EphemerisBody = body
      };

      target.SetTracker(
        new SatelliteTracker(
          PlanetaryKernel,
          body));
      target.BuildAllNames();
      target.SetFlags();

      SatelliteList[id] = target;
      return 1;
    }

    internal void ReplaceSatelliteList(SatnogsDb db)
    {
      SatelliteList = db.SatelliteList;
      loaded = SatelliteList.Count > 0;
      ListUpdated?.Invoke(this, EventArgs.Empty);
    }

    public SatnogsDbSatellite? GetSatellite(string satId)
    {
      return SatelliteList.GetValueOrDefault(satId);
    }


    //----------------------------------------------------------------------------------------------
    //                              download satellite data
    //----------------------------------------------------------------------------------------------
    public void AbortDownload()
    {
      cts.Cancel();
    }

    public async Task DownloadAll()
    {
      cts = new CancellationTokenSource();

      await DownloadConfigured(
        "satellites",
        OrbitSources.SatellitesUrl,
        required: true);
      Log.Information("satellites download step complete");
      DownloadProgress?.Invoke(this, new(25, null));

      await DownloadConfigured(
        "transmitters",
        OrbitSources.TransmittersUrl,
        required: true);
      Log.Information("transmitters download step complete");
      DownloadProgress?.Invoke(this, new(50, null));

      await DownloadConfigured(
        "tle",
        OrbitSources.TleUrl,
        required: false);
      Log.Information("primary TLE download step complete");
      DownloadProgress?.Invoke(this, new(70, null));

      await DownloadJE9PEL();
      Log.Information("JE9PEL downloaded");
      DownloadProgress?.Invoke(this, new(85, null));

      // gr-satellites satyaml is enrichment only — a failure here must not abort the download.
      try
      {
        await DownloadSatyaml();
        Log.Information("satyaml downloaded");
      }
      catch (OperationCanceledException) { throw; }
      catch (Exception ex) { Log.Warning(ex, "satyaml download/extract failed (enrichment skipped)"); }
      DownloadProgress?.Invoke(this, new(99, null));
    }

    public async Task DownloadTle()
    {
      cts = new CancellationTokenSource();

      try
      {
        // Automatic orbit priority, low -> high:
        // SatNOGS (legacy) < AutoTLE < user URL/file sources < CelesTrak OMM CSV.
        // A still-active manual file import is stored separately and remains
        // selected above all of these for exactly 72 hours.
        bool primaryDownloaded =
          await DownloadConfigured(
            "tle",
            OrbitSources.TleUrl,
            required: false);

        int primaryCount = 0;
        if (primaryDownloaded)
          primaryCount = ImportSatnogsTle();
        else
          Log.Information(
            "SatNOGS orbit source is disabled; retaining other automatic layers.");

        int autoTleCount =
          await LoadOptionalOrbitSourceAsync(
            OrbitSources.AutoTleUrl,
            "AutoTLE",
            cancellationToken: cts.Token);

        int customCount =
          await LoadCustomTleSourcesAsync(
            OrbitSources.CustomTleSources,
            raiseEvent: false,
            cancellationToken: cts.Token);

        int celestrakCount =
          await LoadOptionalOrbitSourceAsync(
            OrbitSources.CelestrakOmmCsvUrl,
            "CelesTrak OMM CSV",
            forcedExtension: ".csv",
            cancellationToken: cts.Token);

        int expiredManual =
          ReleaseExpiredManualOrbitPriority(
            DateTime.UtcNow,
            saveAndNotify: false);

        if (primaryDownloaded ||
            primaryCount > 0 ||
            autoTleCount > 0 ||
            customCount > 0 ||
            celestrakCount > 0 ||
            expiredManual > 0)
        {
          SaveToFile();
          TleUpdated?.Invoke(
            this,
            EventArgs.Empty);
        }

        Log.Information(
          "Orbit refresh complete: SatNOGS={Primary}, AutoTLE={AutoTle}, manual-links={Custom}, CelesTrak={Celestrak}, expired-manual={Expired}",
          primaryCount,
          autoTleCount,
          customCount,
          celestrakCount,
          expiredManual);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Orbit element refresh failed");
        throw;
      }
    }

    internal async Task<int> LoadAutomaticOrbitOverlaysAsync(
      CancellationToken cancellationToken = default)
    {
      // ImportAll() has already installed the lowest-priority SatNOGS layer.
      int autoTleCount =
        await LoadOptionalOrbitSourceAsync(
          OrbitSources.AutoTleUrl,
          "AutoTLE",
          cancellationToken: cancellationToken);

      int customCount =
        await LoadCustomTleSourcesAsync(
          OrbitSources.CustomTleSources,
          raiseEvent: false,
          cancellationToken: cancellationToken);

      int celestrakCount =
        await LoadOptionalOrbitSourceAsync(
          OrbitSources.CelestrakOmmCsvUrl,
          "CelesTrak OMM CSV",
          forcedExtension: ".csv",
          cancellationToken: cancellationToken);

      ReleaseExpiredManualOrbitPriority(
        DateTime.UtcNow,
        saveAndNotify: false);

      if (autoTleCount + customCount + celestrakCount > 0)
        SaveToFile();

      return autoTleCount + customCount + celestrakCount;
    }

    private async Task<int> LoadOptionalOrbitSourceAsync(
      string? source,
      string label,
      string? forcedExtension = null,
      CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(source))
        return 0;

      try
      {
        string content;
        string extension;

        if (Uri.TryCreate(source.Trim(), UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
          content =
            await DownloadHttpClient.GetStringAsync(
              uri,
              cancellationToken);
          extension =
            forcedExtension ??
            Path.GetExtension(uri.AbsolutePath);
        }
        else
        {
          string path =
            Environment.ExpandEnvironmentVariables(
              source.Trim());
          content =
            await File.ReadAllTextAsync(
              path,
              cancellationToken);
          extension =
            forcedExtension ??
            Path.GetExtension(path);
        }

        SatnogsDbTleList records =
          ParseTleContent(
            content,
            extension,
            label);

        int applied =
          ApplyTles(
            records,
            createMissingSatellites: true);

        Log.Information(
          "{OrbitSource} applied {Count} record(s).",
          label,
          applied);
        return applied;
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        // Automatic overlays are fallbacks. A failed high-priority source must
        // not prevent the already-applied lower-priority source from being used.
        Log.Warning(
          ex,
          "{OrbitSource} failed; keeping lower-priority automatic orbit data.",
          label);
        return 0;
      }
    }

    private async Task<bool> DownloadConfigured(
      string name,
      string? sourceUrl,
      bool required)
    {
      string destination =
        Path.Combine(
          DownloadsFolder,
          $"{name}.json");

      if (string.IsNullOrWhiteSpace(sourceUrl))
      {
        if (File.Exists(destination))
        {
          Log.Information(
            $"{name} source disabled; retaining cached copy.");
          return false;
        }

        if (required)
          throw new InvalidOperationException(
            $"{name} source URL is empty and no cached copy exists.");

        return false;
      }

      string content =
        await DownloadHttpClient.GetStringAsync(
          sourceUrl.Trim(),
          cts.Token);
      cts.Token.ThrowIfCancellationRequested();

      File.WriteAllText(
        destination,
        content);

      return true;
    }

    private async Task DownloadJE9PEL()
    {
      string url = "https://www.ne.jp/asahi/hamradio/je9pel/satslist.csv";
      string csv = await DownloadHttpClient.GetStringAsync(url, cts.Token);
      cts.Token.ThrowIfCancellationRequested();

      File.WriteAllText(Path.Combine(DownloadsFolder, "JE9PEL.csv"), csv);
    }

    // Download the gr-satellites repo zip and extract only python/satyaml/*.yml into Downloads\satyaml.
    private async Task DownloadSatyaml()
    {
      const string url = "https://codeload.github.com/daniestevez/gr-satellites/zip/refs/heads/main";
      byte[] zipBytes = await DownloadHttpClient.GetByteArrayAsync(url, cts.Token);
      cts.Token.ThrowIfCancellationRequested();

      string zipPath = Path.Combine(DownloadsFolder, "gr-satellites.zip");
      File.WriteAllBytes(zipPath, zipBytes);
      ExtractSatyaml(zipPath);
    }

    private void ExtractSatyaml(string zipPath)
    {
      string destDir = Path.Combine(DownloadsFolder, "satyaml");
      Directory.CreateDirectory(destDir);

      // start clean so a re-download does not leave stale .yml files behind
      foreach (string f in Directory.EnumerateFiles(destDir, "*.yml")) File.Delete(f);

      using ZipArchive archive = ZipFile.OpenRead(zipPath);
      int count = 0;
      foreach (ZipArchiveEntry entry in archive.Entries)
      {
        string full = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrEmpty(entry.Name) ||
            !full.Contains("/python/satyaml/") ||
            !full.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
          continue;

        entry.ExtractToFile(Path.Combine(destDir, entry.Name), overwrite: true);
        count++;
      }
      Log.Information($"satyaml: extracted {count} .yml files");
    }




    //----------------------------------------------------------------------------------------------
    //                                import satellite data
    //----------------------------------------------------------------------------------------------
    private bool CheckFilesPresent()
    {
      // Satellite metadata and transmitter metadata are required to rebuild the
      // database. The primary TLE feed is deliberately optional: operators may
      // clear that URL and rely entirely on Custom TLE Sources.
      return
        File.Exists(Path.Combine(DownloadsFolder, "satellites.json")) &&
        File.Exists(Path.Combine(DownloadsFolder, "transmitters.json")) &&
        File.Exists(Path.Combine(DownloadsFolder, "JE9PEL.csv"));
    }
    public void ImportAll()
    {
      try
      {
        if (!CheckFilesPresent()) throw new Exception("Satellite download(s) missing");

        SatelliteList.Clear();

        ImportSatnogsSatellites();
        ImportSatnogsTransmitters();

        if (!string.IsNullOrWhiteSpace(OrbitSources.TleUrl) &&
            File.Exists(Path.Combine(DownloadsFolder, "tle.json")))
          ImportSatnogsTle();
        else
          Log.Information(
            "Primary TLE source disabled or unavailable; custom TLE overlays will be applied after the base database import.");

        ImportJE9PEL();

        // enrichment only — never let a satyaml problem abort the import
        try { ImportSatyaml(); }
        catch (Exception ex) { Log.Warning(ex, "satyaml import failed (enrichment skipped)"); }

        // local hand-curated overrides on top of satyaml — enrichment only, never abort the import.
        // Kept here as well as at load time: the import writes Satellites.json, and leaving the manual
        // layer out of it would make an imported database momentarily disagree with a loaded one.
        try { ApplyTransmitterOverrides(); }
        catch (Exception ex) { Log.Warning(ex, "transmitter overrides import failed (enrichment skipped)"); }

        var satNames = new SatelliteNames();

        foreach (var sat in Satellites)
        {
          if (sat.norad_cat_id != null)
          {
            sat.LotwName = satNames.Lotw.GetValueOrDefault(sat.norad_cat_id.Value);
            if (sat.LotwName != null) { sat.name = sat.LotwName; sat.names += ", " + sat.LotwName; }
          }

          sat.BuildAllNames();
          sat.SetFlags();
        }
        
        // remove satellites without transmitters
        var satellitesWithoutTransmitters = SatelliteList.Where(kvp => kvp.Value.Transmitters.Count == 0).ToList();
        foreach (var sat in satellitesWithoutTransmitters)
        {
          SatelliteList.Remove(sat.Key);
          Log.Debug($"Removed satellite {sat.Value.name} (ID: {sat.Key}) because it has no transmitters");
        }

        SaveToFile();
        loaded = SatelliteList.Count > 0;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error importing satellite list");
        throw;
      }
    }

    private void ImportSatnogsSatellites()
    {
      string json = File.ReadAllText(Path.Combine(DownloadsFolder, "satellites.json"));
      SatnogsDbSatelliteList satellites = JsonConvert.DeserializeObject<SatnogsDbSatelliteList>(json, JsonSettings)!;
      SatelliteList = satellites.ToDictionary(s => s.sat_id);
    }

    private void ImportSatnogsTransmitters()
    {
      string json = File.ReadAllText(Path.Combine(DownloadsFolder, "transmitters.json"));
      SatnogsDbTransmitterList transmitters = JsonConvert.DeserializeObject<SatnogsDbTransmitterList>(json, JsonSettings)!;

      // only transmitters with downlink frequency are imported.
      // currently 3 transmitters out of 2K+ have downlink_low=null: KOSEN-2, CHUBUSAT-2 and CHUBUSAT-3
      foreach (SatnogsDbTransmitter t in transmitters.Where(t => t.downlink_low.HasValue))
      {
        var sat = SatelliteList.GetValueOrDefault(t.sat_id);
        if (sat != null)
        {
          t.DownlinkMode = ModeMnemonic.ToModeName(t.mode_id, t.mode);
          sat.Transmitters.Add(t);
          t.Satellite = sat;
        }
      }
    }

    private void ImportSatnogsTle()
    {
      string content =
        File.ReadAllText(
          Path.Combine(
            DownloadsFolder,
            "tle.json"));

      SatnogsDbTleList tles =
        ParseTleContent(
          content,
          null,
          "Primary TLE source");

      ApplyTles(
        tles,
        createMissingSatellites: false);
    }

    // Match gr-satellites satyaml entries to transmitters by NORAD + nearest baud, attach gr_sats.
    private void ImportSatyaml()
    {
      string dir = Path.Combine(DownloadsFolder, "satyaml");
      SatyamlDb? satyaml = SatyamlDb.Load(dir);
      if (satyaml == null)
      {
        Log.Warning("satyaml folder not found; skipping gr-satellites enrichment");
        return;
      }

      int matched = 0;
      foreach (var sat in Satellites)
      {
        if (sat.norad_cat_id == null) continue;
        foreach (var tx in sat.Transmitters)
        {
          GrSatsInfo? info = satyaml.Find(sat.norad_cat_id.Value, tx.baud);
          if (info != null) { tx.gr_sats = info; matched++; }
        }
      }
      Log.Information($"satyaml: enriched {matched} transmitters");
    }

    // Apply hand-curated GrSatsInfo overrides from Data\transmitters-override.json into the authoritative
    // manual layer (tx.manual), which the resolver ranks above the satyaml enrichment. The file maps a
    // transmitter uuid to a partial GrSatsInfo object that lists only the fields to override; absent fields
    // are left untouched (PopulateObject merges in place). This is how we supply data the SatNOGS DB lacks
    // and gr-satellites has no satyaml entry for (e.g. FSK deviation), or correct wrong DB values (e.g. baud).
    // Also called after every LoadFromFile and after the dialog writes a record (§6.2), so the file behaves
    // as live configuration rather than as an input to the import that produced Satellites.json.
    internal void ApplyTransmitterOverrides()
    {
      string path = Path.Combine(DataFolder, "transmitters-override.json");
      if (!File.Exists(path)) return;

      var overrides = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(path));
      if (overrides == null) return;

      // index every transmitter by uuid for a quick lookup
      var byUuid = new Dictionary<string, SatnogsDbTransmitter>();
      foreach (var sat in Satellites)
        foreach (var tx in sat.Transmitters)
          byUuid[tx.uuid] = tx;

      int applied = 0;
      foreach (var (uuid, fields) in overrides)
      {
        if (!byUuid.TryGetValue(uuid, out var tx)) continue;   // a satellite this database does not carry

        tx.manual ??= new GrSatsInfo();
        JsonConvert.PopulateObject(fields.ToString(), tx.manual);
        applied++;
      }
      Log.Information($"transmitter overrides: applied {applied} of {overrides.Count}");
    }

    // Write one transmitter's proven parameters into transmitters-override.json and apply them immediately
    // (discover_params_plan.md §6.1). This is the operator's only click in the discovery flow, and the only
    // step that persists anything: parameters are applied to the pass automatically on the first CRC-valid
    // frame, but written here only once the pass has produced FURTHER frames with them.
    //
    // The record is a partial GrSatsInfo, matching the file's existing format, and carries "read_only": true
    // so MergeOverrideFile treats it as user-claimed and does not refresh it from the shipped defaults on
    // the next start. Only the fields GrSatsInfo can express are written; the rest (AfCarrier, Manchester)
    // stay dialog-editable by hand (§6.5).
    internal void SaveTransmitterOverride(SatnogsDbTransmitter tx, SignalParams p)
    {
      string path = Path.Combine(DataFolder, "transmitters-override.json");

      Dictionary<string, JObject> records = new();
      if (File.Exists(path))
        try { records = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) { Log.Warning($"transmitters-override.json is unreadable, starting a fresh one: {ex.Message}"); }

      var record = new JObject
      {
        ["satellite"] = tx.Satellite?.name,
        ["norad"] = tx.norad_cat_id,
        ["read_only"] = true,
        ["modulation"] = p.Modulation.ToString(),
        // a measured rate is written as the round value it approximates (9600.832 -> 9600): the file is a
        // curated record of what the transmitter uses, not a log of what one pass happened to measure.
        ["baudrate"] = SignalParamsResolver.RoundToStandard(p.Baud),
        ["framing"] = FramingText(p.Framing)
      };
      // GMSK pins h = 1/2, so its deviation is implied rather than curated — writing it would turn a
      // derived value into an authoritative one.
      if (p.Modulation != Modulation.GMSK && (p.ResolvedDeviation ?? p.Deviation) is double dev)
        record["deviation"] = SignalParamsResolver.RoundToStandard(dev);
      if (p.RsBasis != null) record["rs_basis"] = p.RsBasis;
      if (p.FrameSize is int fs) record["frame_size"] = fs;
      if (p.Convolutional != null) record["convolutional"] = p.Convolutional;
      if (p.RsInterleaving is int ri) record["rs_interleaving"] = ri;
      if (p.Scrambler is bool sc) record["scrambler"] = sc ? "CCSDS" : "none";
      if (p.Differential is bool diff) record["precoding"] = diff ? "differential" : "none";

      records[tx.uuid] = record;
      File.WriteAllText(path, JsonConvert.SerializeObject(records, Formatting.Indented));
      Log.Information($"transmitter override saved for {tx.Satellite?.name} / {tx.description} ({tx.uuid})");

      // live configuration: the record must take effect now, not at the next database import (§6.2).
      ApplyTransmitterOverrides();
    }

    // Enum → the free text ExtractFraming classifies back to the same value. The override file stores DB-style
    // strings, not enum names, and the round trip has to survive: "AX100RS" would collapse back to AX100ASM.
    private static string FramingText(Framing framing) => framing switch
    {
      Framing.AX25G3RUH => "AX.25 G3RUH",
      Framing.USP => "USP",
      Framing.HADES => "Hades",
      Framing.AX100ASM => "AX100 ASM+Golay",
      Framing.AX100RS => "AX100 Reed Solomon",
      Framing.CCSDS => "CCSDS",
      // both spelled exactly as satyaml does, so ExtractFraming round-trips the written override
      Framing.GEOSCAN => "GEOSCAN",
      Framing.AO40FEC => "AO-40 FEC",
      _ => ""
    };

    // Merge the embedded default transmitters-override.json into the user-editable copy without clobbering
    // user edits. Records are keyed by transmitter uuid: user-only records are kept, new shipped records are
    // added, and a uuid present in both is refreshed from the shipped default unless the user has claimed it
    // with "read_only": true, in which case the user's copy is kept. The file is rewritten in shipped order,
    // with user-only records appended. read_only is a file-merge signal only and plays no part in resolution.
    private void MergeOverrideFile(string path)
    {
      var shipped = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(
        System.Text.Encoding.UTF8.GetString(Properties.Resources.transmitters_override)) ?? new();

      Dictionary<string, JObject> user = new();
      if (File.Exists(path))
        try { user = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) { Log.Warning($"transmitters-override.json is unreadable, refreshing from default: {ex.Message}"); }

      // shipped order first; a shipped record is refreshed unless the user has claimed it with read_only
      var merged = new Dictionary<string, JObject>();
      foreach (var (uuid, shippedRec) in shipped)
        merged[uuid] = user.TryGetValue(uuid, out var claimed) && claimed.Value<bool?>("read_only") == true
          ? claimed : shippedRec;

      // then append the records that exist only in the user file
      foreach (var (uuid, userRec) in user)
        if (!shipped.ContainsKey(uuid)) merged[uuid] = userRec;

      File.WriteAllText(path, JsonConvert.SerializeObject(merged, Formatting.Indented));
    }

    // example: RS-44;44909;145.935-145.995;435.670-435.610;435.605 ;SSB CW;RS44;Operational
    private void ImportJE9PEL()
    {
      Dictionary<int, List<JE9PELtransmitter>> dict = new();

      foreach (string line in File.ReadAllLines(Path.Combine(DownloadsFolder, "JE9PEL.csv")))
        try
        {
          JE9PELtransmitter tx = new(line);
          if (tx.NoradId == 0) continue;
          dict.TryAdd(tx.NoradId, new());
          dict[tx.NoradId].Add(tx);
        }
        catch (Exception ex)
        {
          Log.Error($"Error in JE9PEL.csv: {ex.Message} Line: '{line}'");
        }

      // insert in satnogs db
      foreach (var sat in Satellites)
        if (sat.norad_cat_id != null)
          if (dict.ContainsKey((int)sat.norad_cat_id))
          {
            sat.JE9PELtransmitters = dict[(int)sat.norad_cat_id];
            sat.JE9PEL_Callsigns = sat.JE9PELtransmitters.SelectMany(t => t.Call.Split(' ')).Where(c => c != "").Distinct().ToList();
            sat.JE9PEL_Names = sat.JE9PELtransmitters.SelectMany(t => t.Name.Split(' ')).Where(c => c != "").Distinct().ToList();
          }
    }

    internal void Customize(Dictionary<string, SatelliteCustomization> satelliteCustomizations)
    {
      foreach (var cust in satelliteCustomizations.Values)
      {
        if (cust.sat_id == null) continue;

        if (!SatelliteList.TryGetValue(cust.sat_id, out SatnogsDbSatellite? sat)) return;

        if (!string.IsNullOrEmpty(cust.Name))
        {
          sat.name = cust.Name;
          sat.BuildAllNames();
        }
      }
    }

    internal void LoadTleFromFile(string tleFileName)
    {
      Log.Information($"Loading TLE from file: {tleFileName}");

      try
      {
        string tleContent = File.ReadAllText(tleFileName);
        SatnogsDbTleList tles = ParseTleContent(
          tleContent,
          Path.GetExtension(tleFileName),
          $"File: {Path.GetFileName(tleFileName)}");

        ApplyTles(tles, createMissingSatellites: true);
        SaveToFile();
        TleUpdated?.Invoke(this, EventArgs.Empty);
        Log.Information($"TLE loaded: {tles.Count} records");
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Load TLE from file failed");
        throw;
      }
    }

    internal async Task<int> LoadCustomTleSourcesAsync(
      string? sourceList,
      bool raiseEvent = true,
      CancellationToken cancellationToken = default)
    {
      string[] sources = SplitSourceList(sourceList);
      if (sources.Length == 0) return 0;

      // Priority is intentionally explicit:
      //   Custom source #1 > custom source #2 > ... > primary TLE.
      // Fetch in the user's visible order, then apply in reverse order so the
      // first configured source is written last and therefore wins any NORAD
      // collision. A failed high-priority source simply lets the next source
      // provide that object's TLE.
      var loadedSources =
        new Dictionary<string, SatnogsDbTleList>(
          StringComparer.OrdinalIgnoreCase);

      foreach (string source in sources)
      {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
          string content;
          string extension;
          string label;

          if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
              (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
          {
            content = await DownloadHttpClient.GetStringAsync(uri, cancellationToken);
            extension = Path.GetExtension(uri.AbsolutePath);
            label = $"URL: {uri.Host}";
          }
          else
          {
            string path = Environment.ExpandEnvironmentVariables(source);
            content = await File.ReadAllTextAsync(path, cancellationToken);
            extension = Path.GetExtension(path);
            label = $"File: {Path.GetFileName(path)}";
          }

          SatnogsDbTleList tles = ParseTleContent(content, extension, label);
          loadedSources[source] = tles;
          Log.Information(
            $"Custom TLE source fetched: {source} ({tles.Count} records)");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // One broken optional source must not suppress the remaining sources
          // or the lower-priority primary TLE.
          Log.Warning(ex, $"Custom TLE source failed: {source}");
        }
      }

      int total = 0;
      foreach (string source in GetCustomTleApplyOrder(sourceList))
      {
        if (!loadedSources.TryGetValue(
              source,
              out SatnogsDbTleList? tles))
          continue;

        int applied =
          ApplyTles(
            tles,
            createMissingSatellites: true);
        total += applied;

        int priority =
          Array.IndexOf(
            sources,
            source) + 1;
        Log.Information(
          $"Custom TLE source applied at priority {priority}: {source} ({applied} records)");
      }

      if (total > 0)
      {
        SaveToFile();
        loaded = SatelliteList.Count > 0;
        if (raiseEvent) TleUpdated?.Invoke(this, EventArgs.Empty);
      }

      return total;
    }

    internal static string[] SplitCustomTleSources(string? sourceList) =>
      SplitSourceList(sourceList);

    // Apply low priority first so the first configured custom source is applied
    // last and wins collisions. This helper is deliberately testable because
    // source order is part of the user-visible orbit-data contract.
    internal static string[] GetCustomTleApplyOrder(string? sourceList) =>
      SplitSourceList(sourceList)
        .Reverse()
        .ToArray();

    internal static string[] SplitSourceList(string? sourceList) =>
      string.IsNullOrWhiteSpace(sourceList)
        ? Array.Empty<string>()
        : sourceList
          .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
          .Select(s => s.Trim())
          .Where(s => s.Length > 0)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

    private SatnogsDbTleList ParseTleContent(
      string content,
      string? extension,
      string sourceLabel)
    {
      SatnogsDbTleList tles;

      if (content.TrimStart().StartsWith("["))
      {
        tles =
          JsonConvert.DeserializeObject<SatnogsDbTleList>(content, JsonSettings)
          ?? new SatnogsDbTleList();
      }
      else
      {
        tles = TlesFromText(content, sourceLabel);
      }

      foreach (SatnogsDbTle tle in tles)
      {
        if (tle.norad_cat_id == null && !string.IsNullOrWhiteSpace(tle.tle1))
          tle.norad_cat_id = ParseNoradId(tle.tle1);

        if (string.IsNullOrWhiteSpace(tle.tle_source))
          tle.tle_source = sourceLabel;

        if (tle.updated == default)
          tle.updated = DateTime.UtcNow;
      }

      return tles;
    }

    private int ApplyTles(
      IEnumerable<SatnogsDbTle> tles,
      bool createMissingSatellites)
    {
      int applied = 0;

      foreach (SatnogsDbTle tle in tles)
      {
        if (tle.norad_cat_id == null) continue;

        SatnogsDbSatellite? sat = Satellites
          .FirstOrDefault(s => s.norad_cat_id == tle.norad_cat_id);

        if (sat == null && createMissingSatellites)
        {
          string satId = $"CUSTOM-{tle.norad_cat_id.Value:00000}";
          string name = NormalizeTleName(tle.tle0, tle.norad_cat_id.Value);

          sat = new SatnogsDbSatellite
          {
            sat_id = satId,
            norad_cat_id = tle.norad_cat_id,
            name = name,
            names = string.Empty,
            image = string.Empty,
            status = "in orbit",
            website = string.Empty,
            @operator = string.Empty,
            countries = string.Empty,
            telemetries = new SatnogsDbSatellite.Telemetries(),
            citation = tle.tle_source ?? string.Empty,
            associated_satellites = new List<string>(),
            updated = tle.updated
          };

          SatelliteList[satId] = sat;
        }

        if (sat == null) continue;

        tle.sat_id = sat.sat_id;
        sat.SetTle(tle);
        sat.updated = tle.updated;
        sat.BuildAllNames();
        sat.SetFlags();
        applied++;
      }

      return applied;
    }

    internal static SatnogsDbTleList TlesFromText(
      string tleContent,
      string sourceLabel = "Local file")
    {
      string[] lines = tleContent
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToArray();

      SatnogsDbTleList tles = new();
      int i = 0;

      while (i < lines.Length)
      {
        string name;
        string line1;
        string line2;

        if (lines[i].StartsWith("1 ") &&
            i + 1 < lines.Length &&
            lines[i + 1].StartsWith("2 "))
        {
          line1 = lines[i];
          line2 = lines[i + 1];
          int norad = ParseNoradId(line1);
          name = $"NORAD {norad}";
          i += 2;
        }
        else
        {
          if (i + 2 >= lines.Length ||
              !lines[i + 1].StartsWith("1 ") ||
              !lines[i + 2].StartsWith("2 "))
            throw new ArgumentException(
              $"Invalid TLE record near line {i + 1}. Expected name/line1/line2 or line1/line2.");

          name = lines[i].StartsWith("0 ") ? lines[i][2..].Trim() : lines[i];
          line1 = lines[i + 1];
          line2 = lines[i + 2];
          i += 3;
        }

        if (line1.Length < 69 || line2.Length < 69)
          throw new ArgumentException("Invalid TLE format: line 1/2 is shorter than 69 characters.");

        int noradId = ParseNoradId(line1);
        tles.Add(new SatnogsDbTle
        {
          tle0 = name,
          tle1 = line1,
          tle2 = line2,
          tle_source = sourceLabel,
          updated = DateTime.UtcNow,
          norad_cat_id = noradId
        });
      }

      return tles;
    }

    private static int ParseNoradId(string line1)
    {
      if (line1.Length < 7 ||
          !int.TryParse(
            line1.Substring(2, 5).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int noradId))
        throw new ArgumentException("Unable to parse NORAD catalog number from TLE line 1.");

      return noradId;
    }

    private static string NormalizeTleName(string? tle0, int noradId)
    {
      string? name = tle0?.Trim();
      if (name?.StartsWith("0 ") == true) name = name[2..].Trim();
      return string.IsNullOrWhiteSpace(name) ? $"NORAD {noradId}" : name;
    }
  }
}