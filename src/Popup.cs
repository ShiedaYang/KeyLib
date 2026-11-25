using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed class PopupSession
{
    internal readonly IntPtr TargetWindow;
    internal readonly int Token;
    internal readonly uint OpenedAt = unchecked((uint)Environment.TickCount);
    internal readonly AccountPopup View = new AccountPopup();
    internal bool Busy;

    internal PopupSession(IntPtr window, int token) { TargetWindow = window; Token = token; }
}

internal sealed class AccountPopup : SoftWindow
{
    private const int PopupWidth = 384;
    private readonly Label heading;
    private readonly Label hint;
    private readonly Panel body;
    private readonly Panel line;
    private Action confirm;
    private readonly List<SoftButton> fillButtons = new List<SoftButton>();
    private int selection;
    private int visibleRows;
    private int firstRow;
    private SoftScrollBar scrollBar;

    internal AccountPopup()
    {
        Text = "KeyLib快捷填写";
        SetContentSize(PopupWidth, 159);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AddWindowButtons(false, true);
        Controls.Add(new BrandMark { Bounds = new Rectangle(19, 17, 34, 34) });
        heading = Theme.Label("KeyLib", 65, 17, 260, 34, Theme.Subheading, Theme.Ink);
        heading.BackColor = Theme.Surface;
        heading.AutoEllipsis = true;
        Controls.Add(heading);
        body = new Panel { Bounds = new Rectangle(20, 65, PopupWidth - 40, 78), BackColor = Theme.Surface };
        Controls.Add(body);
        line = new Panel { Bounds = new Rectangle(20, 146, PopupWidth - 40, 1), BackColor = Theme.Line, Visible = false };
        Controls.Add(line);
        hint = Theme.Label("", 20, 150, PopupWidth - 40, 30, Theme.Small, Theme.Muted);
        hint.Visible = false;
        hint.BackColor = Theme.Surface;
        hint.AutoEllipsis = true;
        Controls.Add(hint);
    }

    internal void Present(IntPtr target)
    {
        var desk = Screen.FromHandle(target).WorkingArea;
        Point cursor = Cursor.Position;
        // Keep room for the account list before expanding the popup.
        Location = new Point(Math.Max(desk.Left, Math.Min(cursor.X + 12, desk.Right - Width)),
            Math.Max(desk.Top, Math.Min(cursor.Y + 12, desk.Bottom - 385)));
        Show();
    }

    internal void Message(string title, string detail, string action, Action click)
    {
        Reset(title, 102);
        body.Controls.Add(Theme.Label(detail, 0, 0, body.Width, 39, Theme.Body, Theme.Muted));
        if (action != null)
        {
            AddButton(action, 0, 47, body.Width, true, click);
            confirm = click;
        }
    }

    internal void Accounts(string application, List<VaultEntry> accounts, bool changed,
        Action<VaultEntry> fill, Action save, Action generate, Action verify)
    {
        var ordered = new List<VaultEntry>(VaultEntry.QuickFillOrder(accounts));
        int rows = Math.Min(4, Math.Max(ordered.Count, 1));
        bool needsVerification = ordered.Count == 0 && changed;
        Reset(application, rows * 50 + (needsVerification ? 92 : 47));
        if (ordered.Count == 0)
        {
            var empty = Theme.Label(needsVerification ? "应用校验已变化，确认后继续填写。" : "此应用还没有已保存的账号。",
                0, 0, body.Width, 40, Theme.Body, Theme.Muted);
            body.Controls.Add(empty);
        }
        else
        {
            visibleRows = rows;
            int rowWidth = body.Width - (ordered.Count > visibleRows ? 13 : 0);
            var list = new Panel { Bounds = new Rectangle(0, 0, rowWidth, rows * 50) };
            body.Controls.Add(list);
            for (int index = 0; index < ordered.Count; index++)
            {
                VaultEntry entry = ordered[index];
                var row = new Panel { Bounds = new Rectangle(0, index * 50, rowWidth, 47) };
                list.Controls.Add(row);
                var name = Theme.Label(entry.Name, 9, 3, rowWidth - 110, 22, Theme.BodyBold, Theme.Ink);
                name.AutoEllipsis = true;
                row.Controls.Add(name);
                var user = Theme.Label(entry.User, 9, 24, rowWidth - 110, 18, Theme.Small, Theme.Muted);
                user.AutoEllipsis = true;
                row.Controls.Add(user);
                var button = AddButton("填写", rowWidth - 86, 6, 78, true, () => fill(entry), row);
                button.AccessibleName = "填写 " + entry.Name;
                fillButtons.Add(button);
            }
            if (ordered.Count > visibleRows)
            {
                scrollBar = new SoftScrollBar(first =>
                {
                    ShowRows(first);
                    int inView = Math.Max(first, Math.Min(first + visibleRows - 1, selection));
                    if (inView != selection) SelectAccount(inView);
                }) { Bounds = new Rectangle(rowWidth, 0, 13, rows * 50) };
                body.Controls.Add(scrollBar);
            }
            ShowRows(0);
            SelectAccount(0);
            // Keep the target field focused, so Enter must call the action directly.
            confirm = () => fill(ordered[selection]);
        }
        int actionTop = rows * 50 + 5;
        if (needsVerification)
        {
            AddButton("确认应用", 0, actionTop, body.Width, true, verify);
            actionTop += 45;
        }
        AddButton("快速保存", 0, actionTop, 165, false, save);
        AddButton("随机密码", 177, actionTop, 165, false, generate);
    }

