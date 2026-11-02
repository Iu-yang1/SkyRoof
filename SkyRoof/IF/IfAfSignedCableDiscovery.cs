using System;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof.IF
{
  /// <summary>Selected render/capture pair from an already installed,
  /// OS-enumerated third-party virtual cable. This is ENDPOINT detection,
  /// not cryptographic verification of the driver's catalog signature.</summary>
  public readonly record struct IfAfSignedCablePair(
    string Provider, string? RenderId, string? CaptureId, string Status)
  {
    public bool Ready => !string.IsNullOrWhiteSpace(RenderId) &&
      !string.IsNullOrWhiteSpace(CaptureId) &&
      !string.Equals(RenderId, CaptureId, StringComparison.Ordinal);
  }

  public static class IfAfSignedCableDiscovery
  {
    public const string VbCableUrl = "https://vb-audio.com/Cable/";
    public const string VacLiteUrl = "https://vac.muzychenko.net/en/";

    /// <summary>Independent device enumeration supports stable WASAPI endpoint
    /// IDs. Never use the system default render endpoint or a radio TX codec.</summary>
    public static IfAfSignedCablePair Detect()
    {
      try
      {
        return Select(Soundcard.ListDevices(DataFlow.Render),
          Soundcard.ListDevices(DataFlow.Capture));
      }
      catch (Exception ex)
      {
        return new("None", null, null,
          "Windows audio enumeration unavailable: " + ex.GetType().Name);
      }
    }

    /// <summary>Pure matching, accepts only known vendor endpoint pairs.
    /// Localized Windows prefixes differ, but vendor device labels are
    /// unchanged; neither partial pairing nor generic "virtual" matches
    /// are sufficient. One-way playback devices are rejected.</summary>
    public static IfAfSignedCablePair Select(
      AudioDeviceEntry[] render, AudioDeviceEntry[] capture)
    {
      ArgumentNullException.ThrowIfNull(render);
      ArgumentNullException.ThrowIfNull(capture);
      // A base VB-CABLE installation exposes CABLE Input and CABLE Output.
      // A/B/C/D paid cables or arbitrary renamed devices require explicit
      // selection until a deterministic paired profile is implemented.
      var vb = SelectExactPair(render, capture, "VB-CABLE",
        name => IsVbCableEnd(name, "CABLE Input"),
        name => IsVbCableEnd(name, "CABLE Output"));
      if (vb.Ready) return vb;

      // Eugene Muzychenko's VAC Lite defaults to Line 1 on both sides.
      // Do not mistake a separately renamed line for an established pair.
      var vac = SelectExactPair(render, capture, "VAC Lite",
        name => IsVacLiteLine(name),
        name => IsVacLiteLine(name));
      if (vac.Ready) return vac;

      return new("None", null, null,
        "No unambiguous installed VB-CABLE or VAC Lite render/capture pair. " +
        "Install the original vendor package, or use ExistingVirtualCable with an explicit render device.");
    }

    private static IfAfSignedCablePair SelectExactPair(
      AudioDeviceEntry[] render, AudioDeviceEntry[] capture, string provider,
      Func<string?, bool> isRender, Func<string?, bool> isCapture)
    {
      var matchesOut = Array.FindAll(render, x => IsSafe(x) && isRender(x.Name));
      var matchesIn = Array.FindAll(capture, x => IsSafe(x) && isCapture(x.Name));
      if (matchesOut.Length != 1 || matchesIn.Length != 1 ||
          string.Equals(matchesOut[0].Id, matchesIn[0].Id, StringComparison.Ordinal))
        return new(provider, null, null, "Missing or ambiguous " + provider + " endpoints.");
      return new(provider, matchesOut[0].Id, matchesIn[0].Id,
        provider + " playback and recording endpoints detected (signature not independently verified).");
    }

    private static bool IsSafe(AudioDeviceEntry entry) =>
      !string.IsNullOrWhiteSpace(entry.Id) &&
      !string.IsNullOrWhiteSpace(entry.Name) &&
      !entry.Name.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase) &&
      !entry.Name.Contains("ICOM", StringComparison.OrdinalIgnoreCase);

    public static bool IsVbCableEnd(string? name, string end)
    {
      string n = name?.Trim() ?? "";
      return n.Equals(end, StringComparison.OrdinalIgnoreCase) ||
        n.Equals(end + " (VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith("(" + end + " (VB-Audio Virtual Cable))",
          StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith("（" + end + " (VB-Audio Virtual Cable)）",
          StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVacLiteLine(string? name)
    {
      string n = name?.Trim() ?? "";
      return n.Equals("Line 1 (Virtual Audio Cable)", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith("(Line 1 (Virtual Audio Cable))", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith("（Line 1 (Virtual Audio Cable)）", StringComparison.OrdinalIgnoreCase);
    }
  }
}
