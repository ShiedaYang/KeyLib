using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Automation;

internal sealed class DesktopTarget
{
    internal IntPtr Window;
    internal uint ProcessId;
    internal string Path;
    internal string Hash;
    internal string Name;
    internal long StartedTicks;

    internal static bool IsExtensionBrowser(string fileName)
    {
        return string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBrowser(string fileName)
    {
        string name = (fileName ?? "").ToLowerInvariant();
        return name == "chrome.exe" || name == "msedge.exe" || name == "firefox.exe" ||
            name == "brave.exe" || name == "opera.exe" || name == "vivaldi.exe";
    }

    internal static string ForegroundName(IntPtr window)
    {
        try
        {
            uint id;
            Native.GetWindowThreadProcessId(window, out id);
            if (id == 0) return "";
            using (Process process = Process.GetProcessById((int)id))
                return process.ProcessName + ".exe";
        }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
        catch (Win32Exception) { return ""; }
    }

    internal static DesktopTarget Inspect(IntPtr window, bool includeHash = true)
    {
        uint id;
        Native.GetWindowThreadProcessId(window, out id);
        if (id == 0 || id == Process.GetCurrentProcess().Id) return null;
        using (Process process = Process.GetProcessById((int)id))
        {
            string path = process.MainModule.FileName;
            if (IsBrowser(System.IO.Path.GetFileName(path))) return null;
            string name = process.MainModule.FileVersionInfo.ProductName;
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
            return new DesktopTarget { Window = window, ProcessId = id, Path = path, StartedTicks = process.StartTime.Ticks,
                Name = name.Length > 120 ? name.Substring(0, 120) : name, Hash = includeHash ? Fingerprint(path) : null };
        }
    }

    internal static string Fingerprint(string path)
    {
        using (var sha = SHA256.Create())
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
    }

    internal bool StillForeground()
    {
        uint id;
        Native.GetWindowThreadProcessId(Window, out id);
        return id == ProcessId && Native.GetForegroundWindow() == Window;
    }

    internal bool Matches(VaultEntry entry)
    {
        return string.Equals(entry.AppPath, Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.AppHash, Hash, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class DesktopFiller
{
    internal static async Task Pair(DesktopTarget target, VaultEntry entry, Func<bool> active)
    {
        Guard(target, active);
        var fields = await Task.Run(() => EditFields.Find(target.Window));
        Guard(target, active);
        if (fields.Username == null || fields.Password == null)
            throw new InvalidOperationException("无法唯一确认用户名框和密码框。");

        int focus = await Task.Run(() => FocusedField(fields));
        Guard(target, active);
        if (focus == 2)
        {
            if (!Native.TabInForeground(target.Window, true))
                throw new InvalidOperationException("无法移动到用户名框。");
            await Task.Delay(125);
        }
        else if (focus == 0)
        {
            await Task.Run(() =>
            {
                if (target.StillForeground()) fields.Username.SetFocus();
            });
            Guard(target, active);
        }

        if (!await MatchFocus(target, 1, active) ||
            !Native.TypeIntoForeground(target.Window, entry.User))
            throw new InvalidOperationException("未能填写用户名，请确认输入框焦点。");

        await Task.Delay(120);
        Guard(target, active);
        bool ready = await Task.Run(() =>
        {
            var fresh = EditFields.Find(target.Window);
            if (FocusedField(fresh) != 1) return false;
            object value;
            return !fresh.Username.TryGetCurrentPattern(ValuePattern.Pattern, out value) ||
                ((ValuePattern)value).Current.Value == entry.User;
        });
        Guard(target, active);
        if (!ready || !Native.TabInForeground(target.Window, false))
            throw new InvalidOperationException("请确认用户名已填好，再检查密码框的位置。");

        await Task.Delay(125);
        if (!await MatchFocus(target, 2, active) ||
            !Native.TypeIntoForeground(target.Window, entry.Password))
            throw new InvalidOperationException("未能确认密码框焦点，已停止填写。");
    }

    internal static async Task Password(DesktopTarget target, string value, Func<bool> active)
    {
        Guard(target, active);
        bool verified = await Task.Run(() =>
        {
            var root = AutomationElement.FromHandle(target.Window);
            var focused = AutomationElement.FocusedElement;
            if (root == null || focused == null || !focused.Current.IsPassword ||
                !focused.Current.IsEnabled || focused.Current.IsOffscreen)
                return false;
            for (var element = focused; element != null; element = TreeWalker.RawViewWalker.GetParent(element))
                if (Automation.Compare(element, root)) return true;
            return false;
        });
        Guard(target, active);
        if (!verified || !Native.TypeIntoForeground(target.Window, value))
            throw new InvalidOperationException("请先点选目标应用里的密码框，再生成并填写。");
    }

    private static async Task<bool> MatchFocus(DesktopTarget target, int expected, Func<bool> active)
    {
        Guard(target, active);
        int field = await Task.Run(() => FocusedField(EditFields.Find(target.Window)));
        Guard(target, active);
        return field == expected;
    }

    private static int FocusedField(EditFields fields)
    {
        AutomationElement focused = AutomationElement.FocusedElement;
        if (focused == null) return 0;
        if (fields.Username != null && Automation.Compare(fields.Username, focused)) return 1;
        if (fields.Password != null && Automation.Compare(fields.Password, focused)) return 2;
        return 0;
    }

    private static void Guard(DesktopTarget target, Func<bool> active)
    {
        if (!active() || !target.StillForeground())
            throw new OperationCanceledException("前台窗口已切换，已取消填写。");
    }
}
