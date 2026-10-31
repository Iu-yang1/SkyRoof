using System;
using System.Drawing;
using System.Linq;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Bounded-memory, live 48-kHz AF SSTV receive pipeline for the extended
  /// MMSSTV raster families. Retains eight seconds of pre-VIS audio so a
  /// header can be detected after its stop bit, then scans each subsequent
  /// line as it arrives. Emits immutable bitmap copies every 12 lines.
  /// No radio CAT writes, no TX device access.
  /// </summary>
  public sealed class SstvLiveRasterReceiver : IDisposable
  {
    private const int Rate = 48000;
    private const int RingLength = Rate * 8;
    private readonly float[] ring = new float[RingLength];
    private readonly SstvAfToneDiscriminator demod = new();
    private int write, available, lineFill;
    private long streamSamples, lastDetectionSamples;
    private float[] lineFrequencies = Array.Empty<float>();
    private Bitmap? picture;
    private SstvTxModeProfile? profile;
    private SstvVisModeIdentity identity;
    private int lineIndex;
    private double offsetHz;
    private long ignoreNativeUntil;
    private bool disposed;

    public event Action<Bitmap, string, bool, double>? ImageReady;
    public bool IsDecoding => profile != null;
    public string CurrentMode => profile?.Name ?? "";
    public double CorrectionHz => -offsetHz;

    public void Process(ReadOnlySpan<float> af)
    {
      if (disposed) throw new ObjectDisposedException(nameof(SstvLiveRasterReceiver));
      if (profile != null)
      {
        ProcessRaster(af);
        streamSamples += af.Length;
        return;
      }

      for (int i = 0; i < af.Length; i++)
      {
        ring[write++] = af[i];
        if (write == RingLength) write = 0;
        if (available < RingLength) available++;
      }
      streamSamples += af.Length;
      if (streamSamples < 48000 ||
          streamSamples - lastDetectionSamples < 24000 ||
          streamSamples < ignoreNativeUntil)
        return;
      lastDetectionSamples = streamSamples;

      var snapshot = new float[available];
      int from = (write - available + RingLength) % RingLength;
      for (int i = 0; i < available; i++)
        snapshot[i] = ring[(from + i) % RingLength];
      SstvVisModeIdentity? found = SstvVisModeIdentifier.DetectAudio(snapshot);
      if (!found.HasValue) return;
      if (found.Value.Profile.Native != null)
      {
        // SkySignals already owns Robot/PD real-time decoding. Do not
        // emit a duplicate or try to apply RGB scan timing to its modes.
        ignoreNativeUntil = streamSamples + 30L * Rate;
        return;
      }

      identity = found.Value;
      profile = identity.Profile;
      picture = new Bitmap(profile.Width, profile.Height);
      lineIndex = lineFill = 0;
      offsetHz = identity.FrequencyOffsetHz;
      double headerMs = identity.Profile.Rgb is { } rgb
        ? 910 + (rgb.Layout == SstvRgbTxLayout.Scottie ? 9 : 0)
        : identity.Profile.Extra!.HeaderMs;
      int start = checked((int)Math.Round(
        (identity.LeadingAudio.TotalMilliseconds + headerMs) * 48));
      if (start < snapshot.Length)
        ProcessRaster(snapshot.AsSpan(start));
    }

    private int NextLineSamples()
    {
      double ms = profile!.Rgb?.LineMs ?? profile.Extra!.LineMs;
      return checked((int)(Math.Round((lineIndex + 1) * ms * 48) -
                           Math.Round(lineIndex * ms * 48)));
    }

    private void ProcessRaster(ReadOnlySpan<float> af)
    {
      for (int i = 0; i < af.Length && profile != null; i++)
      {
        int expected = NextLineSamples();
        if (expected <= 0 || expected > Rate * 3)
        {
          Reset();
          return;
        }
        if (lineFrequencies.Length < expected)
          lineFrequencies = new float[expected];
        lineFrequencies[lineFill++] =
          (float)(demod.Process(af[i]) - offsetHz);
        if (lineFill < expected) continue;

        UpdateLineSyncOffset(lineFrequencies, expected);
        SstvOfflineRasterDecoder.RenderLine(
          picture!, profile, lineIndex, lineFrequencies.AsSpan(0, expected));

        lineIndex++;
        lineFill = 0;
        int total = profile.Rgb?.Height ?? profile.Extra!.LineCount;
        bool done = lineIndex >= total;
        if (done || lineIndex % 12 == 0)
        {
          Bitmap frame = new Bitmap(picture!);
          try { ImageReady?.Invoke(frame, profile.Name, done, -offsetHz); }
          catch { frame.Dispose(); throw; }
          if (ImageReady == null) frame.Dispose();
        }
        if (done) Reset();
      }
    }

    private void UpdateLineSyncOffset(float[] frequencies, int samples)
    {
      if (profile == null) return;
      double syncHz = 1200, syncAtMs = 0, syncMs = 0;
      if (profile.Rgb is { } rgb)
      {
        syncMs = rgb.SyncMs;
        if (rgb.Layout == SstvRgbTxLayout.Scottie)
        {
          syncAtMs = 2 * rgb.ScanMs + 3;
          syncMs = 9;
        }
      }
      else if (profile.Extra is { } extra)
      {
        if (extra.Layout == SstvExtraLayout.Avt) return;
        syncMs = extra.SyncMs;
        if (extra.Narrow) syncHz = 1900;
      }
      if (syncMs < 4) return;
      int first = Math.Clamp((int)((syncAtMs + 1.4) * 48), 0, samples);
      int last = Math.Clamp((int)((syncAtMs + syncMs - 1.0) * 48), first, samples);
      if (last - first < 90) return;
      double sum = 0, squares = 0;
      for (int i = first; i < last; i++)
      {
        double v = frequencies[i];
        sum += v;
        squares += v * v;
      }
      int count = last - first;
      double mean = sum / count;
      double sigma = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
      if (sigma < 50 && Math.Abs(mean - syncHz) < 95)
        offsetHz = Math.Clamp(offsetHz + .22 * (mean - syncHz), -400, 400);
    }

    public void Reset()
    {
      picture?.Dispose();
      picture = null;
      profile = null;
      lineFill = lineIndex = 0;
      available = write = 0;
      lastDetectionSamples = streamSamples;
      // Detection ring is cleared at a session epoch boundary.
    }

    public void Dispose()
    {
      disposed = true;
      Reset();
    }
  }
}
