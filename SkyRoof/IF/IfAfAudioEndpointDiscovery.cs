using System;
using System.IO;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof.IF
{
  /// <summary>
  /// Discover the paired Windows audio endpoints created by a genuine
  /// signed IF-AF-Audio driver. An in-process .NET audio buffer is NOT a
  /// Windows microphone device and must never be shown to WSJT-X as one.
  /// </summary>
  public readonly record struct IfAfEndpointPair(
    string? RenderId, string? CaptureId, string Status)
  {
    public bool Ready => !string.IsNullOrEmpty(RenderId) &&
      !string.IsNullOrEmpty(CaptureId);
  }

  public static class IfAfAudioEndpointDiscovery
  {
    /// <summary>
    /// A development driver may be bundled as files without being installed.
    /// Finding INF/SYS/CAT here never means the driver is installed or trusted.
    /// </summary>
    public static string? BundledDevelopmentPackagePath
    {
      get
      {
        string path = Path.Combine(AppContext.BaseDirectory, "Drivers", "IF-AF-Audio");
        return IsCompleteDevelopmentPackage(path) ? path : null;
      }
    }

    public static bool IsCompleteDevelopmentPackage(string? folder)
    {
      if (string.IsNullOrWhiteSpace(folder)) return false;
      return File.Exists(Path.Combine(folder, "IfAfAudio.sys")) &&
        File.Exists(Path.Combine(folder, "IfAfAudio.inf")) &&
        File.Exists(Path.Combine(folder, "ifafaudio.cat"));
    }

    public const string VirtualCaptureName = "IF-AF-Audio";
    public const string VirtualPlaybackName = "IF-AF-Audio Playback";

    public static IfAfEndpointPair Detect()
    {
      AudioDeviceEntry[] render, capture;
      try
      {
        render = Soundcard.ListDevices(DataFlow.Render);
        capture = Soundcard.ListDevices(DataFlow.Capture);
      }
      catch (Exception ex)
      {
        // The Settings property grid may inspect DriverStatus even when
        // Windows Audio Service is unavailable. Discovery must not crash UI.
        return new(null, null, "Windows audio device enumeration unavailable: " +
          ex.GetType().Name);
      }
      var renderDevice = Array.Find(render,
        e => IsExpectedRenderName(e.Name));
      var captureDevice = Array.Find(capture,
        e => IsExpectedCaptureName(e.Name));
      if (renderDevice == null || captureDevice == null)
        return new(renderDevice?.Id, captureDevice?.Id,
          "IF-AF-Audio Windows driver not installed or endpoints are inactive. " +
          "Driver installation and kernel signing are required; do not route " +
          "to IC-9700 USB Audio CODEC (TX).");
      if (renderDevice.Id == captureDevice.Id)
        return new(null, null, "Virtual playback/capture endpoint IDs overlap.");
      return new(renderDevice.Id, captureDevice.Id,
        "IF-AF-Audio playback and recording endpoints detected.");
    }

    public static bool IsExpectedRenderName(string? name) =>
      MatchesEndpointName(name, VirtualPlaybackName);

    public static bool IsExpectedCaptureName(string? name) =>
      MatchesEndpointName(name, VirtualCaptureName);

    private static bool MatchesEndpointName(string? name, string exactName)
    {
      string value = name?.Trim() ?? "";
      return string.Equals(value, exactName, StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("(" + exactName + ")", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("（" + exactName + "）", StringComparison.OrdinalIgnoreCase);
    }
  }
}
