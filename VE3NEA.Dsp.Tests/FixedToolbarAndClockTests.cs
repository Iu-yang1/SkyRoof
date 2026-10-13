using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SkyRoof;
using VE3NEA.Clock;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class FixedToolbarAndClockTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(0.6)]
    public void ClockRemainsInsideItsClientRectangleAcrossScaleAndResize(double factor)
    {
        using var surface = new Panel { Size = new Size(117, 78), AutoScroll = false };
        using var clock = new ClockWidget
        {
            Dock = DockStyle.None,
            Bounds = new Rectangle(4, 3, 109, 70)
        };
        surface.Controls.Add(clock);
        using var zoom = new UniformZoomLayout(surface, surface.Size);
        zoom.Fit(new Size((int)Math.Round(117 * factor),
            (int)Math.Round(78 * factor)));

        Assert.False(surface.AutoScroll);
        Assert.Equal(AutoScaleMode.None, clock.AutoScaleMode);
        foreach (var label in clock.Controls.OfType<Label>())
        {
            Assert.False(label.AutoSize);
            Assert.True(label.Left >= 0 && label.Top >= 0);
            Assert.True(label.Right <= clock.ClientSize.Width + 1,
                $"Clock child {label.Name} reaches past client width");
            Assert.True(label.Bottom <= clock.ClientSize.Height + 1,
                $"Clock child {label.Name} reaches past client height");
        }

        zoom.Fit(new Size(80, 53));
        zoom.Fit(new Size(117, 78));
        Assert.Equal(new Rectangle(4, 3, 109, 70), clock.Bounds);
    }

    [Fact]
    public void ClockUsesUnambiguousInvariantDateAndExplicitZone()
    {
        var sample = new DateTime(2026, 10, 13, 6, 31, 52, DateTimeKind.Utc);
        Assert.Equal("06:31:52", ClockWidget.FormatTime(sample));
        Assert.Equal("2026-10-13 UTC", ClockWidget.FormatDate(sample, true));
        Assert.Equal("2026-10-13 LT", ClockWidget.FormatDate(sample, false));
    }

    [Fact]
    public void ExplicitUtcAndLocalSelectorsDoNotAccidentallyToggleWrongMode()
    {
        using var clock = new ClockWidget();
        bool oldMode = clock.UtcMode;
        try
        {
            var utc = clock.Controls.OfType<Label>().Single(x => x.Name == "utcLabel");
            var local = clock.Controls.OfType<Label>().Single(x => x.Name == "localLabel");
            clock.SetUtcMode(false);
            var utcClick = typeof(ClockWidget).GetMethod("utcLabel_Click",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var localClick = typeof(ClockWidget).GetMethod("localLabel_Click",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(utcClick);
            Assert.NotNull(localClick);
            utcClick!.Invoke(clock, new object[] { utc, EventArgs.Empty });
            Assert.True(clock.UtcMode);
            utcClick.Invoke(clock, new object[] { utc, EventArgs.Empty });
            Assert.True(clock.UtcMode);
            localClick!.Invoke(clock, new object[] { local, EventArgs.Empty });
            Assert.False(clock.UtcMode);
            Assert.Equal("Local", ClockWidget.ModeName);
        }
        finally { clock.SetUtcMode(oldMode); }
    }

    [Fact]
    public void FrozenToolbarFitsEachChildWithinDesignWidthAndNeverWrapsOnDockResize()
    {
        using var flow = new FlowLayoutPanel
        {
            Size = new Size(320, 250),
            Padding = new Padding(4),
            AutoScroll = false,
            WrapContents = true
        };
        for (int i = 0; i < 9; i++)
            flow.Controls.Add(new Button
            {
                Text = "Scope " + i,
                AutoSize = true,
                Margin = new Padding(4, 3, 4, 3)
            });
        using var frozen = DockUniformZoom.FreezeToolbar(flow, 320);
        Assert.False(frozen.AutoScroll);
        Assert.All(frozen.Controls.Cast<Control>(), child =>
        {
            Assert.False(child.AutoSize);
            Assert.True(child.Right <= 320 - frozen.Padding.Right);
            Assert.True(child.Bottom <= frozen.Height);
        });
        var baseline = frozen.Controls.Cast<Control>().Select(c => c.Bounds).ToArray();
        frozen.Size = new Size(150, frozen.Height);
        frozen.PerformLayout();
        Assert.Equal(baseline,
            frozen.Controls.Cast<Control>().Select(c => c.Bounds));
    }

    [Fact]
    public void VeryNarrowViewportStillGetsNonOverflowingZoomInsteadOfTenPercentFloor()
    {
        Assert.Equal(0.04f, UniformZoomLayout.GetScale(
            new Size(1000, 100), new Size(40, 10)), 4);
    }
}
