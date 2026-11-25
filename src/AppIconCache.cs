using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

internal sealed class AppIconCache
{
    private const int Capacity = 192;
    private readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

    internal Bitmap Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        Bitmap icon;
        if (icons.TryGetValue(path, out icon)) return icon;

        if (icons.Count >= Capacity) Clear();
        icon = Load(path);
        icons.Add(path, icon);
        return icon;
    }

    internal void Clear()
    {
        foreach (Bitmap icon in icons.Values)
            if (icon != null) icon.Dispose();
        icons.Clear();
    }

    private static Bitmap Load(string path)
    {
        // A binding imported from another device may point to a share or to a missing file.
        if (path.Length < 4 || path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            DriveType drive = new DriveInfo(Path.GetPathRoot(path)).DriveType;
            if (drive == DriveType.Network || !File.Exists(path)) return null;
            IntPtr handle;
            ExtractIconEx(path, 0, out handle, IntPtr.Zero, 1);
            if (handle == IntPtr.Zero) return null;
            try
            {
                using (Icon icon = Icon.FromHandle(handle))
                    return icon.ToBitmap();
            }
            finally { DestroyIcon(handle); }
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }
        catch (ExternalException) { return null; }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out IntPtr large, IntPtr small, uint count);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
