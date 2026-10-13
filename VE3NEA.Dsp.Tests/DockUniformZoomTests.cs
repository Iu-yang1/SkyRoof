using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using SkyRoof;
using WeifenLuo.WinFormsUI.Docking;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class DockUniformZoomTests
{
    [Theory]
    [InlineData(800, 500, 1.0)]
    [InlineData(1000, 625, 1.25)]
    [InlineData(1200, 750, 1.5)]
    [InlineData(400, 250, 0.5)]
    public void DockChromeAndNativeVideoViewportFollowOneBaseline(
        int width, int height, double expectedScale)
    {
        using var window = new DockContent { ClientSize = new Size(800, 500) };
        using var design = new Panel { Size = new Size(800, 500) };
        using var video = new Panel { Dock = DockStyle.Fill };
        using var footer = new Label
        {
            Dock = DockStyle.Bottom, Height = 30, Font = new Font("Segoe UI", 10f)
        };
        using var header = new Label
        {
            Dock = DockStyle.Top, Height = 50, Font = new Font("Segoe UI", 12f)
        };
        design.Controls.Add(video);
        design.Controls.Add(footer);
        design.Controls.Add(header);
        window.Controls.Add(design);

        using var zoom = DockUniformZoom.Attach(window, design, new Size(800, 500));
        zoom.Viewport.Size = new Size(width, height);
        zoom.Viewport.PerformLayout();
        Assert.Equal(expectedScale, zoom.Scale, 2);
        Assert.Equal(width, design.Width);
        Assert.Equal(height, design.Height);
        Assert.Equal((int)Math.Round(50 * expectedScale), header.Height);
        Assert.Equal((int)Math.Round(30 * expectedScale), footer.Height);
        Assert.Equal((int)Math.Round(12 * expectedScale), header.Font.SizeInPoints, 0);
        Assert.True(video.ClientSize.Width > 0);
        Assert.Equal(height - header.Height - footer.Height, video.Height);
        Assert.False(zoom.Viewport.AutoScroll);
    }

    [Fact]
    public void RepeatedDockResizeNeverAccumulatesFontOrCoordinateDrift()
    {
        using var window = new DockContent { ClientSize = new Size(800, 500) };
        using var design = new Panel { Size = new Size(800, 500) };
        using var button = new Button
        {
            Bounds = new Rectangle(200, 100, 120, 40),
            Font = new Font("Segoe UI", 10f)
        };
        design.Controls.Add(button);
        window.Controls.Add(design);
        using var zoom = DockUniformZoom.Attach(window, design, new Size(800, 500));

        foreach (int width in new[] { 400, 1000, 600, 1200, 800, 320, 800 })
        {
            zoom.Viewport.Size = new Size(width, (int)Math.Round(width * 500.0 / 800.0));
            zoom.Viewport.PerformLayout();
        }
        Assert.Equal(new Rectangle(200, 100, 120, 40), button.Bounds);
        Assert.InRange(button.Font.SizeInPoints, 9.99f, 10.01f);
        Assert.False(zoom.Viewport.AutoScroll);
    }

    [Fact]
    public void IcomToolbarIsFrozenAfterItsOriginalOneTimeFlowLayout()
    {
        using var flow = new FlowLayoutPanel { Size = new Size(400, 200) };
        for (int i = 0; i < 8; i++)
            flow.Controls.Add(new Button { Size = new Size(110, 28) });
        using var fixedToolbar = DockUniformZoom.FreezeToolbar(flow, 400);
        Assert.Equal(8, fixedToolbar.Controls.Count);
        Assert.All(fixedToolbar.Controls.Cast<Control>(), child =>
            Assert.IsType<Button>(child));
        var positions = fixedToolbar.Controls.Cast<Control>().Select(c => c.Location).ToArray();
        fixedToolbar.Size = new Size(200, 100);
        fixedToolbar.PerformLayout();
        Assert.Equal(positions, fixedToolbar.Controls.Cast<Control>().Select(c => c.Location));
        Assert.False(fixedToolbar.AutoScroll);
    }

    [Fact]
    public void CwAndQsoPreserveFixedRowAndAbsoluteCardStructures()
    {
        using var cwRoot = CwConsolePanel.CreateSkimmerRootLayout();
        Assert.Equal(SizeType.Absolute, cwRoot.RowStyles[0].SizeType);
        Assert.Equal(SizeType.Absolute, cwRoot.RowStyles[1].SizeType);
        Assert.Equal(SizeType.Absolute, cwRoot.RowStyles[3].SizeType);
        Assert.Equal(SizeType.Absolute, cwRoot.RowStyles[4].SizeType);
        Assert.False(cwRoot.AutoScroll);

        using var qso = new QsoEntryPanel();
        var viewport = Assert.Single(qso.Controls.OfType<Panel>());
        Assert.False(viewport.AutoScroll);
        var design = Assert.Single(viewport.Controls.OfType<Panel>());
        Assert.DoesNotContain(design.Controls.Cast<Control>(), c => c is FlowLayoutPanel);
        Assert.Equal(12, design.Controls.Count);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void RotatorWheelSectorHitTestUsesItsScaledClientCoordinates(double factor)
    {
        using var card = new RotatorControlCard();
        card.SetDisplayScale((float)factor);
        var wheel = Assert.Single(card.Controls.OfType<RotatorDirectionWheel>());
        Assert.Equal((int)Math.Round(202 * factor), wheel.Width);
        Assert.Equal((int)Math.Round(202 * factor), wheel.Height);
        var method = typeof(RotatorDirectionWheel).GetMethod(
            "HitTest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        RotatorJogDirection At(Point p) =>
            (RotatorJogDirection)method!.Invoke(wheel, new object[] { p })!;
        int center = wheel.Width / 2;
        Assert.Equal(RotatorJogDirection.Stop, At(new Point(center, center)));
        Assert.Equal(RotatorJogDirection.AzimuthUp, At(new Point(wheel.Width - 20, center)));
        Assert.Equal(RotatorJogDirection.ElevationUp, At(new Point(center, 20)));
    }
}
