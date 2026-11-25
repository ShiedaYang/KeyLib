using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class MainWindow : SoftWindow, IMessageFilter
{
    internal const string Shortcut = "Alt+C";
    private const int HotkeyId = 301;
    private const int WindowWidth = 1080;
    private const int WindowHeight = 640;
    private const int RailWidth = 174;
    private const int PageInset = 22;
    private readonly string vaultPath;
    private readonly string bridgePath;
    private readonly FocusPanel content;
    private readonly Label status;
    private readonly Label activeShortcut;
    private readonly Panel brandHeader;
    private readonly Panel railFooter;
    private readonly Panel footerLine;
    private readonly Label viewTitle;
    private readonly SoftButton addAccount;
    private readonly Icon brandIcon;
    private readonly Icon trayBrandIcon;
    private readonly NotifyIcon trayIcon;
    private readonly SoftMenu trayMenu;
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
    private readonly AppIconCache appIcons = new AppIconCache();
    private readonly Dictionary<string, string> autoFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, long> shownProcesses = new Dictionary<uint, long>();
    private IntPtr observedForeground;
    private DateTime checkForegroundAt;
    private SoftListBox list;
    private SoftListFrame accountRows;
    private TextBox search;
    private Panel details;
    private Panel accountTable;
    private Label sourceColumn;
    private Label resultCount;
    private Label emptyResults;
    private SoftButton clearSearch;
    private Label passwordValue;
    private SoftButton revealPassword;
    private EntryEditor inlineEditor;
    private bool creatingEntry;
    private DesktopTarget inlineTarget;
    private bool refreshingEntries;
    private bool detailsOpen;
    private const string MaskedPassword = "••••••••••••";

    internal MainWindow(string directory)
    {
        vaultPath = Path.Combine(directory, "vault.softvault");
        bridgePath = Path.Combine(directory, "browser-host.json");
        SetContentSize(WindowWidth, WindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "KeyLib · 本地密码库";
        brandIcon = BrandMark.CreateIcon(SystemInformation.IconSize);
        trayBrandIcon = BrandMark.CreateIcon(SystemInformation.SmallIconSize);
        Icon = brandIcon;
        trayMenu = new SoftMenu();
        trayMenu.Add("打开 KeyLib", ShowMain);
        trayMenu.Add("锁定密码库", () => Lock("密码库已锁定。"));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Add("退出 KeyLib", ExitApplication);
        trayIcon = new NotifyIcon { Icon = trayBrandIcon, Text = "KeyLib", ContextMenuStrip = trayMenu, Visible = true };
        trayIcon.MouseClick += delegate(object sender, MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left) ShowMain();
        };

        var header = new Panel { Bounds = new Rectangle(1, 1, WindowWidth - 2, 71), BackColor = Theme.Surface };
        brandHeader = new Panel { Bounds = new Rectangle(0, 0, RailWidth, header.Height), BackColor = Theme.Sidebar };
        var mark = new BrandMark { Bounds = new Rectangle(17, 19, 34, 34) };
        var heading = Theme.Label("KeyLib", 58, 16, 109, 40, Theme.Heading, Theme.Ink);
        brandHeader.Controls.Add(mark);
        brandHeader.Controls.Add(heading);
        header.Controls.Add(brandHeader);
        viewTitle = Theme.Label("", RailWidth + PageInset, 13, 440, 48, Theme.Heading, Theme.Ink);
        header.Controls.Add(viewTitle);
        addAccount = AddAction(header, "新建账号", 863, 18, 119, true, () => Edit(null, null, null));
        addAccount.LeadingIcon = "plus";
        MakeDraggable(header);
        MakeDraggable(brandHeader);
        MakeDraggable(mark);
        MakeDraggable(heading);
        MakeDraggable(viewTitle);
        AddWindowButtons(true, parent: header);
        activeShortcut = Theme.Label("快捷键  " + Shortcut, 645, 18, 201, 35, Theme.Small, Theme.Muted);
        activeShortcut.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(activeShortcut);
        MakeDraggable(activeShortcut);
        AddEdgePanel(header);
        Controls.Add(new Panel { Bounds = new Rectangle(1, 72, WindowWidth - 2, 1), BackColor = Theme.Line });
        content = new FocusPanel { Bounds = new Rectangle(1, 73, WindowWidth - 2, 527) };
        Controls.Add(content);
        railFooter = new Panel { Bounds = new Rectangle(1, 600, RailWidth, 39), BackColor = Theme.Sidebar };
        railFooter.Controls.Add(new Panel { Bounds = new Rectangle(RailWidth - 1, 0, 1, railFooter.Height), BackColor = Theme.Line });
        railFooter.Controls.Add(new Panel { Bounds = new Rectangle(14, 1, RailWidth - 28, 1), BackColor = Theme.Line });
        var lockVault = NavigationButton(railFooter, "锁定密码库", "lock", 3, () => Lock("密码库已锁定。"));
        lockVault.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        AddEdgePanel(railFooter);
        footerLine = new Panel { Bounds = new Rectangle(RailWidth + PageInset, 601, WindowWidth - RailWidth - 2 * PageInset, 1), BackColor = Theme.Line };
        Controls.Add(footerLine);
        status = Theme.Label("", RailWidth + PageInset, 607, WindowWidth - RailWidth - 2 * PageInset, 25, Theme.Small, Theme.Muted);
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
        Deactivate += delegate { HidePassword(); };
        FormClosing += delegate(object sender, FormClosingEventArgs args)
        {
            if (args.CloseReason == CloseReason.UserClosing && !quitRequested)
            {
                args.Cancel = true;
                CancelPopup();
                HidePassword();
                trayIcon.Visible = true;
                Hide();
                return;
            }
            closed = true;
            trayIcon.Visible = false;
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
        Application.AddMessageFilter(this);
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

    private void ExitApplication()
    {
        quitRequested = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        bool releaseTray = disposing && !IsDisposed;
        if (releaseTray && trayIcon != null)
        {
            Application.RemoveMessageFilter(this);
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
        }
        base.Dispose(disposing);
        if (releaseTray && brandIcon != null) brandIcon.Dispose();
        if (releaseTray && trayBrandIcon != null) trayBrandIcon.Dispose();
        if (releaseTray) appIcons.Clear();
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
                else if (!popup.Busy && Native.GetForegroundWindow() == popup.TargetWindow)
                {
                    if (action == PopupInput.Confirm) popup.View.ConfirmSelection();
                    else if (action == PopupInput.Previous) popup.View.MoveSelection(-1);
                    else if (action == PopupInput.Next) popup.View.MoveSelection(1);
                }
            }
        }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (inlineEditor != null && keyData == Keys.Escape)
        {
            CancelInlineEdit();
            return true;
        }
        if (search != null && keyData == (Keys.Control | Keys.F))
        {
            search.Focus();
            search.SelectAll();
            return true;
        }
        if (search != null && search.Focused)
        {
            if (keyData == Keys.Escape && search.TextLength > 0)
            {
                search.Clear();
                return true;
            }
            if ((keyData == Keys.Down || keyData == Keys.Enter) && list.Items.Count > 0)
            {
                list.Focus();
                return true;
            }
        }
        if (list != null && list.Focused && keyData == Keys.Enter)
        {
            OpenDetails();
            return true;
        }
        if (detailsOpen && keyData == Keys.Escape)
        {
            CloseDetails();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void ClearContent()
    {
        EndInlineEdit();
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
        list = null;
        accountRows = null;
        search = null;
        accountTable = details = null;
        sourceColumn = null;
        detailsOpen = false;
        resultCount = null;
        emptyResults = passwordValue = null;
        clearSearch = revealPassword = null;
    }

    private void RenderLocked()
    {
        ClearContent();
        brandHeader.Visible = railFooter.Visible = addAccount.Visible = footerLine.Visible = false;
        viewTitle.Text = "";
        content.BackColor = Theme.Sidebar;
        status.TextAlign = ContentAlignment.MiddleCenter;
        bool create = !File.Exists(vaultPath);
        var logo = new BrandMark { Bounds = new Rectangle((WindowWidth - 52) / 2, 44, 52, 52) };
        var name = Theme.Label("KeyLib", (WindowWidth - 240) / 2, 94, 240, 40, Theme.Heading, Theme.Ink);
        name.TextAlign = ContentAlignment.MiddleCenter;
        content.Controls.Add(logo);
        content.Controls.Add(name);
        var form = Theme.FramedPanel(new Rectangle((WindowWidth - 416) / 2, 155, 416, create ? 319 : 247), Theme.Surface);
        content.Controls.Add(form);
        form.Controls.Add(Theme.Label(create ? "创建本地密码库" : "解锁密码库", 28, 18, 360, 36, Theme.Subheading, Theme.Ink));
        form.Controls.Add(Theme.Label(create ? "设置至少 12 个字符的主密码。请妥善保管它。" :
            "输入主密码，查看已加密的账号。", 28, 57, 360, 28, Theme.Body, Theme.Muted));
        var first = AddInput(form, "主密码", 28, 96, 360, 256, true);
        TextBox confirmation = create ? AddInput(form, "确认主密码", 28, 168, 360, 256, true) : null;
        var unlock = AddAction(form, create ? "创建密码库" : "解锁", 28, create ? 252 : 180, 360, true, async () =>
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
        content.BackColor = Theme.Surface;
        brandHeader.Visible = railFooter.Visible = addAccount.Visible = footerLine.Visible = true;
        viewTitle.Text = "全部账号";
        status.TextAlign = ContentAlignment.MiddleLeft;
        var navigation = new Panel { Bounds = new Rectangle(0, 0, RailWidth, content.Height), BackColor = Theme.Sidebar };
        content.Controls.Add(navigation);
        navigation.Controls.Add(new Panel { Bounds = new Rectangle(RailWidth - 1, 0, 1, navigation.Height), BackColor = Theme.Line });
        NavigationButton(navigation, "密码库", "vault", 13, () =>
        {
            if (detailsOpen) CloseDetails();
            if (!detailsOpen) FocusBackground();
        }).Current = true;
        navigation.Controls.Add(Theme.Label("工具", 20, 82, 135, 24, Theme.Small, Theme.Muted));
        NavigationButton(navigation, "随机密码", "generator", 109, ShowGenerator);
        NavigationButton(navigation, "加密备份", "backup", 150, () =>
        {
            if (vault == null) return;
            using (var dialog = new BackupDialog(vault)) dialog.ShowDialog(this);
            if (vault != null) { RebuildAutoLinks(); RefreshEntries(); }
        });
        NavigationButton(navigation, "设置", "settings", 217, ShowSettings);

        int tableLeft = RailWidth + PageInset;
        accountTable = Theme.FramedPanel(new Rectangle(tableLeft, 17, WindowWidth - PageInset - tableLeft, content.Height - 32), Theme.Surface);
        content.Controls.Add(accountTable);
        AddFilterSearch(accountTable);
        resultCount = Theme.Label("", accountTable.Width - 130, 14, 112, 36, Theme.Small, Theme.Muted);
        resultCount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        resultCount.TextAlign = ContentAlignment.MiddleRight;
        accountTable.Controls.Add(resultCount);
        var columns = new Panel { Bounds = new Rectangle(1, 64, accountTable.Width - 2, 34), BackColor = Theme.Sidebar,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        columns.Controls.Add(Theme.Label("名称", 52, 0, 76, 34, Theme.BodyBold, Theme.Ink));
        sourceColumn = Theme.Label("网站 / 应用", 0, 0, 116, 34, Theme.BodyBold, Theme.Ink);
        columns.Controls.Add(sourceColumn);
        accountTable.Controls.Add(columns);
        list = new SoftListBox { Bounds = new Rectangle(1, 98, accountTable.Width - 2, accountTable.Height - 99), Font = Theme.Body,
            BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 57,
            IntegralHeight = false, AccessibleName = "账号列表" };
        list.DrawItem += DrawEntry;
        list.SelectedIndexChanged += delegate { if (!refreshingEntries) RenderDetails(); };
        list.MouseUp += OnEntryMouseUp;
        list.MouseMove += delegate(object sender, MouseEventArgs args)
        {
            list.Cursor = list.IndexFromPoint(args.Location) == ListBox.NoMatches ? Cursors.Default : Cursors.Hand;
        };
        list.GotFocus += delegate { list.Invalidate(); };
        list.LostFocus += delegate { list.Invalidate(); };
        accountRows = new SoftListFrame(list);
        accountTable.Controls.Add(accountRows);
        emptyResults = Theme.Label("", 18, 111, accountTable.Width - 36, 56, Theme.Body, Theme.Muted);
        emptyResults.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        accountTable.Controls.Add(emptyResults);

        details = Theme.FramedPanel(new Rectangle(WindowWidth - PageInset - 364, 17, 364, accountTable.Height), Theme.Surface);
        details.Visible = false;
        content.Controls.Add(details);
        LayoutAccountTable();
        RefreshEntries();
        FocusBackground();
    }

    private void AddFilterSearch(Panel toolbar)
    {
        var field = new SoftInputFrame { Bounds = new Rectangle(16, 14, 310, 36), FillColor = Theme.Surface };
        toolbar.Controls.Add(field);
        field.Paint += delegate(object sender, PaintEventArgs args)
        {
            Theme.DrawIcon(args.Graphics, "search", new Rectangle(8, 11, 14, 14), Theme.Muted);
        };
        search = new SoftTextBox { Bounds = new Rectangle(27, 8, field.Width - 56, 23), Font = Theme.Body,
            BorderStyle = BorderStyle.None, MaxLength = 180, ForeColor = Theme.Ink, BackColor = Theme.Surface,
            AccessibleName = "搜索账号" };
        search.HandleCreated += delegate { SetCueBanner(search.Handle, 0x1501, IntPtr.Zero, "搜索账号"); };
        search.GotFocus += delegate { field.Invalidate(); };
        search.LostFocus += delegate { field.Invalidate(); };
        field.Controls.Add(search);
        clearSearch = new SoftButton("清除搜索", false, "cross") { Bounds = new Rectangle(field.Width - 28, 5, 25, 26) };
        clearSearch.Click += delegate { search.Clear(); search.Focus(); };
        field.Controls.Add(clearSearch);
        search.TextChanged += delegate { RefreshEntries(); };
        field.MouseDown += delegate { search.Focus(); };
    }

    private static SoftButton NavigationButton(Control parent, string caption, string icon, int top, Action action)
    {
        var button = AddAction(parent, caption, 9, top, parent.ClientSize.Width - 18, false, action);
        button.Navigation = true;
        button.LeadingIcon = icon;
        return button;
    }

    private void OnEntryMouseUp(object sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
        int index = list.IndexFromPoint(args.Location);
        if (index == ListBox.NoMatches || !list.GetItemRectangle(index).Contains(args.Location)) return;
        list.SelectedIndex = index;
        OpenDetails();
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        const int leftButtonDown = 0x0201;
        if (message.Msg != leftButtonDown || vault == null || details == null) return false;
        Control clicked = Control.FromHandle(message.HWnd);
        if (clicked == null || clicked.FindForm() != this || clicked == details || details.Contains(clicked))
            return false;

        if (clicked == list)
        {
            int packed = message.LParam.ToInt32();
            var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
            int row = list.IndexFromPoint(point);
            if (row >= 0 && list.GetItemRectangle(row).Contains(point)) return false;
        }
        else if (!(clicked is Panel) && !(clicked is Label) && clicked != this)
            return false;

        if (detailsOpen) CloseDetails();
        else FocusBackground();
        // Prevent the native list box from focusing and selecting its last row on a blank click.
        return clicked == list;
    }

    private void OpenDetails()
    {
        if (list.SelectedIndex < 0) return;
        detailsOpen = true;
        details.Visible = true;
        LayoutAccountTable();
        RenderDetails();
    }

    private void CloseDetails()
    {
        if (inlineEditor != null && inlineEditor.HasChanges)
        {
            inlineEditor.ShowError("请先保存或取消修改。");
            return;
        }
        EndInlineEdit();
        detailsOpen = false;
        details.Visible = false;
        LayoutAccountTable();
        RenderDetails();
        FocusBackground();
    }

    private void FocusBackground()
    {
        ActiveControl = content;
        content.Focus();
    }

    private void LayoutAccountTable()
    {
        int right = detailsOpen ? details.Left - 15 : content.ClientSize.Width - PageInset;
        accountTable.Width = right - accountTable.Left;
        AlignAccountColumns();
        sourceColumn.Visible = !detailsOpen;
        list.Invalidate();
    }

    private void AlignAccountColumns()
    {
        sourceColumn.Left = SourceColumnX(list.ClientSize.Width);
    }

    private static int SourceColumnX(int tableWidth) { return tableWidth * 59 / 100; }

    private void RefreshEntries(string selectedId = null)
    {
        if (list == null || vault == null) return;
        string[] terms = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (selectedId == null && list.SelectedItem is VaultEntry)
            selectedId = ((VaultEntry)list.SelectedItem).Id;
        int selectedIndex = -1;
        refreshingEntries = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (VaultEntry entry in vault.Entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!MatchesSearch(entry, terms)) continue;
                int added = list.Items.Add(entry);
                if (selectedId == entry.Id) selectedIndex = added;
            }
            if (list.Items.Count > 0) list.SelectedIndex = Math.Max(selectedIndex, 0);
        }
        finally
        {
            list.EndUpdate();
            refreshingEntries = false;
        }
        clearSearch.Visible = search.TextLength > 0;
        resultCount.Text = list.Items.Count + " 个账号";
        bool empty = list.Items.Count == 0;
        accountRows.Visible = !empty;
        accountRows.UpdateScroll();
        AlignAccountColumns();
        emptyResults.Visible = empty;
        if (empty)
            emptyResults.Text = terms.Length > 0 ? "没有匹配结果。试试其他关键词。" :
                "新建账号，或在目标应用里按 Alt+C 快速保存。";
        if (empty && detailsOpen && inlineEditor == null) CloseDetails();
        else RenderDetails();
    }

    private bool MatchesSearch(VaultEntry entry, string[] terms)
    {
        if (terms.Length == 0) return true;
        string app = AppDisplayName(entry);
        return terms.All(term => Contains(entry.Name, term) || Contains(entry.User, term) ||
            Contains(entry.Origin, term) || Contains(entry.AppPath, term) ||
            Contains(entry.Notes, term) || Contains(app, term));
    }

    private string AppDisplayName(VaultEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.AppPath))
        {
            InstalledApp bound = catalog.ByPath(entry.AppPath);
            return bound == null ? Path.GetFileName(entry.AppPath) : bound.Name;
        }
        InstalledApp match;
        return autoLinks.TryGetValue(entry.Id, out match) ? match.Name : "";
    }

    private void RenderDetails()
    {
        if (details == null || list == null) return;
        if (inlineEditor != null)
        {
            inlineEditor.UpdateCatalog(catalog);
            return;
        }
        ClearDetailControls();
        if (!detailsOpen) return;
        VaultEntry entry = list.SelectedItem as VaultEntry;
        if (entry == null) return;

        int innerWidth = details.ClientSize.Width - 32;
        int footerTop = details.ClientSize.Height - 54;
        RenderDetailHeading(entry.Name);

        var credentials = new Panel { Bounds = new Rectangle(16, 77, innerWidth, 130), BackColor = Theme.Sidebar };
        details.Controls.Add(credentials);
        AddDetail(credentials, "用户名", entry.User, 12, 8, innerWidth - 24);
        credentials.Controls.Add(new Panel { Bounds = new Rectangle(12, 62, innerWidth - 24, 1), BackColor = Theme.Line });
        passwordValue = AddDetail(credentials, "密码", MaskedPassword, 12, 73, innerWidth - 116);
        revealPassword = AddAction(credentials, "显示", innerWidth - 82, 83, 70, false, () =>
        {
            if (revealPassword.Text == "隐藏") { HidePassword(); return; }
            passwordValue.Text = entry.Password;
            revealPassword.Text = "隐藏";
            revealPassword.AccessibleName = "隐藏密码";
        });
        revealPassword.AccessibleName = "显示密码";
        int y = 224;
        if (!string.IsNullOrEmpty(entry.Origin)) { AddDetail(details, "网站", entry.Origin, 16, y, innerWidth); y += 52; }
        string app = AppDisplayName(entry);
        if (app.Length > 0)
        {
            AddApplicationDetail(entry, app, y, innerWidth);
            y += 84;
        }
        if (!string.IsNullOrWhiteSpace(entry.Notes))
        {
            int height = Math.Min(94, footerTop - y - 32);
            details.Controls.Add(Theme.Label("备注", 16, y, innerWidth, 21, Theme.Small, Theme.Muted));
            details.Controls.Add(new SoftNoteFrame(new Rectangle(16, y + 23, innerWidth, height), entry.Notes));
        }
        details.Controls.Add(new Panel { Bounds = new Rectangle(1, footerTop, details.Width - 2, 1), BackColor = Theme.Line });
        const int actionGap = 12;
        int actionWidth = (innerWidth - actionGap) / 2;
        var edit = AddAction(details, "编辑", 16, footerTop + 10, actionWidth, true, EditSelected);
        edit.AccessibleName = "编辑账号";
        var delete = AddAction(details, "删除", 16 + actionWidth + actionGap, footerTop + 10,
            innerWidth - actionWidth - actionGap, false, RemoveSelected);
        delete.AccessibleName = "删除账号";
        delete.Destructive = true;
    }

    private void HidePassword()
    {
        if (inlineEditor != null) inlineEditor.HidePassword();
        if (passwordValue == null) return;
        passwordValue.Text = MaskedPassword;
        revealPassword.Text = "显示";
        revealPassword.AccessibleName = "显示密码";
    }

    private static Label AddDetail(Control parent, string label, string value, int x, int top, int width)
    {
        parent.Controls.Add(Theme.Label(label, x, top, width, 20, Theme.Small, Theme.Muted));
        var text = Theme.Label(string.IsNullOrEmpty(value) ? "—" : value, x, top + 20, width, 26, Theme.Body, Theme.Ink);
        text.AutoEllipsis = true;
        parent.Controls.Add(text);
        return text;
    }

    private void AddApplicationDetail(VaultEntry entry, string name, int top, int width)
    {
        string label = string.IsNullOrEmpty(entry.AppPath) ? "备注匹配应用" : "绑定应用";
        details.Controls.Add(Theme.Label(label, 16, top, width, 20, Theme.Small, Theme.Muted));
        var row = new SoftInputFrame
        {
            Bounds = new Rectangle(16, top + 23, width, 48), FillColor = Theme.Sidebar,
            AccessibleName = label + "：" + name, AccessibleRole = AccessibleRole.Grouping
        };
        string path = LinkedAppPath(entry);
        row.Paint += delegate(object sender, PaintEventArgs args)
        {
            var bounds = new Rectangle(12, 10, 28, 28);
            Bitmap icon = appIcons.Get(path);
            if (icon == null) Theme.DrawIcon(args.Graphics, "vault", bounds, Theme.Muted);
            else
            {
                GraphicsState previous = args.Graphics.Save();
                args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                args.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                args.Graphics.DrawImage(icon, bounds);
                args.Graphics.Restore(previous);
            }
        };
        var title = Theme.Label(name, 50, 0, width - 62, row.Height, Theme.BodyBold, Theme.Ink);
        title.AutoEllipsis = true;
        row.Controls.Add(title);
        details.Controls.Add(row);
    }

    private static bool Contains(string value, string term)
    {
        return (value ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    private void DrawEntry(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var entry = (VaultEntry)((ListBox)sender).Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0 && detailsOpen && !creatingEntry;
        using (var fill = new SolidBrush(selected ? Theme.TintHover : Theme.Surface))
            e.Graphics.FillRectangle(fill, e.Bounds);
        Bitmap appIcon = appIcons.Get(LinkedAppPath(entry));
        if (appIcon == null)
        {
            Theme.DrawIcon(e.Graphics, "vault", new Rectangle(e.Bounds.X + 17, e.Bounds.Y + 18, 19, 19), Theme.Muted);
        }
        else
        {
            const int iconSize = 24;
            GraphicsState previous = e.Graphics.Save();
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(appIcon, new Rectangle(e.Bounds.X + 14, e.Bounds.Y + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize));
            e.Graphics.Restore(previous);
        }
        using (var line = new Pen(Theme.Line))
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        int left = e.Bounds.X + 51;
        int source = e.Bounds.X + SourceColumnX(list.ClientSize.Width);
        int nameEnd = detailsOpen ? e.Bounds.Right : source;
        int width = nameEnd - left - 12;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, entry.Name, Theme.BodyBold, new Rectangle(left, e.Bounds.Y + 8, width, 23), Theme.Accent, flags);
        TextRenderer.DrawText(e.Graphics, entry.User, Theme.Small, new Rectangle(left, e.Bounds.Y + 31, width, 18), Theme.Muted, flags);
        if (!detailsOpen)
        {
            string context = !string.IsNullOrEmpty(entry.Origin) ? new Uri(entry.Origin).Authority : AppDisplayName(entry);
            TextRenderer.DrawText(e.Graphics, context, Theme.Body, new Rectangle(source, e.Bounds.Y, Math.Max(0, e.Bounds.Right - source - 12), e.Bounds.Height),
                Theme.Ink, flags);
        }
        if (list.Focused && (e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void EditSelected()
    {
        var entry = list == null ? null : list.SelectedItem as VaultEntry;
        if (entry == null || vault == null) { Status("请先选择账号。"); return; }
        if (inlineEditor != null) return;
        Edit(entry, null, null);
    }

    private void ClearDetailControls()
    {
        revealPassword = null;
        passwordValue = null;
        while (details.Controls.Count > 0) details.Controls[0].Dispose();
    }

    private void RenderDetailHeading(string title)
    {
        int width = details.ClientSize.Width - 32;
        var heading = Theme.Label(title, 16, 10, width - 35, 39, Theme.Subheading, Theme.Ink);
        heading.AutoEllipsis = true;
        details.Controls.Add(heading);
        var close = new SoftButton("关闭账号详情", false, "cross") { Bounds = new Rectangle(width - 1, 14, 28, 30) };
        close.Click += delegate { CloseDetails(); };
        details.Controls.Add(close);
        details.Controls.Add(new Panel { Bounds = new Rectangle(1, 60, details.Width - 2, 1), BackColor = Theme.Line });
    }

    private void SaveInlineEntry(VaultEntry entry)
    {
        if (vault == null || inlineEditor == null ||
            (!creatingEntry && !vault.Entries.Any(saved => saved.Id == entry.Id)))
            throw new InvalidOperationException("账号已不可用，请重新打开密码库。");
        if (creatingEntry && inlineTarget != null)
        {
            var duplicate = vault.Entries.FirstOrDefault(item => DetectPath(item, inlineTarget.Path) && item.User == entry.User);
            if (duplicate != null)
            {
                if (!ConfirmDialog.Ask(this, "保存账号", "更新此应用的同名账号？")) return;
                if (vault == null || inlineEditor == null) return;
                entry.Id = duplicate.Id;
                if (!inlineEditor.ApplicationSelectionChanged && !string.IsNullOrEmpty(duplicate.AppPath))
                {
                    entry.AppPath = duplicate.AppPath;
                    entry.AppHash = inlineTarget.Hash;
                }
            }
        }
        vault.Save(entry);
        EndInlineEdit();
        RebuildAutoLinks();
        if (!MatchesSearch(entry, search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)))
            search.Clear();
        RefreshEntries(entry.Id);
        Status("账号已加密保存。");
    }

    private void CancelInlineEdit()
    {
        bool wasCreating = creatingEntry;
        EndInlineEdit();
        if (wasCreating) { CloseDetails(); return; }
        RenderDetails();
        list.Focus();
    }

    private void EndInlineEdit()
    {
        inlineEditor = null;
        creatingEntry = false;
        inlineTarget = null;
        AcceptButton = null;
        if (accountRows != null) accountRows.Enabled = true;
        if (search != null) search.Enabled = true;
        if (clearSearch != null) clearSearch.Enabled = true;
        addAccount.Enabled = true;
    }

    private void Edit(VaultEntry entry, DesktopTarget target, string generated)
    {
        if (vault == null) return;
        ShowMain();
        if (inlineEditor != null) { inlineEditor.ShowError("请先保存或取消修改。"); return; }
        VaultEntry draft = entry == null ? new VaultEntry { Id = Guid.NewGuid().ToString("N") } : entry.Copy();
        if (target != null)
        {
            if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = target.Name;
            if (string.IsNullOrWhiteSpace(draft.Notes)) draft.Notes = target.Name;
        }
        if (generated != null) draft.Password = generated;
        creatingEntry = entry == null;
        inlineTarget = target;
        detailsOpen = true;
        details.Visible = true;
        LayoutAccountTable();
        ClearDetailControls();
        RenderDetailHeading(creatingEntry ? "新建账号" : "编辑账号");
        inlineEditor = new EntryEditor(draft, catalog, RefreshCatalog, SaveInlineEntry, CancelInlineEdit,
            new Size(details.ClientSize.Width - 32, details.ClientSize.Height - 84))
        { Location = new Point(16, 72) };
        details.Controls.Add(inlineEditor);
        AcceptButton = inlineEditor.SaveButton;
        accountRows.Enabled = search.Enabled = clearSearch.Enabled = addAccount.Enabled = false;
        inlineEditor.SelectNextControl(null, true, true, true, false);
    }

    private void RemoveSelected()
    {
        var entry = list == null ? null : list.SelectedItem as VaultEntry;
        if (entry == null) return;
        if (!ConfirmDialog.Ask(this, "删除账号", "删除「" + entry.Name + "」？", destructive: true) || vault == null) return;
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
        }))
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
                    return new { items = VaultEntry.QuickFillOrder(relevant).Take(200)
                        .Select(entry => new { id = entry.Id, name = entry.Name, user = entry.User }).ToArray() };
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
            () => { CancelPopup(); Edit(null, target, null); }, () => ShowPopupGenerator(active, target),
            () => VerifyApplication(target));
        popupInput.ArrowSelection = matches.Count > 1;
    }

    private async void VerifyApplication(DesktopTarget target)
    {
        CancelPopup();
        ShowMain();
        if (vault == null) return;
        if (inlineEditor != null) { inlineEditor.ShowError("请先保存或取消修改。"); return; }
        VaultSession opened = vault;
        using (var dialog = new AppVerificationDialog(target.Name, target.Path))
            if (dialog.ShowDialog(this) != DialogResult.OK || vault != opened) return;
        Status("正在更新应用验证…");
        try
        {
            string hash = await Task.Run(() => DesktopTarget.Fingerprint(target.Path));
            if (vault != opened || closed) return;
            if (!string.Equals(hash, target.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("应用文件再次发生变化，请重新打开快捷小窗并核对应用。");
            opened.UpdateAppHash(target.Path, hash);
            autoFingerprints[target.Path] = hash;
            shownProcesses.Remove(target.ProcessId);
            RebuildAutoLinks();
            RefreshEntries();
            Status("应用验证已更新。返回目标应用后可继续使用 Alt+C 填写。");
        }
        catch (Exception error) { if (!closed && vault == opened) Status(error.Message); }
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
            appIcons.Clear();
            if (!closed && vault != null)
            {
                RebuildAutoLinks();
                if (search.TextLength > 0) RefreshEntries();
                else { list.Invalidate(); RenderDetails(); }
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

    private string LinkedAppPath(VaultEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.AppPath)) return entry.AppPath;
        InstalledApp link;
        return autoLinks.TryGetValue(entry.Id, out link) ? link.Path : null;
    }

    private bool DetectPath(VaultEntry entry, string path)
    {
        string linked = LinkedAppPath(entry);
        return linked != null && string.Equals(linked, path, StringComparison.OrdinalIgnoreCase);
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
        popupInput.ArrowSelection = false;
        active.View.EnableActions(false);
        try
        {
            await DesktopFiller.Pair(target, entry, () => IsCurrent(active));
            if (IsCurrent(active)) { CancelPopup(); Status("已填写账号。请在目标应用确认。"); }
        }
        catch (Exception error)
        {
            if (IsCurrent(active))
                active.View.Message("填写已停止", error.Message, "返回账号",
                    () => ShowAccounts(active, target, MatchingEntries(target)));
        }
        finally { active.Busy = false; }
    }

    private void ShowPopupGenerator(PopupSession active, DesktopTarget target)
    {
        if (!IsCurrent(active)) return;
        popupInput.ArrowSelection = false;
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
            () => { CancelPopup(); Edit(null, target, generated); }, fill, () =>
            {
                if (!IsCurrent(active)) return;
                if (target != null) ShowAccounts(active, target, MatchingEntries(target));
                else active.View.Tools(() => { CancelPopup(); Edit(null, null, null); },
                    () => ShowPopupGenerator(active, null));
            });
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
        appIcons.Clear();
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
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (vault != null && inlineEditor == null) FocusBackground();
    }

    private void Status(string text) { status.Text = text; }

    internal static TextBox AddInput(Control parent, string label, int x, int y, int width, int max, bool mask)
    {
        parent.Controls.Add(Theme.Label(label, x, y, width, 22, Theme.Small, Theme.Muted));
        return Theme.TextField(parent, new Rectangle(x, y + 23, width, 32), label, max, mask);
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
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetCueBanner(IntPtr window, int message, IntPtr wParam, string text);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private readonly uint currentProcessId = GetCurrentProcessId();

    private static uint IdleMilliseconds()
    {
        var data = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        return GetLastInputInfo(ref data) ? unchecked((uint)Environment.TickCount - data.Time) : 0;
    }
}
