namespace SkyRoof
{
  /// <summary>
  /// CI-V little-endian packed-BCD operating frequencies in integral Hz.
  /// IC-905 uses six bytes in the 10 GHz band; five bytes are insufficient.
  /// The 10 GHz scope's negative lower-edge sign is a special case and is
  /// deliberately opt-in, never accepted for normal operating frequencies.
  /// </summary>
  internal static class IcomCivFrequencyCodec
  {
    internal const int StandardByteCount = 5;
    internal const int ExtendedByteCount = 6;
    private const long MaximumSignedEdgeMagnitudeHz = 99_999_999_999L;

    internal static bool TryDecode(
      ReadOnlySpan<byte> data,
      out long frequencyHz,
      bool allowNegativeScopeEdge = false)
    {
      frequencyHz = 0;

      if (data.Length != StandardByteCount &&
          data.Length != ExtendedByteCount)
        return false;

      long multiplier = 1;
      long decoded = 0;
      bool negative = false;

      for (int i = 0; i < data.Length; i++)
      {
        int low = data[i] & 0x0F;
        int high = (data[i] >> 4) & 0x0F;

        if (low > 9)
          return false;

        // Official IC-905 10 GHz scope: the 100 GHz digit is F when
        // a lower edge is negative; all other digits encode its magnitude.
        if (i == ExtendedByteCount - 1 &&
            data.Length == ExtendedByteCount &&
            high == 0x0F &&
            allowNegativeScopeEdge)
        {
          negative = true;
          high = 0;
        }

        if (high > 9)
          return false;

        decoded += (low + high * 10L) * multiplier;
        multiplier *= 100;
      }

      if (negative &&
          (decoded == 0 || decoded > MaximumSignedEdgeMagnitudeHz))
        return false;

      frequencyHz = negative ? -decoded : decoded;
      return true;
    }

    internal static bool TryEncode(
      long frequencyHz,
      int byteCount,
      out byte[] encoded,
      bool allowNegativeScopeEdge = false)
    {
      encoded = Array.Empty<byte>();

      if (byteCount != StandardByteCount &&
          byteCount != ExtendedByteCount)
        return false;

      bool negative = frequencyHz < 0;
      if (negative && (!allowNegativeScopeEdge ||
          byteCount != ExtendedByteCount ||
          frequencyHz < -MaximumSignedEdgeMagnitudeHz))
        return false;

      long remaining = negative ? -frequencyHz : frequencyHz;
      byte[] buffer = new byte[byteCount];

      for (int i = 0; i < buffer.Length; i++)
      {
        long digits = remaining % 100;
        remaining /= 100;
        buffer[i] = (byte)(((digits / 10) << 4) | (digits % 10));
      }

      // Reject overflow instead of silently truncating the upper digits.
      if (remaining != 0)
        return false;

      if (negative)
        buffer[^1] = (byte)((buffer[^1] & 0x0F) | 0xF0);

      encoded = buffer;
      return true;
    }
  }

  /// <summary>
  /// Passive CI-V response address inspection. This never sends commands
  /// and never attempts to infer a unique radio model from an address.
  /// </summary>
  internal static class IcomCivReadOnlyProbe
  {
    internal static bool TryReadResponderAddress(
      ReadOnlySpan<byte> frame,
      byte controllerAddress,
      out byte responderAddress)
    {
      responderAddress = 0;

      if (frame.Length < 6 ||
          frame[0] != 0xFE ||
          frame[1] != 0xFE ||
          frame[^1] != 0xFD ||
          frame[2] != controllerAddress ||
          frame[3] == controllerAddress ||
          frame[3] is 0x00 or 0xFF)
        return false;

      responderAddress = frame[3];
      return true;
    }
  }
}
