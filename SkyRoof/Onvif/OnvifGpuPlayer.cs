using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SkyRoof
{
  /// <summary>
  /// GPU-only Windows mpv host. Deliberately does not use libVLC automatic
  /// decode fallback, FFmpeg CPU frame extraction or WinForms Bitmap rendering.
  /// Media is loaded through a private, random-named Windows IPC pipe, never
  /// through command-line arguments containing RTSP credentials.
  /// </summary>
  internal sealed class OnvifGpuPlayer : IDisposable
  {
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private CancellationTokenSource? monitorCancel;
    private int nextRequestId;
    internal event Action<string>? PlaybackFailed;

    internal async Task StartAsync(string executable, IntPtr hwnd,
      Uri rtsp, string username, string password, CancellationToken cancel)
    {
      Stop();
      if (rtsp.Scheme is not ("rtsp" or "rtsps"))
        throw new ArgumentException("Only ONVIF RTSP stream URIs are supported.");

      string pipeName = "skyroof-onvif-" + Guid.NewGuid().ToString("N");
      uint windowHandle = unchecked((uint)hwnd.ToInt64());
      var args = BuildHardwareOnlyArguments(windowHandle, pipeName);

      var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(executable) ? "mpv.exe" : executable)
      {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
      };
      foreach (string arg in args) info.ArgumentList.Add(arg);

      process = Process.Start(info) ??
        throw new InvalidOperationException("Could not start mpv.exe.");
      try
      {
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
          PipeOptions.Asynchronous);
        await pipe.ConnectAsync(6000, cancel);
        reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        { AutoFlush = true };

        // Never put credentials in process arguments, logs or user-visible errors.
        Uri playUri = rtsp;
        if (!string.IsNullOrWhiteSpace(username) &&
            string.IsNullOrEmpty(rtsp.UserInfo))
        {
          var builder = new UriBuilder(rtsp) { UserName = username, Password = password };
          playUri = builder.Uri;
        }

        await CommandAsync(new object[] { "loadfile", playUri.AbsoluteUri, "replace" }, cancel);
        // mpv may choose software decoding during initial codec negotiation even
        // when --hwdec is set. Require positive live D3D11VA readback.
        bool verified = false;
        for (int attempt = 0; attempt < 28; attempt++)
        {
          cancel.ThrowIfCancellationRequested();
          string? current = await PropertyAsync("hwdec-current", cancel);
          if (string.Equals(current, "d3d11va", StringComparison.OrdinalIgnoreCase))
          {
            verified = true;
            break;
          }
          // During RTSP DESCRIBE/SETUP, hwdec-current may report "no"
          // before the first video frame. Never accept it as a GPU decoder;
          // allow initialization time, then fail closed at the deadline.
          await Task.Delay(250, cancel);
        }
        if (!verified)
          throw new NotSupportedException(
            "D3D11VA hardware decoding could not be confirmed; playback stopped.");

        monitorCancel = new CancellationTokenSource();
        _ = MonitorAsync(monitorCancel.Token);
      }
      catch
      {
        Stop();
        throw;
      }
    }

    internal static string[] BuildHardwareOnlyArguments(uint windowHandle, string pipeName)
    {
      return new[]
      {
        "--no-config",
        "--idle=yes",
        "--force-window=yes",
        "--vo=gpu",
        "--gpu-context=d3d11",
        "--d3d11-warp=no",
        "--hwdec=d3d11va",
        "--hwdec-software-fallback=no",
        "--hwdec-codecs=all",
        "--rtsp-transport=tcp",
        "--audio=no",
        "--osc=no",
        "--osd-level=0",
        "--input-terminal=no",
        "--terminal=no",
        "--wid=" + windowHandle,
        @"--input-ipc-server=\\.\pipe\" + pipeName
      };
    }

    private async Task MonitorAsync(CancellationToken cancel)
    {
      while (!cancel.IsCancellationRequested)
      {
        try
        {
          await Task.Delay(2500, cancel);
          string? decoder = await PropertyAsync("hwdec-current", cancel);
          if (decoder != null &&
              !decoder.Equals("d3d11va", StringComparison.OrdinalIgnoreCase))
          {
            Stop();
            PlaybackFailed?.Invoke("Camera lost D3D11VA hardware decoding; playback stopped.");
            return;
          }
          if (process?.HasExited == true)
          {
            Stop();
            PlaybackFailed?.Invoke("Camera player exited; inspect the stream or GPU drivers.");
            return;
          }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception)
        {
          Stop();
          PlaybackFailed?.Invoke("Camera playback health check failed; GPU-only playback stopped.");
          return;
        }
      }
    }

    private async Task<string?> PropertyAsync(string name, CancellationToken cancel)
    {
      using JsonDocument answer = await CommandAsync(
        new object[] { "get_property", name }, cancel);
      var root = answer.RootElement;
      if (!root.TryGetProperty("error", out JsonElement error) ||
          error.GetString() != "success" ||
          !root.TryGetProperty("data", out JsonElement data))
        return null;
      return data.ValueKind == JsonValueKind.String ? data.GetString() : null;
    }

    private async Task<JsonDocument> CommandAsync(object[] arguments, CancellationToken cancel)
    {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
      timeout.CancelAfter(TimeSpan.FromSeconds(3));
      await ioGate.WaitAsync(timeout.Token);
      try
      {
        if (reader == null || writer == null || pipe?.IsConnected != true)
          throw new IOException("Camera IPC is not connected.");
        int id = Interlocked.Increment(ref nextRequestId);
        string line = JsonSerializer.Serialize(new { command = arguments, request_id = id });
        await writer.WriteLineAsync(line.AsMemory(), timeout.Token);
        while (true)
        {
          string? response = await reader.ReadLineAsync(timeout.Token);
          if (response == null) throw new IOException("Camera player disconnected.");
          var result = JsonDocument.Parse(response);
          if (result.RootElement.TryGetProperty("request_id", out JsonElement request) &&
              request.GetInt32() == id)
            return result;
          result.Dispose(); // Unsolicited mpv events have no request_id.
        }
      }
      finally { ioGate.Release(); }
    }

    internal void Stop()
    {
      monitorCancel?.Cancel();
      monitorCancel?.Dispose();
      monitorCancel = null;
      reader?.Dispose();
      writer?.Dispose();
      pipe?.Dispose();
      reader = null;
      writer = null;
      pipe = null;
      try
      {
        if (process != null && !process.HasExited)
          process.Kill(entireProcessTree: true);
      }
      catch (InvalidOperationException) { }
      catch (System.ComponentModel.Win32Exception) { }
      process?.Dispose();
      process = null;
    }

    public void Dispose()
    {
      Stop();
      ioGate.Dispose();
    }
  }
}
