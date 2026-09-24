namespace SkyRoof
{
  /// <summary>
  /// Reassembles IC-9700 CI-V 27 00 scope waveform messages.
  ///
  /// The radio can expose scope data in two layouts:
  ///  - single-frame: sequence maximum 01, header + up to 475 pixels in one CI-V frame;
  ///  - multi-frame: sequence 01 carries wave-info, sequences 02..11 carry pixels.
  ///
  /// RS-BA1 Remote Utility commonly presents the multi-frame serial form even though
  /// the underlying transport is LAN, so the decoder must not assume that every LAN
  /// sweep is one 497-byte CI-V frame.
  /// </summary>
  internal sealed class IcomScopeAssembler
  {
    internal const int ScopePointCount = 475;
    internal const int MaximumSequence = 11;

    private readonly ScopeState[] States =
    {
      new ScopeState(),
      new ScopeState()
    };

    internal bool TryFeed(byte[] frame, out IcomScopeFrame? completed)
    {
      completed = null;

      if (!IsScopeFrame(frame))
        return false;

      if (frame.Length < 10 || frame[^1] != 0xFD)
        return false;

      byte receiver = frame[6];
      if (receiver > 1)
        return false;

      if (!TryDecodeBcdByte(frame[7], out int sequence) ||
          !TryDecodeBcdByte(frame[8], out int sequenceMaximum))
        return false;

      if (sequence < 1 ||
          sequenceMaximum < 1 ||
          sequence > sequenceMaximum ||
          sequenceMaximum > MaximumSequence)
        return false;

      ScopeState state = States[receiver];

      if (sequenceMaximum == 1)
      {
        state.Reset();

        if (sequence != 1 ||
            !TryReadWaveInfo(frame, out byte mode, out long frequencyA,
              out long frequencyB, out bool outOfRange))
          return false;

        if (outOfRange)
        {
          completed = BuildFrame(
            receiver, 1, 1, mode, frequencyA, frequencyB, true,
            new byte[ScopePointCount]);
          return true;
        }

        const int samplesStart = 21;
        int availableSamples = frame.Length - samplesStart - 1;
        if (availableSamples < ScopePointCount)
          return false;

        var samples = new byte[ScopePointCount];
        for (int i = 0; i < ScopePointCount; i++)
        {
          byte sample = frame[samplesStart + i];
          if (sample > 160)
            return false;

          samples[i] = sample;
        }

        completed = BuildFrame(
          receiver, 1, 1, mode, frequencyA, frequencyB, false, samples);
        return true;
      }

      if (sequence == 1)
      {
        state.Reset();

        if (!TryReadWaveInfo(frame, out byte mode, out long frequencyA,
              out long frequencyB, out bool outOfRange))
          return false;

        state.Active = true;
        state.SequenceMaximum = sequenceMaximum;
        state.ExpectedSequence = 2;
        state.Mode = mode;
        state.FrequencyAHz = frequencyA;
        state.FrequencyBHz = frequencyB;
        state.OutOfRange = outOfRange;
        state.StartedUtc = DateTime.UtcNow;

        if (outOfRange)
        {
          completed = BuildFrame(
            receiver, 1, (byte)sequenceMaximum, mode, frequencyA, frequencyB,
            true, new byte[ScopePointCount]);
          state.Reset();
        }

        return true;
      }

      if (!state.Active ||
          state.SequenceMaximum != sequenceMaximum ||
          state.ExpectedSequence != sequence ||
          (DateTime.UtcNow - state.StartedUtc).TotalSeconds > 1.0)
      {
        state.Reset();
        return false;
      }

      const int chunkStart = 9;
      int chunkLength = frame.Length - chunkStart - 1;
      if (chunkLength <= 0)
      {
        state.Reset();
        return false;
      }

      int room = ScopePointCount - state.Samples.Count;
      int take = Math.Min(room, chunkLength);

      for (int i = 0; i < take; i++)
      {
        byte sample = frame[chunkStart + i];
        if (sample > 160)
        {
          state.Reset();
          return false;
        }

        state.Samples.Add(sample);
      }

      if (sequence < sequenceMaximum)
      {
        state.ExpectedSequence++;
        return true;
      }

      if (state.Samples.Count < ScopePointCount)
      {
        state.Reset();
        return false;
      }

      byte[] completeSamples = state.Samples
        .Take(ScopePointCount)
        .ToArray();

      completed = BuildFrame(
        receiver,
        (byte)sequenceMaximum,
        (byte)sequenceMaximum,
        state.Mode,
        state.FrequencyAHz,
        state.FrequencyBHz,
        state.OutOfRange,
        completeSamples);

      state.Reset();
      return true;
    }

    internal static bool IsScopeFrame(byte[] frame) =>
      frame.Length >= 7 &&
      frame[0] == 0xFE &&
      frame[1] == 0xFE &&
      frame[4] == 0x27 &&
      frame[5] == 0x00;

    private static bool TryReadWaveInfo(
      byte[] frame,
      out byte mode,
      out long frequencyA,
      out long frequencyB,
      out bool outOfRange)
    {
      mode = 0;
      frequencyA = 0;
      frequencyB = 0;
      outOfRange = false;

      // FE FE <to> <from> 27 00 <rx> <seq> <seqMax>
      // <mode> <freqA:5> <freqB/span:5> <outOfRange> ... FD
      if (frame.Length < 22)
        return false;

      mode = frame[9];
      if (mode > 3)
        return false;

      if (!TryDecodeBcdFrequency(
            new ReadOnlySpan<byte>(frame, 10, 5),
            out frequencyA) ||
          !TryDecodeBcdFrequency(
            new ReadOnlySpan<byte>(frame, 15, 5),
            out frequencyB))
        return false;

      byte oor = frame[20];
      if (oor > 1)
        return false;

      outOfRange = oor != 0;
      return true;
    }

    private static IcomScopeFrame BuildFrame(
      byte receiver,
      byte divisionCurrent,
      byte divisionMaximum,
      byte mode,
      long frequencyA,
      long frequencyB,
      bool outOfRange,
      byte[] samples) =>
      new()
      {
        TimestampUtc = DateTime.UtcNow,
        Scope = receiver,
        DivisionCurrent = divisionCurrent,
        DivisionMaximum = divisionMaximum,
        Mode = mode,
        FrequencyAHz = frequencyA,
        FrequencyBHz = frequencyB,
        OutOfRange = outOfRange,
        Samples = samples
      };

    private static bool TryDecodeBcdByte(byte value, out int decoded)
    {
      int high = (value >> 4) & 0x0F;
      int low = value & 0x0F;

      if (high > 9 || low > 9)
      {
        decoded = 0;
        return false;
      }

      decoded = high * 10 + low;
      return true;
    }

    private static bool TryDecodeBcdFrequency(
      ReadOnlySpan<byte> bytes,
      out long value)
    {
      value = 0;
      long multiplier = 1;

      foreach (byte b in bytes)
      {
        int low = b & 0x0F;
        int high = (b >> 4) & 0x0F;

        if (low > 9 || high > 9)
        {
          value = 0;
          return false;
        }

        value += (low + high * 10L) * multiplier;
        multiplier *= 100;
      }

      return true;
    }

    private sealed class ScopeState
    {
      internal bool Active;
      internal int SequenceMaximum;
      internal int ExpectedSequence;
      internal byte Mode;
      internal long FrequencyAHz;
      internal long FrequencyBHz;
      internal bool OutOfRange;
      internal DateTime StartedUtc;
      internal readonly List<byte> Samples = new(ScopePointCount);

      internal void Reset()
      {
        Active = false;
        SequenceMaximum = 0;
        ExpectedSequence = 0;
        Mode = 0;
        FrequencyAHz = 0;
        FrequencyBHz = 0;
        OutOfRange = false;
        StartedUtc = default;
        Samples.Clear();
      }
    }
  }
}
