using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FluentAssertions;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcUiSmokeTests
  {
    [Fact]
    public void SpectrumRendersAndSelectsFrequencyAtSmallWindowsSize()
    {
      Exception? failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          using var view = new AfcSpectrumView();
          view.Size = new Size(685, 145);
          var points = new float[512];
          for (int i = 0; i < points.Length; i++)
            points[i] = (float)(-60 + 55 * Math.Exp(-Math.Pow((i - 280) / 18.0, 2)));
          view.SetSpectrum(points, -6000, 12000.0 / 511, 145, 145);

          using var image = new Bitmap(685, 145);
          view.DrawToBitmap(image, new Rectangle(0, 0, 685, 145));
          image.GetPixel(1, 1).A.Should().Be(255);

          double? selected = null;
          view.TargetSelected += value => selected = value;
          MethodInfo? mouse = typeof(AfcSpectrumView).GetMethod(
            "OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
          mouse.Should().NotBeNull();
          mouse!.Invoke(view, new object[] {
            new MouseEventArgs(MouseButtons.Left, 1, 365, 70, 0)
          });
          selected.Should().NotBeNull();
          selected!.Value.Should().BeInRange(-6000, 6000);
        }
        catch (Exception ex) { failure = ex; }
      });
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
      if (failure != null)
        throw new Xunit.Sdk.XunitException(failure.ToString());
    }
  }
}
