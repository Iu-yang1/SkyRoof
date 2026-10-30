using System;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Owns a reusable scope toolbar popup for the entire panel lifetime.
  /// WinForms may still process closing and focus messages after Closed;
  /// disposing a ContextMenuStrip from its own Closed callback can therefore
  /// raise ObjectDisposedException on the UI thread.
  /// </summary>
  internal sealed class ScopeOwnedContextMenu : IDisposable
  {
    internal ContextMenuStrip Menu { get; } = new();

    /// <summary>
    /// Refreshes the choices while the popup is closed. This also disposes
    /// previous items and their nested drop-downs, avoiding accumulation of
    /// handlers each time the operator opens MORE.
    /// </summary>
    internal bool Rebuild(Action<ContextMenuStrip> populate)
    {
      ArgumentNullException.ThrowIfNull(populate);
      if (Menu.IsDisposed || Menu.Visible) return false;

      while (Menu.Items.Count > 0)
      {
        ToolStripItem previous = Menu.Items[0];
        Menu.Items.RemoveAt(0);
        previous.Dispose();
      }

      populate(Menu);
      return true;
    }

    public void Dispose() => Menu.Dispose();
  }
}
