using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Serilog;
using VE3NEA;
using VE3NEA.SkyTlm.Core;

namespace SkyRoof
{
  // Uploads decoded telemetry frames to the SatNOGS DB (db.satnogs.org) over the SiDS (Simple Downlink
  // Share Convention) protocol: one form-urlencoded HTTP POST per frame, authenticated with the user's
  // permanent SatNOGS DB API key (Authorization: Token <key>). Owned by the TelemetryPanel and disposed
  // with it, so no uploads happen when the panel is closed. The POSTs run on a single background worker
  // draining a queue, so they never block the decode or UI thread.
  public class SatnogsUploader : IDisposable
  {
    // production SatNOGS DB telemetry endpoint (SiDS); independent forwarders must use production,
    // not the db-dev staging server (which exists only to test the SatNOGS platform itself)
    private const string Url = "https://db.satnogs.org/api/telemetry/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string Version =
      "SkyRoof " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0");

    private readonly Context ctx;
    private readonly BlockingCollection<Submission> Queue = new();
    private readonly Task Worker;

    private record Submission(string Token, byte[] Body, DateTime Timestamp);


    public SatnogsUploader(Context ctx)
    {
      this.ctx = ctx;
      Worker = Task.Run(ProcessQueue);
    }

    public void Dispose()
    {
      Queue.CompleteAdding();
      try { Worker.Wait(TimeSpan.FromSeconds(2)); }
      catch (Exception ex)
      {
        Log.Warning("SatNOGS uploader worker shutdown failed ({Type})", ex.GetType().Name);
      }
      Queue.Dispose();
    }


    //----------------------------------------------------------------------------------------------
    //                                      submit
    //----------------------------------------------------------------------------------------------
    // called on the decode thread (via TelemetryPanel.FrameDecodedHandler); enqueues and returns
    public void Submit(Frame frame, int noradId)
    {
      // gate: skip frames that explicitly failed the integrity check; keep CRC-valid and CRC-less (null)
      if (frame.CrcValid == false) return;

      var sett = ctx.Settings.Telemetry.SatnogsUploader;
      var user = ctx.Settings.User;

      if (!sett.Enabled) return;
      if (string.IsNullOrWhiteSpace(sett.ApiToken)) return;
      if (string.IsNullOrWhiteSpace(user.Call)) return;
      if (string.IsNullOrWhiteSpace(user.Square) || !GridSquare.IsValid(user.Square)) return;

      var location = GridSquare.ToGeoPoint(user.Square);
      var timestamp = DateTime.UtcNow;

      var fields = new Dictionary<string, string>
      {
        ["noradID"] = noradId.ToString(),
        ["source"] = user.Call.Trim().ToUpperInvariant(),
        ["locator"] = "longLat",
        ["longitude"] = $"{Math.Abs(location.Longitude).ToString("F4", CultureInfo.InvariantCulture)}{(location.Longitude >= 0 ? "E" : "W")}",
        ["latitude"] = $"{Math.Abs(location.Latitude).ToString("F4", CultureInfo.InvariantCulture)}{(location.Latitude >= 0 ? "N" : "S")}",
        ["timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z",
        ["frame"] = frame.Hex,
        ["version"] = Version
      };

      var content = new FormUrlEncodedContent(fields);
      byte[] body = content.ReadAsByteArrayAsync().Result;
      Log.Information($"Uploading SatNOGS frame: {JsonConvert.SerializeObject(fields)}");

      if (!Queue.IsAddingCompleted)
        try { Queue.Add(new Submission(sett.ApiToken.Trim(), body, timestamp)); }
        catch (InvalidOperationException)
        {
          Log.Debug("SatNOGS frame discarded because upload queue was closed during shutdown");
        }
    }


    //----------------------------------------------------------------------------------------------
    //                                      worker
    //----------------------------------------------------------------------------------------------
    private void ProcessQueue()
    {
      foreach (var item in Queue.GetConsumingEnumerable())
        try { Post(item); }
        catch (Exception e) { Log.Warning(e, "SatNOGS upload failed"); }
    }

    private void Post(Submission item)
    {
      if (!ctx.Settings.Telemetry.SatnogsUploader.Enabled) return;

      using var content = new ByteArrayContent(item.Body);
      content.Headers.ContentType = new("application/x-www-form-urlencoded");

      using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
      request.Headers.TryAddWithoutValidation("Authorization", $"Token {item.Token}");

      using var response = Http.Send(request);

      if (response.IsSuccessStatusCode)
        Log.Information($"SatNOGS frame uploaded ({(int)response.StatusCode}), frame time {item.Timestamp:HH:mm:ss}Z");
      else
      {
        // read the response body so the log shows the server's actual error (e.g. bad timestamp, bad
        // token); ReadAsStringAsync is needed because HttpContent.ToString() only yields the type name
        string reason;
        try { reason = response.Content.ReadAsStringAsync().Result.Trim(); }
        catch (Exception e) { reason = $"<body unreadable: {e.Message}>"; }
        Log.Warning($"SatNOGS upload rejected: HTTP {(int)response.StatusCode} {response.ReasonPhrase} {reason}");
      }
    }
  }
}
