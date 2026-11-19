using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal sealed class InstalledApp
{
    internal readonly string Path;
    internal string Name;
    internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal InstalledApp(string path, string name)
    {
        Path = path;
        Name = name;
        Names.Add(name);
        Names.Add(System.IO.Path.GetFileNameWithoutExtension(path));
    }

    public override string ToString() { return Name; }
}

internal sealed class AppCatalog
{
    internal static readonly AppCatalog Empty = new AppCatalog(new InstalledApp[0]);
    internal readonly List<InstalledApp> Items;
    private readonly List<Tuple<InstalledApp, string>> aliases;
    private static readonly Regex Words = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex Prefix = new Regex(@"^(?:应用(?:名称)?|客户端|软件|app|application)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal AppCatalog(IEnumerable<InstalledApp> items)
    {
        Items = items.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        aliases = new List<Tuple<InstalledApp, string>>();
        foreach (InstalledApp app in Items)
        {
            foreach (string name in app.Names.Select(Normalize).Where(name => name.Length >= 2).Distinct())
                aliases.Add(Tuple.Create(app, name));
        }
    }

    internal InstalledApp ByPath(string path)
    {
        return Items.FirstOrDefault(app => string.Equals(app.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    internal InstalledApp MatchNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        string[] lines = Regex.Split(notes, @"[\r\n，,；;。]+").Select(line => Normalize(Prefix.Replace(line.Trim(), "")))
            .Where(line => line.Length > 0).Distinct().ToArray();
        if (lines.Length == 0) return null;
        int highest = 0;
        InstalledApp found = null;
        bool ambiguous = false;
        foreach (var item in aliases)
        {
            string name = item.Item2;
            int score = 0;
            foreach (string line in lines)
            {
                if (line == name) { score = 3; break; }
                if (name.Length < 4 && !HasCjk(name)) continue;
                if (ContainsPhrase(line, name))
                    score = Math.Max(score, 2);
                else if (name.Length >= 4 && HasCjk(name) && line.Contains(name))
                    score = Math.Max(score, 1);
            }
            if (score == 0) continue;
            if (score > highest)
            {
                found = item.Item1;
                highest = score;
                ambiguous = false;
            }
            else if (score == highest && found != item.Item1) ambiguous = true;
        }
        return ambiguous ? null : found;
    }

    private static bool HasCjk(string text)
    {
        return text.Any(c => c >= '\u3400' && c <= '\u9fff');
    }

    private static bool ContainsPhrase(string line, string name)
    {
        for (int index = line.IndexOf(name, StringComparison.Ordinal); index >= 0;
            index = line.IndexOf(name, index + 1, StringComparison.Ordinal))
        {
            int end = index + name.Length;
            if ((index == 0 || !char.IsLetterOrDigit(line[index - 1]) || (line[index - 1] >= '\u3400' && line[index - 1] <= '\u9fff')) &&
                (end == line.Length || !char.IsLetterOrDigit(line[end]) || (line[end] >= '\u3400' && line[end] <= '\u9fff')))
                return true;
        }
        return false;
    }

    private static string Normalize(string text)
    {
        return Words.Replace((text ?? "").Normalize(NormalizationForm.FormKC).ToUpperInvariant(), " ").Trim();
    }

    internal static AppCatalog Load()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    {
                        ReadRegistry(root, @"Software\Microsoft\Windows\CurrentVersion\App Paths", true, apps);
                        ReadRegistry(root, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", false, apps);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
                catch (IOException) { }
            }
        }

        ReadShortcuts(apps);
        AddRiot(apps);
        return new AppCatalog(apps.Values);
    }

    private static void ReadRegistry(RegistryKey root, string path, bool appPaths, Dictionary<string, InstalledApp> apps)
    {
        using (RegistryKey registry = root.OpenSubKey(path))
        {
            if (registry == null) return;
            foreach (string key in registry.GetSubKeyNames())
            {
                try
                {
                    using (RegistryKey entry = registry.OpenSubKey(key))
                    {
                        if (entry == null) continue;
                        string name = appPaths ? System.IO.Path.GetFileNameWithoutExtension(key)
                            : entry.GetValue("DisplayName") as string;
                        string executable = entry.GetValue(appPaths ? "" : "DisplayIcon") as string;
                        if (appPaths || !string.IsNullOrWhiteSpace(name))
                            Add(apps, executable, name);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
                catch (IOException) { }
            }
        }
    }

    private static void ReadShortcuts(Dictionary<string, InstalledApp> apps)
    {
        object shell = null;
        try
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            shell = Activator.CreateInstance(type);
            var paths = new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory };
            foreach (var path in paths)
            {
                string directory = Environment.GetFolderPath(path);
                if (!IsLocal(directory)) continue;
                foreach (string link in Shortcuts(directory, 0))
                {
                    object item = null;
                    try
                    {
                        item = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                        string target = item.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, item, null) as string;
                        Add(apps, target, System.IO.Path.GetFileNameWithoutExtension(link));
                    }
                    catch (COMException) { }
                    catch (TargetInvocationException) { }
                    finally { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                }
            }
        }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static IEnumerable<string> Shortcuts(string root, int depth)
    {
        string[] matches, branches;
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) yield break;
            matches = Directory.GetFiles(root, "*.lnk");
            branches = depth < 5 ? Directory.GetDirectories(root) : new string[0];
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (string link in matches) yield return link;
        foreach (string branch in branches)
            foreach (string link in Shortcuts(branch, depth + 1)) yield return link;
    }

    private static void AddRiot(Dictionary<string, InstalledApp> apps)
    {
        string manifest = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Riot Games\RiotClientInstalls.json");
        try
        {
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 256) return;
            var config = VaultFormat.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
            foreach (string key in new[] { "rc_live", "rc_default", "rc_beta" })
            {
                object value;
                if (!config.TryGetValue(key, out value)) continue;
                string launcher = Executable(value as string);
                if (launcher == null) continue;
                string directory = System.IO.Path.GetDirectoryName(launcher);
                string target = new[] { System.IO.Path.Combine(directory, @"RiotClientElectron\Riot Client.exe"),
                    System.IO.Path.Combine(directory, @"UX\RiotClientUx.exe"),
                    System.IO.Path.Combine(directory, "RiotClientUx.exe"), launcher }.FirstOrDefault(File.Exists);
                if (target == null) continue;
                if (!string.Equals(target, launcher, StringComparison.OrdinalIgnoreCase)) apps.Remove(launcher);
                InstalledApp riot = Add(apps, target, "Riot Client");
                if (riot == null) continue;
                riot.Name = "Riot Client";
                foreach (string name in new[] { "Riot Games", "RiotGames", "RiotClient", "拳头客户端", "拳头游戏" })
                    riot.Names.Add(name);
                return;
            }
        }
        catch (IOException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static InstalledApp Add(Dictionary<string, InstalledApp> apps, string candidate, string name)
    {
        string path = Executable(candidate);
        if (path == null) return null;
        string fileName = System.IO.Path.GetFileName(path);
        if (DesktopTarget.IsBrowser(fileName) || fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        InstalledApp app;
        if (!apps.TryGetValue(path, out app))
        {
            string nice = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(fileName) : name.Trim();
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info != null && !string.IsNullOrWhiteSpace(info.ProductName) &&
                    nice.Equals(System.IO.Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                    nice = info.ProductName.Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
            app = new InstalledApp(path, nice);
            apps.Add(path, app);
        }
        if (!string.IsNullOrWhiteSpace(name)) app.Names.Add(name.Trim());
        return app;
    }

    private static string Executable(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = Environment.ExpandEnvironmentVariables(raw.Trim());
        int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        int suffix = end + 4;
        if (suffix < value.Length && value[suffix] != '"' && value[suffix] != ',' && !char.IsWhiteSpace(value[suffix])) return null;
        value = value.Substring(0, suffix).TrimStart('"');
        if (!IsLocal(value) || !File.Exists(value)) return null;
        try { return System.IO.Path.GetFullPath(value); } catch (ArgumentException) { return null; }
    }

    private static bool IsLocal(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/')) return false;
        try
        {
            var driveType = new DriveInfo(path.Substring(0, 3)).DriveType;
            return driveType == DriveType.Fixed || driveType == DriveType.Removable;
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }
}
