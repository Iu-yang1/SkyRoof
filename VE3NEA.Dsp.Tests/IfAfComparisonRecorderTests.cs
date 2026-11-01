using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using SkyRoof.IF;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfComparisonRecorderTests
  {
    [Fact]
    public async Task DualTrackWriterPreservesRawPcmAndTimestampMetadata()
    {
      string folder = Path.Combine(Path.GetTempPath(),
        "skyroof-if-af-test-" + Guid.NewGuid().ToString("N"));
      try
      {
        using var first = new IfAfWavTrack(folder, "if");
        using var second = new IfAfWavTrack(folder, "af");
        DateTime zero = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
        float[] ifPcm = Enumerable.Range(0, 1600)
          .Select(i => (float)(.4 * Math.Sin(2 * Math.PI * 12000 * i / 48000.0)))
          .ToArray();
        float[] afPcm = Enumerable.Range(0, 4800)
          .Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 1300 * i / 48000.0)))
          .ToArray();
        Assert.True(first.TryAppend(ifPcm, zero));
        Assert.True(first.TryAppend(ifPcm, zero.AddMilliseconds(33)));
        Assert.True(second.TryAppend(afPcm, zero.AddMilliseconds(11)));
        await Task.WhenAll(first.CompleteAsync(), second.CompleteAsync());

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(3200, first.WrittenSamples);
        Assert.Equal(4800, second.WrittenSamples);
        Assert.Equal(zero, first.FirstBlockUtc);
        Assert.Equal(zero.AddMilliseconds(33), first.LastBlockUtc);
        Assert.Equal(zero.AddMilliseconds(11), second.FirstBlockUtc);
        Assert.Equal(0, first.DroppedBlocks + second.DroppedBlocks);

        using var rIf = new WaveFileReader(first.WavPath);
        using var rAf = new WaveFileReader(second.WavPath);
        Assert.Equal(48000, rIf.WaveFormat.SampleRate);
        Assert.Equal(48000, rAf.WaveFormat.SampleRate);
        Assert.Equal(1, rIf.WaveFormat.Channels);
        Assert.Equal(1, rAf.WaveFormat.Channels);
        Assert.Equal(32, rIf.WaveFormat.BitsPerSample);
        Assert.Equal(32, rAf.WaveFormat.BitsPerSample);
        Assert.Equal(3200 * 4L, rIf.Length);
        Assert.Equal(4800 * 4L, rAf.Length);
        byte[] data = new byte[4];
        Assert.Equal(4, rAf.Read(data, 0, data.Length));
        Assert.InRange(Math.Abs(BitConverter.ToSingle(data)), 0, .00001);
        string[] csv = await File.ReadAllLinesAsync(first.TimePath);
        Assert.Equal(3, csv.Length);
        Assert.StartsWith("sample_offset,sample_count", csv[0]);
        Assert.StartsWith("0,1600,", csv[1]);
        Assert.StartsWith("1600,1600,", csv[2]);
      }
      finally
      {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
      }
    }

    [Fact]
    public async Task ClosedTrackDoesNotAcceptFurtherPcm()
    {
      string folder = Path.Combine(Path.GetTempPath(),
        "skyroof-if-af-test-" + Guid.NewGuid().ToString("N"));
      try
      {
        using var writer = new IfAfWavTrack(folder, "if");
        await writer.CompleteAsync();
        Assert.False(writer.TryAppend(new float[] { .1f }, DateTime.UtcNow));
        Assert.Equal(0, writer.WrittenSamples);
      }
      finally
      {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
      }
    }
  }
}
