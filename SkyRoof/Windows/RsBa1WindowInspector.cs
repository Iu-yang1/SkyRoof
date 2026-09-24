using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyRoof
{
  internal static class RsBa1WindowInspector
  {
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;

    internal sealed class WindowInfo
    {
      internal IntPtr Handle;
      internal IntPtr Parent;
      internal IntPtr Owner;
      internal uint ProcessId;
      internal string ProcessName = "";
      internal string Title = "";
      internal string ClassName = "";
      internal long Style;
      internal long ExStyle;
      internal bool Visible;
      internal Rectangle Bounds;
      internal uint Dpi;

      public override string ToString()
      {
        string title = string.IsNullOrWhiteSpace(Title) ? "<untitled>" : Title;
        string process = string.IsNullOrWhiteSpace(ProcessName) ? "?" : ProcessName;
        return $"{title}  [{process}, PID {ProcessId}, HWND {FormatHandle(Handle)}]";
      }
    }

    internal static List<WindowInfo> EnumerateTopLevelWindows(bool includeAll)
    {
      var result = new List<WindowInfo>();

      EnumWindows((hwnd, _) =>
      {
        var info = ReadWindow(hwnd);
        if (includeAll || IsRsBa1Candidate(info))
          result.Add(info);
        return true;
      }, IntPtr.Zero);

      return result
        .OrderByDescending(x => IsSpectrumScope(x))
        .ThenByDescending(x => IsRsBa1Candidate(x))
        .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    internal static WindowInfo ReadWindow(IntPtr hwnd)
    {
      GetWindowThreadProcessId(hwnd, out uint pid);

      string processName = "";
      if (pid != 0)
      {
        try
        {
          processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
          processName = "";
        }
      }

      uint dpi = 0;
      try
      {
        dpi = GetDpiForWindow(hwnd);
      }
      catch (EntryPointNotFoundException)
      {
        dpi = 0;
      }

      Rectangle bounds = Rectangle.Empty;
      if (GetWindowRect(hwnd, out RECT rect))
        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

      return new WindowInfo
      {
        Handle = hwnd,
        Parent = GetParent(hwnd),
        Owner = GetWindow(hwnd, GW_OWNER),
        ProcessId = pid,
        ProcessName = processName,
        Title = GetWindowTitle(hwnd),
        ClassName = GetWindowClass(hwnd),
        Style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64(),
        ExStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64(),
        Visible = IsWindowVisible(hwnd),
        Bounds = bounds,
        Dpi = dpi
      };
    }

    internal static List<WindowInfo> EnumerateDescendants(IntPtr root)
    {
      var result = new List<WindowInfo>();

      EnumChildWindows(root, (hwnd, _) =>
      {
        result.Add(ReadWindow(hwnd));
        return true;
      }, IntPtr.Zero);

      return result;
    }

    internal static bool IsSpectrumScope(WindowInfo info)
    {
      return info.Title.Equals("Spectrum Scope", StringComparison.OrdinalIgnoreCase) &&
             info.ClassName.Equals("TFormScope", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRsBa1Candidate(WindowInfo info)
    {
      if (IsSpectrumScope(info)) return true;

      if (info.Title.Contains("RS-BA1", StringComparison.OrdinalIgnoreCase) ||
          info.Title.Contains("Remote Control", StringComparison.OrdinalIgnoreCase) ||
          info.Title.Contains("Remote Controller", StringComparison.OrdinalIgnoreCase))
        return true;

      string p = info.ProcessName;
      return p.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
             p.Contains("icom", StringComparison.OrdinalIgnoreCase) ||
             p.Contains("rsba", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildWindowReport(IntPtr root)
    {
      var sb = new StringBuilder();
      WindowInfo rootInfo = ReadWindow(root);
      var descendants = EnumerateDescendants(root);

      sb.AppendLine("SkyRoof RS-BA1 Window Inspector");
      sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
      sb.AppendLine();
      AppendWindow(sb, rootInfo, 0);

      var byParent = descendants
        .GroupBy(x => x.Parent)
        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Handle.ToInt64()).ToList());

      AppendChildren(sb, root, byParent, 1, new HashSet<IntPtr>(), 0);
      return sb.ToString();
    }

    internal static string DescribeWindow(WindowInfo info)
    {
      var sb = new StringBuilder();
      AppendWindow(sb, info, 0);
      return sb.ToString().TrimEnd();
    }

    private static void AppendChildren(
      StringBuilder sb,
      IntPtr parent,
      Dictionary<IntPtr, List<WindowInfo>> byParent,
      int depth,
      HashSet<IntPtr> visited,
      int count)
    {
      if (depth > 12 || count > 500) return;
      if (!byParent.TryGetValue(parent, out var children)) return;

      foreach (var child in children)
      {
        if (!visited.Add(child.Handle)) continue;
        AppendWindow(sb, child, depth);
        AppendChildren(sb, child.Handle, byParent, depth + 1, visited, count + 1);
      }
    }

    private static void AppendWindow(StringBuilder sb, WindowInfo info, int depth)
    {
      string indent = new string(' ', depth * 2);
      sb.AppendLine($"{indent}HWND: {FormatHandle(info.Handle)}");
      sb.AppendLine($"{indent}  Title: {Printable(info.Title)}");
      sb.AppendLine($"{indent}  Class: {Printable(info.ClassName)}");
      sb.AppendLine($"{indent}  PID: {info.ProcessId}");
      sb.AppendLine($"{indent}  Process: {Printable(info.ProcessName)}");
      sb.AppendLine($"{indent}  Visible: {info.Visible}");
      sb.AppendLine($"{indent}  Parent: {FormatHandle(info.Parent)}");
      sb.AppendLine($"{indent}  Owner: {FormatHandle(info.Owner)}");
      sb.AppendLine($"{indent}  Style: 0x{unchecked((ulong)info.Style):X16}");
      sb.AppendLine($"{indent}  ExStyle: 0x{unchecked((ulong)info.ExStyle):X16}");
      sb.AppendLine(
        $"{indent}  Rect: {info.Bounds.X},{info.Bounds.Y} {info.Bounds.Width}x{info.Bounds.Height}");
      sb.AppendLine($"{indent}  DPI: {(info.Dpi == 0 ? "unknown" : info.Dpi.ToString())}");
      sb.AppendLine();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
      int length = GetWindowTextLength(hwnd);
      var sb = new StringBuilder(Math.Max(length + 1, 256));
      _ = GetWindowText(hwnd, sb, sb.Capacity);
      return sb.ToString();
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
      var sb = new StringBuilder(512);
      _ = GetClassName(hwnd, sb, sb.Capacity);
      return sb.ToString();
    }

    private static string Printable(string value)
    {
      return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
    }

    internal static string FormatHandle(IntPtr hwnd)
    {
      return hwnd == IntPtr.Zero ? "0x0000000000000000" : $"0x{hwnd.ToInt64():X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
      internal int Left;
      internal int Top;
      internal int Right;
      internal int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
  }
}
