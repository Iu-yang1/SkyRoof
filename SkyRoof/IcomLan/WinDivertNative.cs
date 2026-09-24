using System.Runtime.InteropServices;

namespace SkyRoof
{
  internal static class WinDivertNative
  {
    internal const int LayerNetwork = 0;

    // WinDivert 2.x flags. SNIFF copies matching packets instead of removing them
    // from the network stack; RECV_ONLY makes accidental packet injection impossible.
    internal const ulong FlagSniff = 0x0001;
    internal const ulong FlagRecvOnly = 0x0004;

    internal const int ShutdownRecv = 0x1;
    internal const int ShutdownSend = 0x2;
    internal const int ShutdownBoth = ShutdownRecv | ShutdownSend;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport(
      "WinDivert.dll",
      EntryPoint = "WinDivertOpen",
      SetLastError = true,
      ExactSpelling = true,
      CallingConvention = CallingConvention.Cdecl,
      CharSet = CharSet.Ansi)]
    internal static extern IntPtr Open(
      [MarshalAs(UnmanagedType.LPStr)] string filter,
      int layer,
      short priority,
      ulong flags);

    [DllImport(
      "WinDivert.dll",
      EntryPoint = "WinDivertRecv",
      SetLastError = true,
      ExactSpelling = true,
      CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Recv(
      IntPtr handle,
      [Out] byte[] packet,
      uint packetLength,
      out uint recvLength,
      IntPtr address);

    [DllImport(
      "WinDivert.dll",
      EntryPoint = "WinDivertShutdown",
      SetLastError = true,
      ExactSpelling = true,
      CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shutdown(IntPtr handle, int how);

    [DllImport(
      "WinDivert.dll",
      EntryPoint = "WinDivertClose",
      SetLastError = true,
      ExactSpelling = true,
      CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Close(IntPtr handle);
  }
}