    internal void Tools(Action save, Action generate)
    {
        Reset("KeyLib", 42);
        AddButton("新建账号", 0, 1, 165, true, save);
        AddButton("随机密码", 177, 1, 165, false, generate);
    }

    internal void Generated(string password, Action again, Action save, Action fill, Action back)
    {
        Reset("随机密码", 141);
        var value = Theme.Label(password, 10, 0, body.Width - 20, 46, Theme.Key, Theme.Ink);
        var paper = new Panel { Bounds = new Rectangle(0, 0, body.Width, 46), BackColor = Theme.Tint };
        paper.Controls.Add(value);
        body.Controls.Add(paper);
        AddButton("重新生成", 0, 58, fill == null ? 165 : 103, false, again);
        AddButton("保存账号", fill == null ? 177 : 111, 58, fill == null ? 165 : 103, fill == null, save);
        if (fill != null) AddButton("填写密码框", 222, 58, 121, true, fill);
        AddButton("返回", 0, 99, 78, false, back);
        if (fill != null) confirm = fill;
    }

    internal void SetHint(string message)
    {
        hint.Text = message;
        hint.Visible = line.Visible = !string.IsNullOrEmpty(message);
        SetContentSize(PopupWidth, body.Bottom + (hint.Visible ? 42 : 16));
    }
    internal void EnableActions(bool enabled) { body.Enabled = enabled; }
    internal void ConfirmSelection() { if (body.Enabled && confirm != null) confirm(); }

    internal void MoveSelection(int direction)
    {
        if (!body.Enabled || fillButtons.Count < 2) return;
        SelectAccount((selection + direction + fillButtons.Count) % fillButtons.Count);
    }

    private void SelectAccount(int index)
    {
        fillButtons[selection].Parent.BackColor = Theme.Surface;
        selection = index;
        SoftButton button = fillButtons[selection];
        button.Parent.BackColor = Theme.Sidebar;
        if (selection < firstRow) ShowRows(selection);
        else if (selection >= firstRow + visibleRows) ShowRows(selection - visibleRows + 1);
    }

    private void ShowRows(int first)
    {
        firstRow = first;
        for (int index = 0; index < fillButtons.Count; index++)
        {
            Control row = fillButtons[index].Parent;
            bool show = index >= first && index < first + visibleRows;
            row.Visible = show;
            if (show) row.Top = (index - first) * 50;
        }
        if (scrollBar != null) scrollBar.SetPosition(fillButtons.Count, visibleRows, first);
    }

    private void Reset(string title, int bodyHeight)
    {
        SuspendLayout();
        try
        {
            while (body.Controls.Count > 0) body.Controls[0].Dispose();
            fillButtons.Clear();
            scrollBar = null;
            selection = 0;
            confirm = null;
            heading.Text = title;
            body.Enabled = true;
            body.Height = bodyHeight;
            int below = body.Top + bodyHeight;
            line.Top = below + 2;
            hint.Top = below + 6;
            hint.Text = "";
            hint.Visible = line.Visible = false;
            SetContentSize(PopupWidth, below + 16);
        }
        finally { ResumeLayout(true); }
        // Clear the previous view when the non-activating window changes size.
        Invalidate(true);
    }

    private SoftButton AddButton(string title, int x, int y, int width, bool primary, Action click, Control parent = null)
    {
        var button = new SoftButton(title, primary) { Bounds = new Rectangle(x, y, width, 35) };
        button.KeepTargetFocused();
        button.Click += delegate { click(); };
        (parent ?? body).Controls.Add(button);
        return button;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int noActivate = 0x08000000;
            const int toolWindow = 0x00000080;
            const int topmost = 0x00000008;
            CreateParams style = base.CreateParams;
            style.ExStyle |= noActivate | toolWindow | topmost;
            return style;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021)
        {
            message.Result = (IntPtr)3; // MA_NOACTIVATE; continue delivering the click.
            return;
        }
        base.WndProc(ref message);
    }
}
