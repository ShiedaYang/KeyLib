using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed class PopupSession
{
    internal readonly IntPtr TargetWindow;
    internal readonly uint OpenedAt = unchecked((uint)Environment.TickCount);
    internal readonly AccountPopup View = new AccountPopup();
    internal bool Busy;

    internal PopupSession(IntPtr window) { TargetWindow = window; }
}

internal sealed class AccountPopup : SoftWindow
{
    private const int PopupWidth = 384;
    private readonly Label heading;
    private readonly Label hint;
    private readonly Panel body;
    private readonly Panel line;

    internal AccountPopup()
    {
        Text = "KeyLib快捷填写";
        ClientSize = new Size(PopupWidth, 184);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AddWindowButtons(false, true);
        Controls.Add(new BrandMark { Bounds = new Rectangle(19, 17, 34, 34) });
        heading = Theme.Label("KeyLib", 65, 17, 260, 34, Theme.Subheading, Theme.Ink);
        heading.AutoEllipsis = true;
        Controls.Add(heading);
        body = new Panel { Bounds = new Rectangle(20, 65, PopupWidth - 40, 78) };
        Controls.Add(body);
        line = new Panel { Bounds = new Rectangle(20, 146, PopupWidth - 40, 1), BackColor = Theme.Line };
        Controls.Add(line);
        hint = Theme.Label("切换窗口即取消", 20, 150, PopupWidth - 40, 30, Theme.Small, Theme.Muted);
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
        if (action != null) AddButton(action, 0, 47, body.Width, true, click);
    }

    internal void Accounts(string application, List<VaultEntry> accounts, bool changed,
        Action<VaultEntry> fill, Action save, Action generate)
    {
        var ordered = accounts.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        int rows = Math.Min(4, Math.Max(ordered.Count, 1));
        Reset(application, rows * 50 + 47);
        if (ordered.Count == 0)
        {
            var empty = Theme.Label(changed ? "应用文件已更新，请重新绑定账号。" : "此应用还没有已保存的账号。",
                0, 0, body.Width, 40, Theme.Body, Theme.Muted);
            body.Controls.Add(empty);
        }
        else
        {
            var list = new Panel { Bounds = new Rectangle(0, 0, body.Width, rows * 50), AutoScroll = ordered.Count > 4 };
            body.Controls.Add(list);
            for (int index = 0; index < ordered.Count; index++)
            {
                VaultEntry entry = ordered[index];
                var name = Theme.Label(entry.Name, 0, index * 50, 240, 22, Theme.BodyBold, Theme.Ink);
                name.AutoEllipsis = true;
                list.Controls.Add(name);
                var user = Theme.Label(entry.User, 0, index * 50 + 22, 238, 18, Theme.Small, Theme.Muted);
                user.AutoEllipsis = true;
                list.Controls.Add(user);
                int buttonLeft = ordered.Count > 4 ? 246 : 265;
                AddButton("填写", buttonLeft, index * 50 + 4, 78, true, () => fill(entry), list);
            }
        }
        AddButton("快速保存", 0, rows * 50 + 5, 165, false, save);
        AddButton("随机密码", 177, rows * 50 + 5, 165, false, generate);
    }

    internal void Tools(Action save, Action generate)
    {
        Reset("KeyLib", 42);
        AddButton("新建账号", 0, 1, 165, true, save);
        AddButton("随机密码", 177, 1, 165, false, generate);
    }

    internal void Generated(string password, Action again, Action save, Action fill)
    {
        Reset("随机密码", 99);
        var value = Theme.Label(password, 10, 0, body.Width - 20, 46, Theme.Key, Theme.Ink);
        var paper = new Panel { Bounds = new Rectangle(0, 0, body.Width, 46), BackColor = Theme.Tint };
        paper.Controls.Add(value);
        body.Controls.Add(paper);
        AddButton("重新生成", 0, 58, fill == null ? 165 : 103, false, again);
        AddButton("保存账号", fill == null ? 177 : 111, 58, fill == null ? 165 : 103, fill == null, save);
        if (fill != null) AddButton("填写密码框", 222, 58, 121, true, fill);
        SetHint("24 个字符，包含大小写字母、数字和符号");
    }

    internal void SetHint(string message) { hint.Text = message; }
    internal void EnableActions(bool enabled) { body.Enabled = enabled; }

    private void Reset(string title, int bodyHeight)
    {
        while (body.Controls.Count > 0) body.Controls[0].Dispose();
        heading.Text = title;
        body.Enabled = true;
        body.Height = bodyHeight;
        int below = body.Top + bodyHeight;
        line.Top = below + 2;
        hint.Top = below + 6;
        ClientSize = new Size(PopupWidth, below + 42);
        hint.Text = "切换窗口即取消";
    }

    private void AddButton(string title, int x, int y, int width, bool primary, Action click, Control parent = null)
    {
        var button = new SoftButton(title, primary) { Bounds = new Rectangle(x, y, width, 35) };
        button.KeepTargetFocused();
        button.Click += delegate { click(); };
        (parent ?? body).Controls.Add(button);
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
