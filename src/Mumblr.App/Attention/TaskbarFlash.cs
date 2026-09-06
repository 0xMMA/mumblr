using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Mumblr.App.Attention;

/// <summary>
/// <c>FlashWindowEx</c> on the window's taskbar button. Windows flashes it a system-defined number
/// of times and then leaves it highlighted until the window comes to the front. No-op off Windows
/// and before the window has a native handle.
/// </summary>
internal static partial class TaskbarFlash
{
    private const uint FlashwStop = 0;
    private const uint FlashwAll = 3;
    private const uint FlashwTimerNoFg = 12;

    public static void Set(Window window, bool flashing)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        var info = new FlashInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
            hwnd = handle,
            dwFlags = flashing ? FlashwAll | FlashwTimerNoFg : FlashwStop,
        };

        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
