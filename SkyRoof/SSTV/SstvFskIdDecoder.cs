using System;
using System.IO;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Decode MMSSTV's optional 6-bit LSB-first audio FSK station ID:
  /// 2100-Hz 100-ms guard, 1900-Hz 22-ms separator, STX 0x2A,
  /// 6-bit ASCII-minus-0x20 payload, EOT 0x01 and XOR checksum.
  /// It never creates a QSO automatically.
  /// </summary>
  public static class SstvFskIdDecoder
  {
    private const int SampleRate = 48000;
    private const int FiveMs = SampleRate / 200;
    private const int SymbolSamples = 22 * 48;

    public static string? DecodeWaveSuffix(string path)
    {
      using var wav = new WaveFileReader(path);
      if (wav.WaveFormat.SampleRate != SampleRate ||
          wav.WaveFormat.Channels != 1 ||
          wav.WaveFormat.BitsPerSample != 16)
        return null;
      // Seek only the last five seconds, rather than loading a 9-minute
      // recording into memory solely to obtain the station ID.
      long suffix = Math.Min(wav.Length, 5L * SampleRate * 2);
      wav.Position = wav.Length - suffix;
      byte[] bytes = new byte[(int)suffix];
      int read = 0;
      while (read < bytes.Length)
      {
        int n = wav.Read(bytes, read, bytes.Length - read);
        if (n == 0) break;
        read += n;
      }
      float[] mono = new float[read / 2];
      for (int i = 0; i < mono.Length; i++)
        mono[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8) / 32768f;
      return Decode(mono);
    }

    public static string? Decode(ReadOnlySpan<float> audio)
    {
      if (audio.Length < SampleRate / 2) return null;
      int consecutiveGuardFrames = 0;
      // Only soundcard audio enters here; the scan cannot key any radio.
      for (int at = 0; at + FiveMs <= audio.Length; at += FiveMs)
      {
        double hz = SstvVisAfcDetector.EstimatePitch(
          audio.Slice(at, FiveMs));
        if (Near(hz, 2100, 80))
        {
          consecutiveGuardFrames++;
          continue;
        }
        if (consecutiveGuardFrames >= 16 && Near(hz, 1900, 80))
        {
          // A 22-ms separator transition may fall anywhere within the
          // 5-ms pitch frame; also tolerate the matched window starting
          // in the second 5-ms segment after the transition.
          for (int shift = -9; shift <= 5; shift++)
          {
            string? id = TryReadPayload(audio,
              at + shift * 48);
            if (id != null) return id;
          }
        }
        consecutiveGuardFrames = 0;
      }
      return null;
    }

    private static string? TryReadPayload(
      ReadOnlySpan<float> audio, int separatorStart)
    {
      int? ReadByte(int byteIndex)
      {
        int result = 0;
        for (int bit = 0; bit < 6; bit++)
        {
          int center = separatorStart + SymbolSamples +
            (byteIndex * 6 + bit) * SymbolSamples +
            SymbolSamples / 2;
          int begin = center - FiveMs / 2;
          if (begin < 0 || begin + FiveMs > audio.Length)
            return null;
          double hz = SstvVisAfcDetector.EstimatePitch(
            audio.Slice(begin, FiveMs));
          if (Near(hz, 1900, 75)) result |= 1 << bit;
          else if (!Near(hz, 2100, 75)) return null;
        }
        return result;
      }

      if (ReadByte(0) != 0x2A) return null;
      int checksum = 0;
      var callsign = new System.Text.StringBuilder();
      for (int i = 1; i <= 17; i++)
      {
        int? symbol = ReadByte(i);
        if (symbol == null) return null;
        if (symbol == 0x01)
        {
          if (ReadByte(i + 1) != checksum) return null;
          string call = callsign.ToString();
          return Regex.IsMatch(call, @"^[A-Z0-9/]{3,16}$")
            ? call : null;
        }
        checksum ^= symbol.Value;
        int ascii = symbol.Value + 0x20;
        if (!((ascii >= 'A' && ascii <= 'Z') ||
              (ascii >= '0' && ascii <= '9') || ascii == '/'))
          return null;
        callsign.Append((char)ascii);
      }
      return null;
    }

    private static bool Near(double observed, double expected, double tolerance) =>
      double.IsFinite(observed) &&
      Math.Abs(observed - expected) <= tolerance;
  }
}
