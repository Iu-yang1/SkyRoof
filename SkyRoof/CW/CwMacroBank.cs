namespace SkyRoof.CW
{
  public static class CwMacroBank
  {
    public const int Count = 8;

    public static string Get(
      CwMacroSettings settings,
      int index)
    {
      ArgumentNullException.ThrowIfNull(
        settings);

      return index switch
      {
        0 => settings.F1,
        1 => settings.F2,
        2 => settings.F3,
        3 => settings.F4,
        4 => settings.F5,
        5 => settings.F6,
        6 => settings.F7,
        7 => settings.F8,
        _ => throw new ArgumentOutOfRangeException(
          nameof(index))
      } ?? string.Empty;
    }

    // Used by the Console's right-click F1-F8 editor. Persisting the
    // owning Settings object is the caller's responsibility.
    public static void Set(
      CwMacroSettings settings,
      int index,
      string text)
    {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(text);
      if (text.Length > 0)
        CwMessageTiming.ValidateText(text);

      switch (index)
      {
        case 0: settings.F1 = text; break;
        case 1: settings.F2 = text; break;
        case 2: settings.F3 = text; break;
        case 3: settings.F4 = text; break;
        case 4: settings.F5 = text; break;
        case 5: settings.F6 = text; break;
        case 6: settings.F7 = text; break;
        case 7: settings.F8 = text; break;
        default: throw new ArgumentOutOfRangeException(nameof(index));
      }
    }

    public static string Prepare(
      CwMacroSettings settings,
      int index)
    {
      string text =
        Get(settings, index)
          .Trim()
          .ToUpperInvariant();

      if (text.Length == 0)
        throw new InvalidOperationException(
          $"CW macro F{index + 1} is empty.");

      CwMessageTiming.ValidateText(text);
      return text;
    }

    public static string Preview(
      CwMacroSettings settings,
      int index,
      int maxCharacters = 12)
    {
      if (maxCharacters < 4)
        throw new ArgumentOutOfRangeException(
          nameof(maxCharacters));

      string text =
        Get(settings, index)
          .Trim()
          .Replace(
            "\r",
            " ")
          .Replace(
            "\n",
            " ");

      if (text.Length == 0)
        return $"F{index + 1}";

      if (text.Length >
          maxCharacters)
        text =
          text[..(maxCharacters - 1)] +
          "…";

      return
        $"F{index + 1} {text}";
    }
  }
}
