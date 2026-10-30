using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class ScopeOwnedContextMenuTests
{
  [Fact]
  public void SpectrumPanelUsesOwnedReusableMenu()
  {
    var field = typeof(IcomLanSpectrumPanel).GetField("MoreScopeMenu",
      BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.NotNull(field);
    Assert.Equal(typeof(ScopeOwnedContextMenu), field!.FieldType);
  }

  [Fact]
  public void RebuildAfterCloseDisposesOldItemsButNotMenu()
  {
    using var owner = new ScopeOwnedContextMenu();
    ToolStripMenuItem? old = null;

    for (int i = 0; i < 20; i++)
    {
      Assert.True(owner.Rebuild(menu =>
      {
        var parent = new ToolStripMenuItem("Select");
        parent.DropDownItems.Add("Option " + i);
        menu.Items.Add(parent);
        if (i == 0) old = parent;
      }));
      Assert.False(owner.Menu.IsDisposed);
      Assert.Single(owner.Menu.Items);
    }

    Assert.NotNull(old);
    Assert.True(old!.IsDisposed);
    owner.Dispose();
    Assert.True(owner.Menu.IsDisposed);
    Assert.False(owner.Rebuild(menu => menu.Items.Add("Disposed")));
  }

  [Fact]
  public void RepeatedWinFormsPopupCloseDoesNotDisposeOwner()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        using var form = new Form
        {
          Size = new Size(360, 160),
          StartPosition = FormStartPosition.Manual,
          Location = new Point(100, 100),
          ShowInTaskbar = false
        };
        using var trigger = new Button { Text = "MORE", Size = new Size(90, 30) };
        form.Controls.Add(trigger);
        using var owner = new ScopeOwnedContextMenu();

        int closed = 0;
        owner.Menu.Closed += (_, _) => closed++;
        form.Show();
        for (int i = 0; i < 5; i++)
        {
          int selected = i;
          Assert.True(owner.Rebuild(menu =>
          {
            menu.Items.Add("Entry " + selected);
            var submenu = new ToolStripMenuItem("Submenu");
            submenu.DropDownItems.Add("Option " + selected);
            menu.Items.Add(submenu);
          }));
          owner.Menu.Show(trigger, new Point(0, trigger.Height));
          Application.DoEvents();
          owner.Menu.Close();
          Application.DoEvents();
          Assert.False(owner.Menu.IsDisposed);
          Assert.Equal(2, owner.Menu.Items.Count);
        }
        Assert.True(closed >= 1);
        form.Close();
        owner.Dispose();
        Assert.True(owner.Menu.IsDisposed);
      }
      catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
      "WinForms popup must close without hanging the UI thread");
    if (failure != null)
      throw new Xunit.Sdk.XunitException(failure.ToString());
  }
}
