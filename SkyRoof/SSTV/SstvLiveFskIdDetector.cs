using System;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Bounded streaming detector for the optional MMSSTV FSK callsign tail.
  /// Buffers five seconds of AF to tolerate callbacks of arbitrary size.
  /// Station IDs are advisory only: a decoded ID does not establish a QSO.
  /// No CAT/PTT access.
  /// </summary>
  public sealed class SstvLiveFskIdDetector
  {
    private const int SampleRate = 48000;
    private const int Capacity = SampleRate * 5;
    private const int ScanInterval = SampleRate / 4;
    private const int DuplicateGuard = SampleRate * 10;
    private readonly float[] buffer = new float[Capacity];
    private int write, count;
    private long received, lastScan, lastReported;
    private string lastCall = "";

    public event Action<string>? StationIdentified;
    public void Reset()
    {
      write = count = 0;
      received = lastScan = lastReported = 0;
      lastCall = "";
    }

    public void Process(ReadOnlySpan<float> audio)
    {
      for (int i = 0; i < audio.Length; i++)
      {
        buffer[write++] = audio[i];
        if (write == Capacity) write = 0;
        if (count < Capacity) count++;
      }
      received += audio.Length;
      if (count < SampleRate / 2 ||
          received - lastScan < ScanInterval)
        return;
      lastScan = received;

      var ordered = new float[count];
      int first = write - count;
      if (first < 0) first += Capacity;
      int front = Math.Min(count, Capacity - first);
      Array.Copy(buffer, first, ordered, 0, front);
      if (front < count)
        Array.Copy(buffer, 0, ordered, front, count - front);

      string? candidate = SstvFskIdDecoder.Decode(ordered);
      if (candidate == null) return;
      if (candidate == lastCall &&
          received - lastReported < DuplicateGuard) return;
      lastCall = candidate;
      lastReported = received;
      StationIdentified?.Invoke(candidate);
    }
  }
}
