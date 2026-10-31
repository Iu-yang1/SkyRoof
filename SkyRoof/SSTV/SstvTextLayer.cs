using System;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Independently editable SSTV text layer. X and Y are the upper-left
  /// corner in normalized canvas coordinates; size is percent of canvas width.
  /// Stored independently of the source bitmap and reused by every TX mode.
  /// </summary>
  public sealed record SstvTextLayer(float X, float Y, float SizePercent, int ColorArgb)
  {
    public static SstvTextLayer MyCallDefault => new(.04f, .69f, 6.5f, unchecked((int)0xFFFFFFFF));
    public static SstvTextLayer TheirCallDefault => new(.51f, .69f, 6.5f, unchecked((int)0xFF80D8FF));
    public static SstvTextLayer CustomDefault => new(.04f, .87f, 5.5f, unchecked((int)0xFFFFFFFF));

    public SstvTextLayer Normalized()
    {
      float x = float.IsFinite(X) ? Math.Clamp(X, 0f, .99f) : 0f;
      float y = float.IsFinite(Y) ? Math.Clamp(Y, 0f, .99f) : 0f;
      float size = float.IsFinite(SizePercent)
        ? Math.Clamp(SizePercent, 2f, 25f) : 6f;
      // ColorDialog always emits an opaque color. Preserve alpha from older
      // user data only for backward compatibility; never hide active text.
      return this with { X = x, Y = y, SizePercent = size };
    }
  }
}
