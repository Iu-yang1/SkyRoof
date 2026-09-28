using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace SkyRoof
{
  internal sealed class PttHotkeyController : IDisposable
  {
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly Context Ctx;
    private readonly LowLevelKeyboardProc Proc;
    private IntPtr Hook;
    private bool KeyHeld;
    private bool HotkeyAssertedPtt;
    private bool Disposed;

    internal PttHotkeyController(Context ctx)
    {
      Ctx = ctx;
      Proc = HookCallback;
      Install();
    }

    internal void UpdateSettings()
    {
      if (KeyHeld)
        ReleaseOwnedPtt();

      KeyHeld = false;
    }

    private void Install()
    {
      if (!OperatingSystem.IsWindows() || Hook != IntPtr.Zero)
        return;

      try
      {
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle =
          module == null
            ? IntPtr.Zero
            : GetModuleHandle(module.ModuleName);

        Hook = SetWindowsHookEx(
          WhKeyboardLl,
          Proc,
          moduleHandle,
          0);

        if (Hook == IntPtr.Zero)
          Log.Warning(
            $"Unable to install physical PTT keyboard hook (Win32 {Marshal.GetLastWin32Error()}).");
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Unable to install physical PTT keyboard hook.");
      }
    }

    private IntPtr HookCallback(
      int nCode,
      IntPtr wParam,
      IntPtr lParam)
    {
      if (nCode >= 0 && !Disposed)
      {
        Keys configured = Ctx.Settings.Cat.PttHotkey;
        if (configured != Keys.None)
        {
          int vkCode = Marshal.ReadInt32(lParam);
          Keys key = (Keys)vkCode;

          if (key == configured)
          {
            int message = wParam.ToInt32();

            if ((message == WmKeyDown || message == WmSysKeyDown) &&
                !KeyHeld)
            {
              KeyHeld = true;

              // If another client already has the rig keyed, the physical key
              // did not create this transmission and must not release it later.
              HotkeyAssertedPtt =
                Ctx.CatControl.Tx?.CanPtt() == true &&
                Ctx.CatControl.Tx.Ptt != true;

              if (HotkeyAssertedPtt)
                DispatchPtt(true);
            }
            else if ((message == WmKeyUp || message == WmSysKeyUp) &&
                     KeyHeld)
            {
              KeyHeld = false;

              if (HotkeyAssertedPtt)
                DispatchPttRelease();

              HotkeyAssertedPtt = false;
            }

            if (Ctx.Settings.Cat.SuppressPttHotkey)
              return (IntPtr)1;
          }
        }
      }

      return CallNextHookEx(Hook, nCode, wParam, lParam);
    }

    private void DispatchPtt(bool on)
    {
      MainForm? form = Ctx.MainForm;
      if (form == null || form.IsDisposed)
        return;

      try
      {
        form.BeginInvoke(() =>
        {
          if (Disposed) return;

          if (on)
          {
            if (Ctx.CatControl.Tx?.CanPtt() == true)
              Ctx.FrequencyControl.SetPtt(true);
          }
          else
          {
            Ctx.FrequencyControl.ReleaseApplicationPtt();
          }
        });
      }
      catch (InvalidOperationException)
      {
        // The UI is already tearing down; Dispose performs the final release.
      }
    }

    private void DispatchPttRelease()
    {
      MainForm? form = Ctx.MainForm;
      if (form == null || form.IsDisposed)
        return;

      try
      {
        form.BeginInvoke(() =>
        {
          if (Disposed) return;
          Ctx.FrequencyControl.ReleaseApplicationPtt();
        });
      }
      catch (InvalidOperationException)
      {
        // Dispose() performs the synchronous final release path.
      }
    }

    private void ReleaseOwnedPtt()
    {
      if (!HotkeyAssertedPtt)
        return;

      try
      {
        Ctx.FrequencyControl.ReleaseApplicationPtt();
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Unable to release physical-key PTT.");
      }
      finally
      {
        HotkeyAssertedPtt = false;
      }
    }

    public void Dispose()
    {
      if (Disposed) return;
      Disposed = true;

      if (KeyHeld && HotkeyAssertedPtt)
      {
        try
        {
          Ctx.FrequencyControl.SetPtt(false);
        }
        catch (Exception ex)
        {
          Log.Warning(ex, "Unable to release physical-key PTT during shutdown.");
        }
      }

      KeyHeld = false;
      HotkeyAssertedPtt = false;

      if (Hook != IntPtr.Zero)
      {
        _ = UnhookWindowsHookEx(Hook);
        Hook = IntPtr.Zero;
      }
    }

    private delegate IntPtr LowLevelKeyboardProc(
      int nCode,
      IntPtr wParam,
      IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
      int idHook,
      LowLevelKeyboardProc lpfn,
      IntPtr hMod,
      uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
      IntPtr hhk,
      int nCode,
      IntPtr wParam,
      IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
  }
}
