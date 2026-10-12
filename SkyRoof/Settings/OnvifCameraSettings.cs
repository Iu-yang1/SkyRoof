using System.ComponentModel;
using Newtonsoft.Json;

namespace SkyRoof
{
  /// <summary>Persisted ONVIF camera preferences. Never persist the raw password.</summary>
  public sealed class OnvifCameraSettings
  {
    [DisplayName("Camera name")]
    public string CameraName { get; set; } = "Camera 01";

    [DisplayName("ONVIF Device Service URL")]
    public string DeviceServiceUrl { get; set; } = "";

    [DisplayName("Camera user")]
    public string Username { get; set; } = "";

    [Browsable(false)]
    [JsonIgnore]
    public string Password { get; set; } = "";

    [Browsable(false)]
    [JsonProperty("PasswordProtected")]
    public string PasswordProtected
    {
      get => SecretProtector.Protect(Password);
      set => Password = SecretProtector.Unprotect(value);
    }

    [DisplayName("Preferred profile token")]
    public string PreferredProfileToken { get; set; } = "";

    [DisplayName("Preferred media version")]
    [DefaultValue(true)]
    public bool PreferMedia2 { get; set; } = true;

    [DisplayName("mpv executable")]
    [DefaultValue("mpv.exe")]
    public string MpvPath { get; set; } = "mpv.exe";

    [DisplayName("Connect on open")]
    [DefaultValue(true)]
    public bool AutoConnect { get; set; } = true;

    [DisplayName("Play on open")]
    [DefaultValue(false)]
    public bool AutoPlay { get; set; }

    [DisplayName("Reconnect after interruption")]
    [DefaultValue(false)]
    public bool AutoReconnect { get; set; }

    [Browsable(false)]
    [JsonIgnore]
    public bool IsConfigured =>
      Uri.TryCreate(DeviceServiceUrl?.Trim(), UriKind.Absolute, out Uri? uri) &&
      uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static string ValidateEndpoint(string endpoint)
    {
      if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out Uri? uri) ||
          uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        return "Enter an HTTP(S) ONVIF Device Service address, not an RTSP URL.";

      if (uri.Port == 554 && uri.AbsolutePath == "/")
        return "Port 554 is normally RTSP, not ONVIF. Try http://" +
          uri.Host + "/onvif/device_service or use the XAddr discovered by ODM.";

      return string.Empty;
    }

    // A detached copy keeps Cancel from mutating the live or persisted settings.
    internal OnvifCameraSettings Clone() => new()
    {
      CameraName = CameraName, DeviceServiceUrl = DeviceServiceUrl,
      Username = Username, Password = Password,
      PreferredProfileToken = PreferredProfileToken,
      PreferMedia2 = PreferMedia2, MpvPath = MpvPath,
      AutoConnect = AutoConnect, AutoPlay = AutoPlay,
      AutoReconnect = AutoReconnect
    };

    internal static OnvifMediaProfile SelectProfile(
      IReadOnlyList<OnvifMediaProfile> available, string token, bool preferMedia2)
    {
      if (available.Count == 0)
        throw new InvalidOperationException("Camera returned no ONVIF media profiles.");
      return available.FirstOrDefault(p => p.Token == token && p.Media2 == preferMedia2)
        ?? available.FirstOrDefault(p => p.Token == token)
        ?? available.FirstOrDefault(p => p.Media2 == preferMedia2)
        ?? available[0];
    }

    public override string ToString() => string.Empty;
  }
}
