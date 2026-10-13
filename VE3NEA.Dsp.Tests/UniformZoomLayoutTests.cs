using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class UniformZoomLayoutTests
{
    [Theory]
    [InlineData(760, 550, 760, 550, 1.0)]
    [InlineData(760, 550, 380, 275, 0.5)]
    [InlineData(760, 550, 570, 550, 0.75)]
    [InlineData(760, 550, 1520, 1100, 2.0)]
    public void UsesOneAspectPreservingFactorForBothDimensions(
        int baseWidth, int baseHeight, int width, int height, double expected)
    {
        float scale = UniformZoomLayout.GetScale(
            new Size(baseWidth, baseHeight), new Size(width, height));
        Assert.Equal(expected, scale, 2);
    }

    [Fact]
    public void RepeatedResizeUsesImmutableBaselineAndScalesFontsWithButtons()
    {
        using var surface = new Panel { Bounds = new Rectangle(0, 0, 800, 400) };
        using var button = new Button
        {
            Bounds = new Rectangle(200, 100, 120, 40),
            Font = new Font("Segoe UI", 12F)
        };
        surface.Controls.Add(button);
        using var zoom = new UniformZoomLayout(surface, new Size(800, 400));

        zoom.Fit(new Size(400, 200));
        Assert.Equal(new Rectangle(100, 50, 60, 20), button.Bounds);
        Assert.Equal(6f, button.Font.SizeInPoints, 1);

        zoom.Fit(new Size(600, 300));
        Assert.Equal(new Rectangle(150, 75, 90, 30), button.Bounds);
        Assert.Equal(9f, button.Font.SizeInPoints, 1);

        zoom.Fit(new Size(800, 400));
        Assert.Equal(new Rectangle(200, 100, 120, 40), button.Bounds);
        Assert.Equal(12f, button.Font.SizeInPoints, 1);
    }

    [Fact]
    public void UniformZoomHasNoScrollbarsWrappingOrContainerReparenting()
    {
        using var surface = new Panel { Size = new Size(600, 300) };
        using var button = new Button { Bounds = new Rectangle(440, 100, 120, 40) };
        using var clock = new Label { Bounds = new Rectangle(15, 10, 150, 30) };
        surface.Controls.Add(button);
        surface.Controls.Add(clock);
        using var zoom = new UniformZoomLayout(surface, surface.Size);
        zoom.Fit(new Size(300, 150));

        Assert.False(surface.AutoScroll);
        Assert.Same(surface, button.Parent);
        Assert.Same(surface, clock.Parent);
        Assert.True(button.Right <= surface.ClientSize.Width);
        Assert.True(clock.Right <= button.Left);
    }

    [Fact]
    public void FrequencyControlScalesDesignSurfaceInsteadOfReplacingItWithScrollView()
    {
        using var panel = new FrequencyControlPanel();
        var viewport = Assert.Single(panel.Controls.OfType<Panel>());
        Assert.False(viewport.AutoScroll);
        var design = Assert.Single(viewport.Controls.OfType<TableLayoutPanel>());
        Assert.Equal(DockStyle.None, design.Dock);
        Assert.Equal(3, design.RowCount);
        panel.Size = new Size(380, 275);
        viewport.PerformLayout();
        // DockContent.Size includes tab caption / non-client border, so its
        // actual viewport ClientSize is smaller than the assigned outer Size.
        // Assert the geometry against the real available client rectangle.
        float factor = UniformZoomLayout.GetScale(
            new Size(760, 550), viewport.ClientSize);
        Assert.InRange(design.Width,
            (int)Math.Round(760 * factor) - 1,
            (int)Math.Round(760 * factor) + 1);
        Assert.InRange(design.Height,
            (int)Math.Round(550 * factor) - 1,
            (int)Math.Round(550 * factor) + 1);
        Assert.False(viewport.AutoScroll);
    }
}
