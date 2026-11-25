using System;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class WindowChrome
{
    internal const int BorderStyle = 0x00800000;
    internal static readonly bool SupportsRoundedFrame = IsWindows11();

    internal static bool TryRound(IntPtr window, bool small)
    {
        if (!SupportsRoundedFrame) return false;
        const int renderingPolicy = 2;
        const int cornerPreference = 33;
        const int borderColor = 34;
        int enabled = 2;
        if (DwmSetWindowAttribute(window, renderingPolicy, ref enabled, sizeof(int)) < 0)
            return false;
        int preference = small ? 3 : 2;
        if (DwmSetWindowAttribute(window, cornerPreference, ref preference, sizeof(int)) < 0)
            return false;

        Color outline = Theme.WindowOutline;
        int color = outline.R | (outline.G << 8) | (outline.B << 16);
        return DwmSetWindowAttribute(window, borderColor, ref color, sizeof(int)) >= 0;
    }

    private static bool IsWindows11()
    {
        var version = new VersionInfo { Size = (uint)Marshal.SizeOf(typeof(VersionInfo)) };
        return RtlGetVersion(ref version) == 0 && version.Build >= 22000;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct VersionInfo
    {
        internal uint Size;
        internal uint Major;
        internal uint Minor;
        internal uint Build;
        internal uint Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
    }

    [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref VersionInfo version);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
