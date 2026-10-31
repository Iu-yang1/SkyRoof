using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using SkyRoof.SSTV;
using VE3NEA.SkySSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvWaveReceiverTests
  {
    [Fact]
    public async Task OfflineRobot36WavCanBeDecodedWithoutRadioHardware()
    {
      string file = Path.Combine(Path.GetTempPath(),
        $"SkyRoof-SSTV-RX-{Guid.NewGuid():N}.wav");
      try
      {
        using var bitmap = new Bitmap(320, 240);
        using (var g = Graphics.FromImage(bitmap))
        {
          g.Clear(Color.White);
          g.FillRectangle(Brushes.Blue, 0, 0, 106, 240);
          g.FillRectangle(Brushes.Red, 107, 0, 106, 240);
          g.FillRectangle(Brushes.Green, 214, 0, 106, 240);
        }
        SstvWaveExporter.Export(bitmap, SstvMode.Robot36, file);

        SstvImageEvent? completed = null;
        await SstvWaveReceiver.DecodeAsync(file, evt =>
        {
          if (evt.Final && evt.ValidRows > 0) completed = evt;
        });

        Assert.NotNull(completed);
        Assert.Equal(SstvMode.Robot36, completed!.Mode);
        Assert.InRange(completed.ValidRows, 180, 240);
        Assert.Equal(320, completed.Image.Width);
        Assert.Equal(240, completed.Image.Height);
      }
      finally
      {
        try { File.Delete(file); } catch (IOException) { }
      }
    }
  }
}
