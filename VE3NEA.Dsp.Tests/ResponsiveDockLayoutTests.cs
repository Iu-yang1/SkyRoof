using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SkyRoof;
using VE3NEA.Clock;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class ResponsiveDockLayoutTests
{
    [Theory]
    [InlineData(380, 260)]
    [InlineData(580, 380)]
    [InlineData(760, 520)]
    public void FrequencyControlKeepsCompleteBaseAndTuningSections(int width, int height)
    {
        using var panel = new FrequencyControlPanel
        {
            Size = new Size(width, height)
        };
        panel.PerformLayout();

        var scrollHost = Assert.Single(panel.Controls.OfType<Panel>());
        var layout = Assert.Single(scrollHost.Controls.OfType<TableLayoutPanel>());
        scrollHost.PerformLayout();
        layout.PerformLayout();

        Assert.True(scrollHost.AutoScroll);
        Assert.True(layout.AutoSize);
        Assert.Equal(DockStyle.Top, layout.Dock);
        Assert.True(layout.Width >= 690);
        Assert.Equal(3, layout.RowCount);
        Assert.True(layout.GetRowHeights()[1] >= 206);
        Assert.True(layout.GetRowHeights()[2] >= 234);

        var baseGroup = layout.Controls.OfType<TableLayoutPanel>().Single();
        Assert.Equal(2, baseGroup.Controls.OfType<GroupBox>().Count());
        var tuning = layout.Controls.OfType<GroupBox>().Single();
        Assert.Equal("Tuning", tuning.Text);
        // Do not allow the second section to paint over buttons of the first.
        Assert.True(tuning.Top >= baseGroup.Bottom);
        Assert.Contains(
            baseGroup.Controls.OfType<GroupBox>().SelectMany(group =>
                group.Controls.OfType<TableLayoutPanel>()
                     .SelectMany(inner => inner.Controls.OfType<FlowLayoutPanel>())
                     .SelectMany(flow => flow.Controls.OfType<Button>())),
            button => button.Text == "Reset to Database");
    }

    [Theory]
    [InlineData(117, 71)]
    [InlineData(150, 90)]
    public void ClockUsesThreeDockedRowsInsteadOfOverlappingPixelPositions(
        int width, int height)
    {
        using var clock = new ClockWidget { Size = new Size(width, height) };
        var table = Assert.Single(clock.Controls.OfType<TableLayoutPanel>());
        Assert.Equal(DockStyle.Fill, table.Dock);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.Controls.Count);
        Assert.All(table.Controls.Cast<Control>(), c =>
            Assert.Equal(DockStyle.Fill, c.Dock));
        var modes = Assert.Single(table.Controls.OfType<TableLayoutPanel>());
        Assert.Equal(2, modes.Controls.Count);
    }

    [Fact]
    public void QsoEntryAllowsScrollableWrappingOnNarrowDockTabs()
    {
        using var entry = new QsoEntryPanel();
        var flow = Assert.Single(entry.Controls.OfType<FlowLayoutPanel>());
        Assert.True(flow.AutoScroll);
        Assert.True(flow.WrapContents);
        Assert.False(flow.AutoSize);
    }
    [Fact]
    public void ToolbarReservesRightSideForClockAndPttBeforeLeftOverflow()
    {
        using var toolbar = new Panel { Size = new Size(440, 95) };
        using var scrollable = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        using var transmit = new Panel { Dock = DockStyle.Right, Width = 112 };
        using var clock = new Panel { Dock = DockStyle.Right, Width = 117 };
        using var endSpacer = new Panel { Dock = DockStyle.Right, Width = 4 };
        toolbar.Controls.Add(scrollable);
        toolbar.Controls.Add(transmit);
        toolbar.Controls.Add(clock);
        toolbar.Controls.Add(endSpacer);
        toolbar.PerformLayout();

        Assert.True(scrollable.Right <= transmit.Left,
            "The scrolling strip must not paint under PTT");
        Assert.True(transmit.Right <= clock.Left,
            "PTT must not overlap the UTC clock");
        Assert.True(clock.Right <= endSpacer.Left,
            "Clock remains at the visible right edge");
    }

    [Fact]
    public void SkimmerRootUsesScrollableToolbarsAndFiveRows()
    {
        using var root = CwConsolePanel.CreateSkimmerRootLayout();
        Assert.Equal(5, root.RowCount);
        Assert.Equal(1, root.ColumnCount);
    }

}
