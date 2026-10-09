using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SkyRoof
{
  /// <summary>
  /// Controls only a Windows *render audio session*, never endpoint master
  /// volume or the transceiver's CI-V AF gain. RS-BA1 Remote Utility typically
  /// owns the receive-audio session (Remote Control does not necessarily).
  /// Ambiguous/missing sessions fail closed until selected explicitly by user.
  /// </summary>
  internal sealed class RsBa1AudioSessionController
  {
    internal sealed record AudioSessionInfo(
      int ProcessId,
      string ProcessName,
      string WindowTitle,
      string DeviceId,
      string DeviceName,
      float Volume,
      bool AutoCandidate)
    {
      internal string DisplayText =>
        $"{ProcessName} (PID {ProcessId}) — {DeviceName}";
    }

    internal static bool IsLikelyRemoteUtility(
      string name, string title, string company)
    {
      // Never match generic audio players or RS-BA1's *controller* when
      // the Remote Utility is handling sound instead.
      string normalized = name.Replace("-", "").Replace("_", "");
      return normalized.Contains("RSBA1Remote", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("RemoteUtility", StringComparison.OrdinalIgnoreCase) &&
               (company.Contains("Icom", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Remote Utility", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("RSBA1", StringComparison.OrdinalIgnoreCase)) ||
             (company.Contains("Icom", StringComparison.OrdinalIgnoreCase) &&
              title.Contains("Remote Utility", StringComparison.OrdinalIgnoreCase));
    }

    internal static float GainDbToScalar(int db) =>
      Math.Clamp((float)Math.Pow(10, Math.Clamp(db, -50, 0) / 20.0), 0f, 1f);

    internal static int ScalarToGainDb(float volume) =>
      Math.Clamp((int)Math.Round(20 * Math.Log10(Math.Max(volume, 0.00001f))),
        -50, 0);

    internal IReadOnlyList<AudioSessionInfo> ListSessions()
    {
      List<AudioSessionInfo> found = new();
      using var enumerator = new MMDeviceEnumerator();
      foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(
        DataFlow.Render, DeviceState.Active))
      {
        try
        {
          var sessions = device.AudioSessionManager.Sessions;
          for (int i = 0; i < sessions.Count; i++)
          {
            try
            {
              var session = sessions[i];
              if (session.GetProcessID is 0 or > int.MaxValue)
                continue;

              using Process process = Process.GetProcessById(
                (int)session.GetProcessID);
              string name = process.ProcessName;
              string title = process.MainWindowTitle ?? "";
              string company = "";
              try { company = process.MainModule?.FileVersionInfo.CompanyName ?? ""; }
              catch (System.ComponentModel.Win32Exception) { }
              catch (InvalidOperationException) { }

              found.Add(new AudioSessionInfo(
                process.Id, name, title, device.ID, device.FriendlyName,
                session.SimpleAudioVolume.Volume,
                IsLikelyRemoteUtility(name, title, company)));
            }
            catch (ArgumentException) { } // session process exited
            catch (InvalidOperationException) { } // session disconnected
            catch (System.Runtime.InteropServices.COMException) { }
          }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        finally { device.Dispose(); }
      }
      return found;
    }

    // Resolve exactly one session, preventing unintended changes when two
    // different RS-BA1 instances or output devices have active streams.
    internal static AudioSessionInfo? Resolve(
      IReadOnlyList<AudioSessionInfo> sessions,
      string? selectedName,
      string? selectedDeviceId)
    {
      var matches = sessions.Where(s =>
        !string.IsNullOrWhiteSpace(selectedName)
          ? string.Equals(s.ProcessName, selectedName,
              StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(selectedDeviceId) ||
             s.DeviceId == selectedDeviceId)
          : s.AutoCandidate).ToArray();

      return matches.Length == 1 ? matches[0] : null;
    }

    internal bool TryGetVolume(
      string? processName, string? deviceId, out float volume,
      out AudioSessionInfo? target)
    {
      target = Resolve(ListSessions(), processName, deviceId);
      volume = target?.Volume ?? 0;
      return target != null;
    }

    internal bool TrySetVolume(AudioSessionInfo target, float scalar)
    {
      // Identify both endpoint and PID to avoid a stale or ambiguous match.
      using var enumerator = new MMDeviceEnumerator();
      MMDevice? device = enumerator.EnumerateAudioEndPoints(
        DataFlow.Render, DeviceState.Active)
        .FirstOrDefault(d => d.ID == target.DeviceId);
      if (device == null)
        return false;

      try
      {
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
          var session = sessions[i];
          if (session.GetProcessID != (uint)target.ProcessId)
            continue;

          try
          {
            using Process process = Process.GetProcessById(target.ProcessId);
            if (!string.Equals(process.ProcessName, target.ProcessName,
                  StringComparison.OrdinalIgnoreCase))
              return false;
            session.SimpleAudioVolume.Volume = Math.Clamp(scalar, 0f, 1f);
            return true;
          }
          catch (ArgumentException) { return false; }
        }
      }
      catch (System.Runtime.InteropServices.COMException) { }
      finally { device.Dispose(); }
      return false;
    }
  }
}
