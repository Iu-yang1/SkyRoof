namespace SkyRoof.CW
{
  public static class CwMessageTiming
  {
    public const int MaxCharacters = 30;
    public const double MinimumWpm = 6;
    public const double MaximumWpm = 48;

    private static readonly IReadOnlyDictionary<char, string>
      Morse = new Dictionary<char, string>
      {
        ['A'] = ".-",
        ['B'] = "-...",
        ['C'] = "-.-.",
        ['D'] = "-..",
        ['E'] = ".",
        ['F'] = "..-.",
        ['G'] = "--.",
        ['H'] = "....",
        ['I'] = "..",
        ['J'] = ".---",
        ['K'] = "-.-",
        ['L'] = ".-..",
        ['M'] = "--",
        ['N'] = "-.",
        ['O'] = "---",
        ['P'] = ".--.",
        ['Q'] = "--.-",
        ['R'] = ".-.",
        ['S'] = "...",
        ['T'] = "-",
        ['U'] = "..-",
        ['V'] = "...-",
        ['W'] = ".--",
        ['X'] = "-..-",
        ['Y'] = "-.--",
        ['Z'] = "--..",
        ['0'] = "-----",
        ['1'] = ".----",
        ['2'] = "..---",
        ['3'] = "...--",
        ['4'] = "....-",
        ['5'] = ".....",
        ['6'] = "-....",
        ['7'] = "--...",
        ['8'] = "---..",
        ['9'] = "----.",
        ['/'] = "-..-.",
        ['?'] = "..--..",
        ['.'] = ".-.-.-",
        ['-'] = "-....-",
        [','] = "--..--",
        [':'] = "---...",
        ['\''] = ".----.",
        ['('] = "-.--.",
        [')'] = "-.--.-",
        ['='] = "-...-",
        ['+'] = ".-.-.",
        ['"'] = ".-..-.",
        ['@'] = ".--.-."
      };

    public static void ValidateText(
      string text)
    {
      if (string.IsNullOrEmpty(text))
        throw new ArgumentException(
          "CW text must not be empty.",
          nameof(text));

      if (text.Length > MaxCharacters)
        throw new ArgumentException(
          $"CW text exceeds {MaxCharacters} characters.",
          nameof(text));

      foreach (char original in text)
      {
        char ch =
          char.ToUpperInvariant(original);

        if (ch == ' ' ||
            ch == '^' ||
            Morse.ContainsKey(ch))
          continue;

        throw new ArgumentException(
          $"Character '{original}' is not supported by the IC-9700 CW keyer.",
          nameof(text));
      }
    }

    /// <summary>
    /// IC-9700 CI-V 14 0C uses normalized 0000..0255 for the documented
    /// 6..48 WPM key-speed control.
    /// </summary>
    public static double RawKeySpeedToWpm(
      int raw)
    {
      if (raw is < 0 or > 255)
        throw new ArgumentOutOfRangeException(
          nameof(raw));

      return
        MinimumWpm +
        raw *
        (MaximumWpm - MinimumWpm) /
        255.0;
    }

    /// <summary>
    /// Estimate the actual keyed duration using standard Morse timing.
    /// Dot=1, dash=3, intra-element=1, character=3, word=7 units.
    /// IC-9700 '^' removes the normal inter-character space for prosigns;
    /// model it as one intra-element unit between the joined characters.
    /// </summary>
    public static TimeSpan EstimateDuration(
      string text,
      double wpm)
    {
      ValidateText(text);

      if (!double.IsFinite(wpm) ||
          wpm < MinimumWpm ||
          wpm > MaximumWpm)
        throw new ArgumentOutOfRangeException(
          nameof(wpm));

      double units = 0;
      bool havePreviousSymbol = false;
      bool pendingWordSpace = false;
      bool joinNext = false;

      foreach (char original in text)
      {
        char ch =
          char.ToUpperInvariant(original);

        if (ch == ' ')
        {
          if (havePreviousSymbol)
            pendingWordSpace = true;
          joinNext = false;
          continue;
        }

        if (ch == '^')
        {
          if (havePreviousSymbol &&
              !pendingWordSpace)
            joinNext = true;
          continue;
        }

        string pattern =
          Morse[ch];

        if (havePreviousSymbol)
        {
          units +=
            pendingWordSpace
              ? 7
              : joinNext
                ? 1
                : 3;
        }

        for (int i = 0;
             i < pattern.Length;
             i++)
        {
          units +=
            pattern[i] == '-'
              ? 3
              : 1;

          if (i + 1 < pattern.Length)
            units += 1;
        }

        havePreviousSymbol = true;
        pendingWordSpace = false;
        joinNext = false;
      }

      // PARIS defines 50 units per word, so one dot unit is 1.2/WPM seconds.
      double seconds =
        units *
        1.2 /
        wpm;

      return TimeSpan.FromSeconds(
        seconds);
    }

    public static TimeSpan ComputeWatchdog(
      string text,
      int? keySpeedRaw)
    {
      double wpm =
        keySpeedRaw is >= 0 and <= 255
          ? RawKeySpeedToWpm(
              keySpeedRaw.Value)
          : MinimumWpm;

      TimeSpan estimated =
        EstimateDuration(
          text,
          wpm);

      double watchdogSeconds =
        estimated.TotalSeconds *
          1.35 +
        1.5;

      return TimeSpan.FromSeconds(
        Math.Clamp(
          watchdogSeconds,
          2.0,
          90.0));
    }
  }
}
