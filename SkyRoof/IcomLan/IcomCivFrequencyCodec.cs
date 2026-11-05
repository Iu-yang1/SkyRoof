namespace SkyRoof
{
  /// <summary>
  /// Strict CI-V little-endian packed-BCD frequency codec, in integral Hz.
  /// Standard IC-705/IC-9700 commands use 5 bytes. IC-905 also uses 6
  /// bytes for 10 GHz operation. This codec only handles unsigned
  /// operating frequencies; specialized signed offsets are separate formats.
  /// </summary>
  internal static class IcomCivFrequencyCodec
  {
    internal const int StandardByteCount = 5;
    internal const int ExtendedByteCount = 6;

    internal static bool TryDecode(
      ReadOnlySpan<byte> data, out long frequencyHz)
    {
      frequencyHz = 0;
      if (data.Length is not (StandardByteCount or ExtendedByteCount))
        return false;

      long multiplier = 1;
      long decoded = 0;

      foreach (byte item in data)
      {
        int low = item & 0x0F;
        int high = (item >> 4) & 0x0F;
        if (low > 9 || high > 9)
          return false;

        decoded += (low + high * 10L) * multiplier;
        multiplier *= 100;
      }

      frequencyHz = decoded;
      return true;
    }

    internal static bool TryEncode(
      long frequencyHz, int byteCount, out byte[] encoded)
    {
      encoded = Array.Empty<byte>();
      if (frequencyHz < 0 ||
          byteCount is not (StandardByteCount or ExtendedByteCount))
        return false;

      long remaining = frequencyHz;
      byte[] buffer = new byte[byteCount];

      for (int i = 0; i < buffer.Length; i++)
      {
        long pair = remaining % 100;
        remaining /= 100;
        buffer[i] = (byte)(((pair / 10) << 4) | (pair % 10));
      }

      // A 5-byte codec must reject 10 GHz instead of truncating it.
      if (remaining != 0)
        return false;

      encoded = buffer;
      return true;
    }
  }

  /// <summary>
  /// Examines an already-received CI-V response. This passive probe neither
  /// sends commands nor proves a radio model: CI-V addresses are editable.
  /// </summary>
  internal static class IcomCivReadOnlyProbe
  {
    internal static bool TryReadResponderAddress(
      ReadOnlySpan<byte> frame,
      byte controllerAddress,
      out byte responderAddress)
    {
      responderAddress = 0;
      if (frame.Length < 7 ||
          frame[0] != 0xFE ||
          frame[1] != 0xFE ||
          frame[^1] != 0xFD ||
          frame[2] != controllerAddress ||
          frame[3] == controllerAddress ||
          frame[3] is 0x00 or 0xFF ||
          frame[4] is 0xFA or 0xFB)
        return false;

      responderAddress = frame[3];
      return true;
    }
  }
}
