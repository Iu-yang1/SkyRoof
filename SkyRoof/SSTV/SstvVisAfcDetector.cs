using System;
using System.Numerics;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Receive-side, self-contained standard 8-bit SSTV VIS header detector.
  /// Uses 5-ms zero-crossing pitch windows and checks both 1900-Hz leaders,
  /// the break/start intervals, all 8 VIS bits, stop bit and even parity
  /// before authorizing an AF offset. A generic spectral peak cannot lock AFC.
  /// No CAT writes, no transmit hardware and no GUI dependencies.
  /// </summary>
  public sealed class SstvVisAfcDetector
  {
    private const int Rate = 48000;
    private const int FrameSize = 240;
    private enum Stage { Searching, FirstLeader, Break, SecondLeader, Start, Data, Stop }
    private Stage stage;
    private readonly float[] frame = new float[FrameSize];
    private int fill, leaderCount, secondCount, shortCount, dataFrame, dataBit;
    private int dataByte, bitsRead, oneFrames, zeroFrames;
    private double leaderFreqSum, secondFreqSum;
    private int leaderFreqCount, secondFreqCount;
    private double measuredOffset;
    private long sampleCount;
    private long epoch;
    public event Action<SstvVisAfcLock>? VisLocked;

    public void Reset(long nextEpoch)
    {
      stage = Stage.Searching;
      epoch = nextEpoch;
      fill = leaderCount = secondCount = shortCount = dataFrame = dataBit = 0;
      bitsRead = oneFrames = zeroFrames = dataByte = 0;
      leaderFreqSum = secondFreqSum = 0;
      leaderFreqCount = secondFreqCount = 0;
      sampleCount = 0;
    }

    public void Process(ReadOnlySpan<float> audio)
    {
      for (int i = 0; i < audio.Length; i++)
      {
        frame[fill++] = audio[i];
        sampleCount++;
        if (fill == FrameSize)
        {
          fill = 0;
          double hz = EstimatePitch(frame);
          NextFrame(hz);
        }
      }
    }

    public static double EstimatePitch(ReadOnlySpan<float> block)
    {
      if (block.Length < 96) return double.NaN;
      double energy = 0;
      double first = -1, last = -1, previousPeriod = 0;
      double periodError = 0;
      int count = 0, checkedPeriods = 0;
      for (int n = 0; n < block.Length; n++)
      {
        float a = block[n];
        if (!float.IsFinite(a)) return double.NaN;
        energy += a * a;
        if (n == 0 || block[n - 1] > 0 || a <= 0) continue;
        double d = a - block[n - 1];
        if (d <= 0) continue;
        double crossing = n - 1 + (-block[n - 1]) / d;
        if (first < 0) first = crossing;
        if (last >= 0)
        {
          double period = crossing - last;
          if (previousPeriod > 0)
          {
            double rel = (period - previousPeriod) / previousPeriod;
            periodError += rel * rel;
            checkedPeriods++;
          }
          previousPeriod = period;
        }
        last = crossing;
        count++;
      }
      if (energy / block.Length < .0025 || count < 5 ||
          checkedPeriods < 3 || periodError / checkedPeriods > .025)
        return double.NaN;
      double hz = (count - 1) * Rate / (last - first);
      return hz is >= 900 and <= 2350 ? hz : double.NaN;
    }

    private static bool Near(double measured, double target, double tolerance = 130)
      => double.IsFinite(measured) && Math.Abs(measured - target) < tolerance;

    private void NextFrame(double hz)
    {
      switch (stage)
      {
        case Stage.Searching:
        case Stage.FirstLeader:
          if (Near(hz, 1900, 220))
          {
            stage = Stage.FirstLeader;
            leaderCount++;
            leaderFreqSum += hz;
            leaderFreqCount++;
          }
          else if (leaderCount >= 52 && Near(hz, 1200, 220))
          {
            stage = Stage.Break;
            shortCount = 1;
          }
          else ResetSearch();
          break;

        case Stage.Break:
          if (Near(hz, 1200, 220) && shortCount <= 4) shortCount++;
          else if (shortCount is >= 1 and <= 4 && Near(hz, 1900, 220))
          {
            stage = Stage.SecondLeader;
            secondCount = secondFreqCount = 1;
            secondFreqSum = hz;
          }
          else ResetSearch();
          break;

        case Stage.SecondLeader:
          if (Near(hz, 1900, 220))
          {
            secondCount++;
            secondFreqCount++;
            secondFreqSum += hz;
          }
          else if (secondCount >= 52 && Near(hz, 1200, 220))
          {
            measuredOffset =
              (leaderFreqSum / Math.Max(1, leaderFreqCount) +
               secondFreqSum / Math.Max(1, secondFreqCount)) / 2 - 1900;
            if (Math.Abs(measuredOffset) > 160)
              ResetSearch();
            else
            {
              stage = Stage.Start;
              shortCount = 1;
            }
          }
          else ResetSearch();
          break;

        case Stage.Start:
          if (Near(hz, 1200 + measuredOffset, 65) && shortCount <= 8)
            shortCount++;
          else if (shortCount >= 4 && NearBit(hz) != 0)
          {
            stage = Stage.Data;
            dataByte = dataFrame = bitsRead = oneFrames = zeroFrames = 0;
            AcceptDataFrame(hz);
          }
          else ResetSearch();
          break;

        case Stage.Data:
          AcceptDataFrame(hz);
          break;

        case Stage.Stop:
          if (Near(hz, 1200 + measuredOffset, 65))
          {
            shortCount++;
            if (shortCount >= 4)
            {
              // VIS parity byte: the most significant bit is XOR parity;
              // successful even parity across all 8 bits is mandatory.
              if ((BitOperations.PopCount((uint)dataByte) & 1) == 0)
                VisLocked?.Invoke(new SstvVisAfcLock(
                  dataByte, measuredOffset, epoch, sampleCount, 0.9));
              ResetSearch();
            }
          }
          else if (shortCount > 0 && !double.IsFinite(hz))
          {
            // A partial stop followed by silence is not a valid VIS frame.
            ResetSearch();
          }
          else ResetSearch();
          break;
      }
    }

    private int NearBit(double hz)
    {
      if (Near(hz, 1100 + measuredOffset, 100)) return 1;
      if (Near(hz, 1300 + measuredOffset, 100)) return 2;
      return 0;
    }

    private void AcceptDataFrame(double hz)
    {
      int tone = NearBit(hz);
      if (tone == 1) oneFrames++;
      if (tone == 2) zeroFrames++;
      dataFrame++;
      if (dataFrame != 6) return;
      if (oneFrames + zeroFrames < 4 ||
          Math.Abs(oneFrames - zeroFrames) < 2)
      {
        ResetSearch();
        return;
      }
      if (oneFrames > zeroFrames) dataByte |= (1 << bitsRead);
      bitsRead++;
      dataFrame = oneFrames = zeroFrames = 0;
      if (bitsRead == 8)
      {
        stage = Stage.Stop;
        shortCount = 0;
      }
    }

    private void ResetSearch()
    {
      stage = Stage.Searching;
      leaderCount = secondCount = shortCount = 0;
      leaderFreqSum = secondFreqSum = 0;
      leaderFreqCount = secondFreqCount = 0;
      dataFrame = oneFrames = zeroFrames = 0;
    }
  }

  public readonly record struct SstvVisAfcLock(
    int VisByte, double OffsetHz, long Epoch, long SampleCount,
    double Confidence);
}
