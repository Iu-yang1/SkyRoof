using System;
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
      string.Equals(name?.Trim(), VirtualPlaybackName,
        StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name?.Trim(), "Speakers (IF-AF-Audio Playback)",
        StringComparison.OrdinalIgnoreCase);

    public static bool IsExpectedCaptureName(string? name) =>
      string.Equals(name?.Trim(), VirtualCaptureName,
        StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name?.Trim(), "Microphone (IF-AF-Audio)",
        StringComparison.OrdinalIgnoreCase);
  }
}
