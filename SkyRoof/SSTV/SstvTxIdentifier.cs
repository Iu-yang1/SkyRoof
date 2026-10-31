using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SkyRoof.SSTV
{
  public sealed record SstvIdOptions(
    string Callsign, bool IncludeCw, bool IncludeFsk,
    int CwWpm = 20, double CwToneHz = 800);

  /// <summary>
  /// Append an optional MMSSTV-style 6-bit FSK station ID and/or amateur
  /// CW ID after a completed image. These are AF tone outputs, not CAT keyer
  /// operations. FSK codes follow MMSSTV sstv.cpp WriteFSK and Main.cpp
  /// OutputFSKID, LSB-first at 1900/2100 Hz, 22 ms symbols, XOR checksum.
  /// </summary>
  public static class SstvTxIdentifier
  {
    private static readonly IReadOnlyDictionary<char, string> Morse =
      new Dictionary<char, string>
      {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.",
        ['D'] = "-..", ['E'] = ".", ['F'] = "..-.",
        ['G'] = "--.", ['H'] = "....", ['I'] = "..",
        ['J'] = ".---", ['K'] = "-.-", ['L'] = ".-..",
        ['M'] = "--", ['N'] = "-.", ['O'] = "---",
        ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.",
        ['S'] = "...", ['T'] = "-", ['U'] = "..-",
        ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-",
        ['Y'] = "-.--", ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---",
        ['3'] = "...--", ['4'] = "....-", ['5'] = ".....",
        ['6'] = "-....", ['7'] = "--...", ['8'] = "---..",
        ['9'] = "----.", ['/'] = "-..-."
      };

    public static void Append(
      SstvWaveExporter.WaveOscillator oscillator, SstvIdOptions? options)
    {
      if (options == null || (!options.IncludeFsk && !options.IncludeCw))
        return;
      string call = (options.Callsign ?? "").Trim().ToUpperInvariant();
      if (!Regex.IsMatch(call, @"^[A-Z0-9/]{3,16}$"))
        throw new ArgumentException("SSTV station ID requires a valid callsign.", nameof(options));
      if (options.CwWpm is < 5 or > 40 ||
          options.CwToneHz < 400 || options.CwToneHz > 1500 ||
          !double.IsFinite(options.CwToneHz))
        throw new ArgumentException("Invalid CW ID pitch or WPM.", nameof(options));
      if (options.IncludeFsk) EmitFskId(oscillator, call);
      if (options.IncludeCw) EmitCwId(oscillator, call, options.CwToneHz, options.CwWpm);
    }

    private static void EmitFskId(SstvWaveExporter.WaveOscillator outAudio, string call)
    {
      outAudio.Tone(2100, 100);
      outAudio.Tone(1900, 22);
      WriteFskByte(outAudio, 0x2A); // STX
      int xor = 0;
      foreach (char letter in call)
      {
        int code = letter - 0x20;
        if (code is < 0 or > 0x3F)
          throw new ArgumentException("Character cannot be encoded in MMSSTV FSK ID.");
        xor ^= code;
        WriteFskByte(outAudio, code);
      }
      WriteFskByte(outAudio, 0x01); // EOT
      WriteFskByte(outAudio, xor);
      outAudio.Tone(2100, 100);
    }

    private static void WriteFskByte(SstvWaveExporter.WaveOscillator outAudio, int data)
    {
      for (int bit = 0; bit < 6; bit++)
        outAudio.Tone((data & (1 << bit)) != 0 ? 1900 : 2100, 22);
    }

    private static void EmitCwId(SstvWaveExporter.WaveOscillator output,
      string call, double tone, int wpm)
    {
      double dot = 1200.0 / wpm;
      output.Tone(0, 250);
      for (int k = 0; k < call.Length; k++)
      {
        if (!Morse.TryGetValue(call[k], out string? code)) continue;
        for (int j = 0; j < code.Length; j++)
        {
          output.KeyedTone(tone, code[j] == '.' ? dot : dot * 3);
          if (j != code.Length - 1) output.Tone(0, dot);
        }
        if (k != call.Length - 1) output.Tone(0, dot * 3);
      }
      output.Tone(0, 250);
    }
  }
}
