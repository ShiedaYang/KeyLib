using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class MainWindow : SoftWindow
{
    internal const string Shortcut = "Alt+C";
    private const int HotkeyId = 301;
    private readonly string vaultPath;
    private readonly string bridgePath;
    private readonly Panel content;
    private readonly Label status;
    private readonly Label activeShortcut;
    private readonly Timer idle;
    private readonly Native.ForegroundCallback foregroundCallback;
    private IntPtr hook;
    private VaultSession vault;
    private BrowserBridge bridge;
    private bool desktopHotkey;
    private bool browserForeground;
    private IntPtr routedWindow;
    private DateTime retryHotkeyAt;
    private int serial;
    private bool closed;
    private bool quitRequested;
    private PopupSession popup;
    private PopupInput popupInput;
    private int popupSerial;
    private AppCatalog catalog = AppCatalog.Empty;
    private Task<AppCatalog> pendingCatalog;
    private DateTime catalogReadAt;
    private readonly Dictionary<string, InstalledApp> autoLinks = new Dictionary<string, InstalledApp>();
    private readonly Dictionary<string, string> autoFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, long> shownProcesses = new Dictionary<uint, long>();
    private IntPtr observedForeground;
    private DateTime checkForegroundAt;
    private ListBox list;
    private TextBox search;
    private Panel details;
    private Label resultCount;

    internal MainWindow(string directory)
    {
        vaultPath = Path.Combine(directory, "vault.softvault");
        bridgePath = Path.Combine(directory, "browser-host.json");
        ClientSize = new Size(1042, 632);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "KeyLib · 本地密码库";
        var heading = Theme.Label("KeyLib", 69, 15, 210, 41, Theme.Heading, Theme.Ink);
        Controls.Add(new BrandMark { Bounds = new Rectangle(25, 18, 36, 36) });
        Controls.Add(heading);
        MakeDraggable(heading);
        AddWindowButtons(true);
        activeShortcut = Theme.Label("快捷键  " + Shortcut, 720, 18, 230, 35, Theme.BodyBold, Theme.Muted);
        activeShortcut.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(activeShortcut);
        Controls.Add(new Panel { Bounds = new Rectangle(0, 70, 1042, 1), BackColor = Theme.Line });
        content = new Panel { Bounds = new Rectangle(20, 89, 1002, 486) };
        Controls.Add(content);
        Controls.Add(new Panel { Bounds = new Rectangle(20, 592, 1002, 1), BackColor = Theme.Line });
        status = Theme.Label("", 24, 597, 994, 29, Theme.Small, Theme.Muted);
        Controls.Add(status);
        idle = new Timer { Interval = 100 };
        idle.Tick += delegate
        {
            if (popup != null && Native.GetForegroundWindow() != popup.TargetWindow) CancelPopup();
            RouteShortcut(false);
            if (vault != null && IdleMilliseconds() >= 5 * 60 * 1000) Lock("电脑闲置 5 分钟，密码库已锁定。");
            ObserveForeground();
            TryAutoPopup();
            if (vault != null && pendingCatalog == null && DateTime.UtcNow - catalogReadAt > TimeSpan.FromMinutes(2))
                LoadCatalogInBackground();
        };
        foregroundCallback = ForegroundChanged;
        Shown += OnStart;
        FormClosing += delegate(object sender, FormClosingEventArgs args)
        {
            if (args.CloseReason == CloseReason.UserClosing && !quitRequested)
            {
                args.Cancel = true;
                CancelPopup();
                WindowState = FormWindowState.Minimized;
                return;
            }
            closed = true;
            Lock("");
            if (popupInput != null) popupInput.Dispose();
            if (bridge != null) bridge.Dispose();
            bridge = null;
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            if (desktopHotkey) Native.UnregisterHotKey(Handle, HotkeyId);
            idle.Stop();
            idle.Dispose();
            SystemEvents.SessionSwitch -= SessionSwitched;
            SystemEvents.PowerModeChanged -= PowerChanged;
        };
        RenderLocked();
    }

    private void OnStart(object sender, EventArgs args)
    {
        popupInput = new PopupInput(Handle);
        hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
        idle.Start();
        SystemEvents.SessionSwitch += SessionSwitched;
        SystemEvents.PowerModeChanged += PowerChanged;
        if (File.Exists(bridgePath))
        {
            try { BrowserBridge.Register(bridgePath, Application.ExecutablePath); StartBridge(); }
            catch (Exception error) { Status(error.Message); }
        }
        RouteShortcut(true);
        LoadCatalogInBackground();
    }

    internal void ExitApplication()
    {
        quitRequested = true;
        Close();
    }

    private void QueueLock(string reason)
    {
        if (!closed && IsHandleCreated)
        {
            try { BeginInvoke(new Action(() => Lock(reason))); }
            catch (InvalidOperationException) { }
        }
    }

    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock) QueueLock("Windows 锁屏，密码库已锁定。");
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) QueueLock("系统休眠，密码库已锁定。");
    }

    private void RouteShortcut(bool force)
    {
        if (closed || !IsHandleCreated) return;
        IntPtr foreground = Native.GetForegroundWindow();
        if (force || routedWindow != foreground)
        {
            routedWindow = foreground;
            browserForeground = bridge != null && DesktopTarget.IsExtensionBrowser(DesktopTarget.ForegroundName(foreground));
        }
        if (browserForeground)
        {
            if (desktopHotkey) desktopHotkey = !Native.UnregisterHotKey(Handle, HotkeyId);
            activeShortcut.Text = "快捷键  " + Shortcut;
            return;
        }
        if (desktopHotkey || (!force && DateTime.UtcNow < retryHotkeyAt)) return;
        const int altNoRepeat = 0x4001;
        desktopHotkey = Native.RegisterHotKey(Handle, HotkeyId, altNoRepeat, 'C');
        retryHotkeyAt = DateTime.UtcNow.AddSeconds(2);
        activeShortcut.Text = "快捷键  " + Shortcut + (desktopHotkey ? "" : "（被占用）");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId)
        {
            RouteShortcut(false);
            if (!browserForeground) TogglePopup();
        }
        else if (message.Msg == PopupInput.MessageId && popupInput != null)
        {
            int action = message.WParam.ToInt32();
            if (!popupInput.IsReleasedMessage(action) && popup != null && popup.Token == message.LParam.ToInt32())
            {
                if (action == PopupInput.Cancel) CancelPopup();
                else if (action == PopupInput.Confirm && !popup.Busy) popup.View.ConfirmSelection();
            }
        }
        base.WndProc(ref message);
    }

    private void ClearContent()
    {
        AcceptButton = null;
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
        list = null;
        search = null;
        details = null;
        resultCount = null;
    }

    private void RenderLocked()
    {
        ClearContent();
        bool create = !File.Exists(vaultPath);
        var heading = Theme.Label(create ? "创建本地密码库" : "解锁密码库", 299, 60, 415, 43, Theme.Heading, Theme.Ink);
        content.Controls.Add(heading);
        content.Controls.Add(Theme.Label(create ? "设置至少 12 个字符的主密码。请妥善保管它。" :
            "输入主密码，查看已加密的账号。", 299, 110, 416, 32, Theme.Body, Theme.Muted));
        var first = AddInput(content, "主密码", 299, 160, 405, 256, true);
        TextBox confirmation = create ? AddInput(content, "确认主密码", 299, 233, 405, 256, true) : null;
        var unlock = AddAction(content, create ? "创建密码库" : "解锁", 299, create ? 318 : 244, 405, true, async () =>
        {
            if (first.Text.Length == 0) { Status("请输入主密码。"); return; }
            if (confirmation != null && first.Text != confirmation.Text) { Status("两次主密码不一致。"); return; }
            string password = first.Text;
            first.Clear();
            if (confirmation != null) confirmation.Clear();
            Control action = AcceptButton as Control;
            if (action != null) action.Enabled = false;
            int stamp = serial;
            Status("正在验证主密码…");
            try
            {
                var opened = await Task.Run(() => VaultSession.Open(vaultPath, password, create));
                if (closed || stamp != serial) { opened.Dispose(); return; }
                CancelPopup();
                vault = opened;
                RebuildAutoLinks();
                RenderVault();
                Status("密码库已解锁。");
            }
            catch (Exception error) { if (!closed && stamp == serial) Status(error.Message); }
            finally { if (action != null && !action.IsDisposed) action.Enabled = true; }
        });
        AcceptButton = unlock;
        ActiveControl = first;
    }

    private void RenderVault()
    {
        ClearContent();
        var navigation = new Panel { Bounds = new Rectangle(0, 0, 188, 486), BackColor = Theme.Sidebar };
        content.Controls.Add(navigation);
        navigation.Controls.Add(Theme.Label("密码库", 14, 11, 150, 29, Theme.BodyBold, Theme.Ink));
        var all = NavigationButton(navigation, "全部账号", 49, () => { search.Focus(); RefreshEntries(); });
        all.Current = true;
        navigation.Controls.Add(new Panel { Bounds = new Rectangle(14, 102, 160, 1), BackColor = Theme.Line });
        NavigationButton(navigation, "随机密码", 118, ShowGenerator);
        NavigationButton(navigation, "加密备份", 159, () =>
        {
            if (vault == null) return;
            using (var dialog = new BackupDialog(vault)) dialog.ShowDialog(this);
            if (vault != null) { RebuildAutoLinks(); RefreshEntries(); }
        });
        NavigationButton(navigation, "设置", 200, ShowSettings);
        NavigationButton(navigation, "锁定", 437, () => Lock("密码库已锁定。"));

        content.Controls.Add(Theme.Label("全部账号", 211, 0, 230, 36, Theme.Subheading, Theme.Ink));
        AddAction(content, "新建账号", 483, 2, 102, true, () => Edit(null, null, null));
        search = AddInput(content, "搜索账号", 211, 48, 374, 180, false);
        search.TextChanged += delegate { RefreshEntries(); };
        resultCount = Theme.Label("", 211, 110, 374, 26, Theme.Small, Theme.Muted);
        content.Controls.Add(resultCount);
        list = new ListBox { Bounds = new Rectangle(211, 143, 374, 343), Font = Theme.Body, BackColor = Theme.Surface,
            ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 59,
            IntegralHeight = false, AccessibleName = "账号列表" };
        list.DrawItem += DrawEntry;
        list.SelectedIndexChanged += delegate { RenderDetails(); };
        list.DoubleClick += delegate { EditSelected(); };
        content.Controls.Add(list);

        content.Controls.Add(new Panel { Bounds = new Rectangle(608, 2, 1, 483), BackColor = Theme.Line });
        details = new Panel { Bounds = new Rectangle(631, 0, 371, 486) };
        content.Controls.Add(details);
        RefreshEntries();
    }

    private static SoftButton NavigationButton(Control parent, string caption, int top, Action action)
    {
        var button = AddAction(parent, caption, 9, top, 170, false, action);
        button.Navigation = true;
        return button;
    }

    private void RefreshEntries()
    {
        if (list == null || vault == null) return;
        string term = search.Text.Trim();
        var previous = list.SelectedItem as VaultEntry;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (VaultEntry entry in vault.Entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!Contains(entry.Name, term) && !Contains(entry.User, term) &&
                    !Contains(entry.Origin, term) && !Contains(entry.AppPath, term) &&
                    !Contains(entry.Notes, term)) continue;
                int added = list.Items.Add(entry);
                if (previous != null && previous.Id == entry.Id) list.SelectedIndex = added;
            }
            if (list.Items.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
        }
        finally { list.EndUpdate(); }
        resultCount.Text = list.Items.Count + " 个账号";
        RenderDetails();
    }

    private void RenderDetails()
    {
        if (details == null || list == null) return;
        while (details.Controls.Count > 0) details.Controls[0].Dispose();
        VaultEntry entry = list.SelectedItem as VaultEntry;
        if (entry == null)
        {
            details.Controls.Add(Theme.Label("账号详情", 0, 0, 300, 36, Theme.Subheading, Theme.Ink));
            string empty = vault != null && vault.Entries.Count == 0
                ? "新建账号，或在目标应用里按 Alt+C 快速保存。"
                : "这个视图没有匹配的账号。";
            details.Controls.Add(Theme.Label(empty, 0, 53, 355, 46, Theme.Body, Theme.Muted));
            return;
        }

        var name = Theme.Label(entry.Name, 0, 0, 370, 37, Theme.Subheading, Theme.Ink);
        name.AutoEllipsis = true;
        details.Controls.Add(name);
        details.Controls.Add(new Panel { Bounds = new Rectangle(0, 79, 370, 1), BackColor = Theme.Line });
        AddDetail("用户名", entry.User, 95);
        Label password = AddDetail("密码", "••••••••••••", 155, 263);
        SoftButton reveal = null;
        reveal = AddAction(details, "显示", 285, 169, 85, false, () =>
        {
            bool masked = reveal.Text == "显示";
            password.Text = masked ? entry.Password : "••••••••••••";
            reveal.Text = masked ? "隐藏" : "显示";
            reveal.AccessibleName = reveal.Text;
        });
        int y = 215;
        if (!string.IsNullOrEmpty(entry.Origin)) { AddDetail("网站", entry.Origin, y); y += 57; }
        if (!string.IsNullOrEmpty(entry.AppPath)) { AddDetail("绑定应用", Path.GetFileName(entry.AppPath), y); y += 57; }
        else
        {
            InstalledApp automatic;
            if (autoLinks.TryGetValue(entry.Id, out automatic)) { AddDetail("备注匹配应用", automatic.Name, y); y += 57; }
        }
        if (!string.IsNullOrEmpty(entry.Notes)) AddDetail("备注", entry.Notes.Replace('\r', ' ').Replace('\n', ' '), y);
        AddAction(details, "编辑账号", 0, 432, 109, true, EditSelected);
        AddAction(details, "删除", 121, 432, 76, false, RemoveSelected);
    }

    private Label AddDetail(string label, string value, int top, int width = 370)
    {
        details.Controls.Add(Theme.Label(label, 0, top, width, 21, Theme.Small, Theme.Muted));
        var text = Theme.Label(string.IsNullOrEmpty(value) ? "—" : value, 0, top + 21, width, 27, Theme.Body, Theme.Ink);
        text.AutoEllipsis = true;
        details.Controls.Add(text);
        return text;
    }

    private static bool Contains(string value, string term)
    {
        return (value ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    private static void DrawEntry(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var entry = (VaultEntry)((ListBox)sender).Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? Theme.TintHover : Theme.Surface))
            e.Graphics.FillRectangle(fill, e.Bounds);
        int iconX = e.Bounds.X + 12, iconY = e.Bounds.Y + 12;
        using (var tile = Theme.Rounded(new Rectangle(iconX, iconY, 34, 34), 8))
        using (var fill = new SolidBrush(selected ? Theme.Surface : Theme.Tint))
        using (var edge = new Pen(Theme.Accent, 1.4f))
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, tile);
            e.Graphics.DrawEllipse(edge, iconX + 8, iconY + 11, 10, 10);
            e.Graphics.DrawLine(edge, iconX + 18, iconY + 16, iconX + 26, iconY + 16);
            e.Graphics.DrawLine(edge, iconX + 24, iconY + 16, iconX + 24, iconY + 20);
        }
        using (var line = new Pen(Theme.Line))
            e.Graphics.DrawLine(line, e.Bounds.Left + 56, e.Bounds.Bottom - 1, e.Bounds.Right - 2, e.Bounds.Bottom - 1);
        int left = e.Bounds.X + 57;
        int width = e.Bounds.Width - 68;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, entry.Name, Theme.BodyBold, new Rectangle(left, e.Bounds.Y + 8, width, 23), Theme.Ink, flags);
        string context = !string.IsNullOrEmpty(entry.Origin) ? entry.Origin :
            !string.IsNullOrEmpty(entry.AppPath) ? Path.GetFileNameWithoutExtension(entry.AppPath) : "";
        string subtitle = entry.User + (context.Length > 0 ? "  /  " + context : "");
        TextRenderer.DrawText(e.Graphics, subtitle, Theme.Small, new Rectangle(left, e.Bounds.Y + 32, width, 18), Theme.Muted, flags);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void EditSelected()
    {
        var entry = list == null ? null : list.SelectedItem as VaultEntry;
        if (entry != null) Edit(entry, null, null);
        else Status("请先选择账号。");
    }

    private void Edit(VaultEntry entry, DesktopTarget target, string generated)
    {
        if (vault == null) return;
        ShowMain();
        using (var dialog = new EntryDialog(entry, target, generated, catalog, RefreshCatalog))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK || vault == null) return;
            try
            {
                VaultEntry saved = dialog.Saved;
                if (entry == null && target != null)
                {
                    var duplicate = vault.Entries.FirstOrDefault(item => DetectPath(item, target.Path) && item.User == saved.User);
                    if (duplicate != null)
                    {
                        if (!ConfirmDialog.Ask(this, "保存账号", "更新此应用的同名账号？")) return;
                        if (vault == null) return;
                        saved.Id = duplicate.Id;
                        if (!dialog.ApplicationSelectionChanged && !string.IsNullOrEmpty(duplicate.AppPath))
                        {
                            saved.AppPath = duplicate.AppPath;
                            saved.AppHash = target.Hash;
                        }
                    }
                }
                vault.Save(saved);
                RebuildAutoLinks();
                RefreshEntries();
                Status("账号已加密保存。");
            }
            catch (Exception error) { Status(error.Message); }
        }
    }

    private void RemoveSelected()
    {
        var entry = list == null ? null : list.SelectedItem as VaultEntry;
        if (entry == null) return;
        if (!ConfirmDialog.Ask(this, "删除账号", "删除「" + entry.Name + "」？") || vault == null) return;
        try { vault.Remove(entry.Id); RebuildAutoLinks(); RefreshEntries(); Status("账号已删除。"); }
        catch (Exception error) { Status(error.Message); }
    }

    private void ShowGenerator()
    {
        using (var dialog = new GeneratorDialog())
            if (dialog.ShowDialog(this) == DialogResult.OK) Edit(null, null, dialog.Value);
    }

    private void ShowSettings()
    {
        using (var dialog = new SettingsDialog(bridge != null, enabled =>
        {
            if (enabled)
            {
                BrowserBridge.Register(bridgePath, Application.ExecutablePath);
                StartBridge();
            }
            else
            {
                if (bridge != null) bridge.Dispose();
                bridge = null;
                BrowserBridge.Unregister(bridgePath);
            }
            RouteShortcut(true);
        }, ExitApplication))
            dialog.ShowDialog(this);
    }

    private void StartBridge()
    {
        if (bridge == null)
            bridge = new BrowserBridge(request =>
            {
                if (closed || !IsHandleCreated) return new { error = "KeyLib未运行。" };
                return Invoke(new Func<object>(() => HandleBrowser(request)));
            });
    }

    private object HandleBrowser(BridgeRequest request)
    {
        if (closed || bridge == null || vault == null) return new { error = "请在KeyLib主窗口解锁密码库。" };
        if (request == null || request.origin == null || request.origin != VaultFormat.WebOrigin(request.origin))
            return new { error = "无法确认当前网站来源。" };
        var relevant = vault.Entries.Where(entry => entry.Origin == request.origin);
        try
        {
            switch (request.op)
            {
                case "list":
                    return new { items = relevant.Take(200).Select(entry => new { id = entry.Id, name = entry.Name, user = entry.User }).ToArray() };
                case "fill":
                    var found = relevant.FirstOrDefault(entry => entry.Id == request.id);
                    if (found != null) return new { user = found.User, password = found.Password };
                    break;
                case "generate":
                    return new { password = Passwords.Generate(request.length, request.symbols) };
                case "save":
                    var existing = relevant.FirstOrDefault(entry => entry.User == request.user);
                    if (existing != null && request.id != existing.Id)
                        return new { updateId = existing.Id };
                    var value = existing == null ? new VaultEntry { Id = Guid.NewGuid().ToString("N"), Name = new Uri(request.origin).Host,
                        Origin = request.origin } : existing.Copy();
                    value.User = request.user;
                    value.Password = request.password;
                    vault.Save(value);
                    RebuildAutoLinks();
                    RefreshEntries();
                    Status("网站账号已加密保存。");
                    return new { saved = true };
            }
        }
        catch (Exception error) { return new { error = error.Message }; }
        return new { error = "找不到与此网站匹配的条目。" };
    }

    private async void TogglePopup()
    {
        if (popup != null) { CancelPopup(); return; }
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        checkForegroundAt = DateTime.MaxValue;
        var active = OpenPopup(hwnd);
        if (active == null) return;
        if (vault == null)
        {
            active.View.Message("密码库已锁定", "请先输入主密码。", "解锁密码库", () => { CancelPopup(); ShowMain(); });
            return;
        }

        string processName = DesktopTarget.ForegroundName(hwnd);
        if (DesktopTarget.IsBrowser(processName))
        {
            active.View.Message("浏览器账号", DesktopTarget.IsExtensionBrowser(processName)
                ? "启用 KeyLib 本机连接并加载扩展，浏览器也用 Alt+C。"
                : "网页填写通过 Chrome 或 Edge 扩展使用。", null, null);
            return;
        }
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == currentProcessId)
        {
            active.View.Tools(() => { CancelPopup(); Edit(null, null, null); }, () => ShowPopupGenerator(active, null));
            return;
        }

        active.View.Message("正在识别应用…", "", null, null);
        try
        {
            DesktopTarget target = await Task.Run(() => DesktopTarget.Inspect(hwnd));
            if (!IsCurrent(active)) return;
            if (target == null) { active.View.Message("无法识别当前应用", "请返回支持的桌面窗口。", null, null); return; }
            ShowAccounts(active, target, MatchingEntries(target));
            if (vault.Entries.Any(entry => DetectPath(entry, target.Path))) MarkShown(target);
        }
        catch (Exception)
        {
            if (IsCurrent(active))
                active.View.Message("无法读取应用身份", "请在普通权限的桌面应用里重试。", null, null);
        }
    }

    private PopupSession OpenPopup(IntPtr target)
    {
        var active = new PopupSession(target, ++popupSerial);
        popup = active;
        active.View.FormClosed += delegate
        {
            if (popup != active) return;
            popup = null;
            popupInput.Stop();
        };
        if (!popupInput.Start(active.View.Handle, target, active.Token))
        {
            CancelPopup();
            Status("Windows 无法为快捷窗口接管 Esc 和回车，请重试。");
            return null;
        }
        active.View.Present(target);
        return active;
    }

    private void ShowAccounts(PopupSession active, DesktopTarget target, List<VaultEntry> matches)
    {
        bool changed = matches.Count == 0 && vault.Entries.Any(entry => DetectPath(entry, target.Path));
        InstalledApp known = catalog.ByPath(target.Path);
        active.View.Accounts(known == null ? target.Name : known.Name, matches, changed,
            entry => FillPair(active, target, entry),
            () => { CancelPopup(); Edit(null, target, null); }, () => ShowPopupGenerator(active, target));
    }

    private async void LoadCatalogInBackground()
    {
        try { await RefreshCatalog(); }
        catch (Exception) { /* Manual filling and previously selected app identities remain available. */ }
    }

    private async Task<AppCatalog> RefreshCatalog()
    {
        if (pendingCatalog != null) return await pendingCatalog;
        if (DateTime.UtcNow - catalogReadAt < TimeSpan.FromSeconds(15)) return catalog;
        catalogReadAt = DateTime.UtcNow;
        var scan = Task.Run(() => AppCatalog.Load());
        pendingCatalog = scan;
        try
        {
            catalog = await scan;
            if (!closed && vault != null)
            {
                RebuildAutoLinks();
                RenderDetails();
                if (popup == null) checkForegroundAt = DateTime.UtcNow.AddMilliseconds(350);
            }
            return catalog;
        }
        finally { pendingCatalog = null; }
    }

    private void RebuildAutoLinks()
    {
        autoLinks.Clear();
        if (vault == null) return;
        var resolvedNotes = new Dictionary<string, InstalledApp>();
        foreach (VaultEntry entry in vault.Entries)
        {
            if (entry.DisableAppDetection || !string.IsNullOrEmpty(entry.AppPath) || string.IsNullOrWhiteSpace(entry.Notes)) continue;
            InstalledApp found;
            if (!resolvedNotes.TryGetValue(entry.Notes, out found))
            {
                found = catalog.MatchNotes(entry.Notes);
                resolvedNotes.Add(entry.Notes, found);
            }
            if (found != null) autoLinks[entry.Id] = found;
        }
    }

    private bool DetectPath(VaultEntry entry, string path)
    {
        if (!string.IsNullOrEmpty(entry.AppPath))
            return string.Equals(entry.AppPath, path, StringComparison.OrdinalIgnoreCase);
        InstalledApp link;
        return autoLinks.TryGetValue(entry.Id, out link) && string.Equals(link.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    private List<VaultEntry> MatchingEntries(DesktopTarget target)
    {
        var matches = new List<VaultEntry>();
        foreach (VaultEntry entry in vault.Entries)
        {
            if (!DetectPath(entry, target.Path)) continue;
            if (!string.IsNullOrEmpty(entry.AppPath))
            {
                if (target.Matches(entry)) matches.Add(entry);
                continue;
            }
            string pinned;
            if (!autoFingerprints.TryGetValue(target.Path, out pinned))
            {
                pinned = target.Hash;
                autoFingerprints.Add(target.Path, pinned);
            }
            if (pinned == target.Hash) matches.Add(entry);
        }
        return matches;
    }

    private void ObserveForeground()
    {
        IntPtr window = Native.GetForegroundWindow();
        if (window == observedForeground) return;
        observedForeground = window;
        checkForegroundAt = DateTime.UtcNow.AddMilliseconds(350);
    }

    private async void TryAutoPopup()
    {
        if (closed || vault == null || popup != null || observedForeground == IntPtr.Zero ||
            DateTime.UtcNow < checkForegroundAt || vault.Entries.Count == 0)
            return;
        checkForegroundAt = DateTime.MaxValue;
        IntPtr window = observedForeground;
        VaultSession opened = vault;
        try
        {
            DesktopTarget target = await Task.Run(() => DesktopTarget.Inspect(window, false));
            if (target == null || vault != opened || popup != null || !target.StillForeground()) return;
            long alreadyShown;
            if (shownProcesses.TryGetValue(target.ProcessId, out alreadyShown) && alreadyShown == target.StartedTicks)
                return;
            if (!opened.Entries.Any(entry => DetectPath(entry, target.Path))) return;
            target.Hash = await Task.Run(() => DesktopTarget.Fingerprint(target.Path));
            if (vault != opened || popup != null || !target.StillForeground()) return;
            var matches = MatchingEntries(target);
            if (matches.Count == 0) return;
            MarkShown(target);
            var active = OpenPopup(window);
            if (active != null) ShowAccounts(active, target, matches);
        }
        catch (Exception) { /* Other applications can close or change permissions during inspection. */ }
    }

    private void MarkShown(DesktopTarget target)
    {
        if (shownProcesses.Count >= 1024) shownProcesses.Clear();
        shownProcesses[target.ProcessId] = target.StartedTicks;
    }

    private async void FillPair(PopupSession active, DesktopTarget target, VaultEntry entry)
    {
        if (!IsCurrent(active) || active.Busy) return;
        active.Busy = true;
        active.View.EnableActions(false);
        try
        {
            await DesktopFiller.Pair(target, entry, () => IsCurrent(active));
            if (IsCurrent(active)) { CancelPopup(); Status("已填写账号。请在目标应用确认。"); }
        }
        catch (Exception error)
        {
            if (IsCurrent(active)) active.View.Message("填写已停止", error.Message, null, null);
        }
        finally { active.Busy = false; }
    }

    private void ShowPopupGenerator(PopupSession active, DesktopTarget target)
    {
        if (!IsCurrent(active)) return;
        string generated = Passwords.Generate(24, true);
        Action fill = null;
        if (target != null)
            fill = async () =>
            {
                if (!IsCurrent(active) || active.Busy) return;
                active.Busy = true;
                active.View.EnableActions(false);
                try
                {
                    await DesktopFiller.Password(target, generated, () => IsCurrent(active));
                    if (IsCurrent(active)) active.View.SetHint("密码已发往当前密码框。可继续保存账号。");
                }
                catch (Exception error) { if (IsCurrent(active)) active.View.SetHint(error.Message); }
                finally
                {
                    if (IsCurrent(active)) active.View.EnableActions(true);
                    active.Busy = false;
                }
            };
        active.View.Generated(generated, () => ShowPopupGenerator(active, target),
            () => { CancelPopup(); Edit(null, target, generated); }, fill);
    }

    private bool IsCurrent(PopupSession active)
    {
        return !closed && vault != null && popup == active && Native.GetForegroundWindow() == active.TargetWindow;
    }

    private void ForegroundChanged(IntPtr handle, uint type, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        RouteShortcut(false);
        ObserveForeground();
        if (popup == null || window == IntPtr.Zero || window == popup.TargetWindow ||
            unchecked((int)(time - popup.OpenedAt)) < 0) return;
        CancelPopup();
    }

    private void CancelPopup()
    {
        PopupSession old = popup;
        popup = null;
        if (popupInput != null) popupInput.Stop();
        if (old != null && !old.View.IsDisposed) old.View.Close();
    }

    private void Lock(string info)
    {
        serial++;
        CancelPopup();
        if (vault != null) vault.Dispose();
        vault = null;
        autoLinks.Clear();
        autoFingerprints.Clear();
        shownProcesses.Clear();
        foreach (Form owned in OwnedForms) owned.Close();
        if (!closed)
        {
            RenderLocked();
            Status(info);
        }
    }

    private void ShowMain()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void Status(string text) { status.Text = text; }

    internal static TextBox AddInput(Control parent, string label, int x, int y, int width, int max, bool mask)
    {
        parent.Controls.Add(Theme.Label(label, x, y, width, 22, Theme.Small, Theme.Muted));
        var panel = new Panel { Bounds = new Rectangle(x, y + 23, width, 32), BackColor = Theme.Tint };
        var input = new TextBox { Bounds = new Rectangle(10, 6, width - 20, 23), Font = Theme.Body,
            BorderStyle = BorderStyle.None, BackColor = Theme.Tint, ForeColor = Theme.Ink,
            UseSystemPasswordChar = mask, MaxLength = max, AccessibleName = label };
        panel.Controls.Add(input);
        parent.Controls.Add(panel);
        return input;
    }

    internal static SoftButton AddAction(Control parent, string text, int x, int y, int width, bool primary, Action action)
    {
        var button = new SoftButton(text, primary) { Bounds = new Rectangle(x, y, width, 35) };
        button.Click += delegate { action(); };
        parent.Controls.Add(button);
        return button;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private readonly uint currentProcessId = GetCurrentProcessId();

    private static uint IdleMilliseconds()
    {
        var data = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        return GetLastInputInfo(ref data) ? unchecked((uint)Environment.TickCount - data.Time) : 0;
    }
}
