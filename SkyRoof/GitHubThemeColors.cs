namespace SkyRoof
{
  internal sealed class GitHubPalette
  {
    internal Color CanvasDefault { get; init; }
    internal Color CanvasSubtle { get; init; }
    internal Color CanvasInset { get; init; }
    internal Color BorderDefault { get; init; }
    internal Color BorderMuted { get; init; }
    internal Color TextPrimary { get; init; }
    internal Color TextSecondary { get; init; }
    internal Color Accent { get; init; }
    internal Color AccentEmphasis { get; init; }
    internal Color AccentMuted { get; init; }
    internal Color Success { get; init; }
    internal Color SuccessMuted { get; init; }
    internal Color Attention { get; init; }
    internal Color AttentionMuted { get; init; }
    internal Color Danger { get; init; }
    internal Color DangerMuted { get; init; }
  }

  // GitHub Primer-inspired application palettes. Keep these values isolated
  // from SkyRoof's pink/blue identity so existing themes remain unchanged.
  internal static class GitHubThemeColors
  {
    internal static readonly GitHubPalette Light =
      new()
      {
        CanvasDefault =
          Color.FromArgb(0xFF, 0xFF, 0xFF),
        CanvasSubtle =
          Color.FromArgb(0xF6, 0xF8, 0xFA),
        CanvasInset =
          Color.FromArgb(0xEF, 0xF2, 0xF5),
        BorderDefault =
          Color.FromArgb(0xD0, 0xD7, 0xDE),
        BorderMuted =
          Color.FromArgb(0xD8, 0xDE, 0xE4),
        TextPrimary =
          Color.FromArgb(0x24, 0x29, 0x2F),
        TextSecondary =
          Color.FromArgb(0x57, 0x60, 0x6A),
        Accent =
          Color.FromArgb(0x09, 0x69, 0xDA),
        AccentEmphasis =
          Color.FromArgb(0x05, 0x50, 0xAE),
        AccentMuted =
          Color.FromArgb(0xDD, 0xF4, 0xFF),
        Success =
          Color.FromArgb(0x1A, 0x7F, 0x37),
        SuccessMuted =
          Color.FromArgb(0xDA, 0xFB, 0xE1),
        Attention =
          Color.FromArgb(0x9A, 0x67, 0x00),
        AttentionMuted =
          Color.FromArgb(0xFF, 0xF8, 0xC5),
        Danger =
          Color.FromArgb(0xCF, 0x22, 0x2E),
        DangerMuted =
          Color.FromArgb(0xFF, 0xEB, 0xE9)
      };

    internal static readonly GitHubPalette Dark =
      new()
      {
        CanvasDefault =
          Color.FromArgb(0x0D, 0x11, 0x17),
        CanvasSubtle =
          Color.FromArgb(0x16, 0x1B, 0x22),
        CanvasInset =
          Color.FromArgb(0x01, 0x04, 0x09),
        BorderDefault =
          Color.FromArgb(0x30, 0x36, 0x3D),
        BorderMuted =
          Color.FromArgb(0x21, 0x26, 0x2D),
        TextPrimary =
          Color.FromArgb(0xC9, 0xD1, 0xD9),
        TextSecondary =
          Color.FromArgb(0x8B, 0x94, 0x9E),
        Accent =
          Color.FromArgb(0x58, 0xA6, 0xFF),
        AccentEmphasis =
          Color.FromArgb(0x1F, 0x6F, 0xEB),
        AccentMuted =
          Color.FromArgb(0x0C, 0x2D, 0x6B),
        Success =
          Color.FromArgb(0x3F, 0xB9, 0x50),
        SuccessMuted =
          Color.FromArgb(0x03, 0x3A, 0x16),
        Attention =
          Color.FromArgb(0xD2, 0x99, 0x22),
        AttentionMuted =
          Color.FromArgb(0x34, 0x1A, 0x00),
        Danger =
          Color.FromArgb(0xF8, 0x51, 0x49),
        DangerMuted =
          Color.FromArgb(0x49, 0x02, 0x02)
      };
  }
}
