using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

internal class VaultDialog : SoftWindow
{
    protected VaultDialog(string title, int width, int height)
    {
        Text = title;
        SetContentSize(width, height);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var header = new Panel { Bounds = new Rectangle(1, 1, width - 2, 57), BackColor = Theme.Sidebar };
        var heading = Theme.Label(title, 24, 18, width - 95, 36, Theme.Subheading, Theme.Ink);
        header.Controls.Add(heading);
        header.Controls.Add(new Panel { Bounds = new Rectangle(0, 56, header.Width, 1), BackColor = Theme.Line });
        AddEdgePanel(header);
        MakeDraggable(header);
        MakeDraggable(heading);
        AddWindowButtons(false, parent: header);
        KeyPreview = true;
        KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Escape) return;
            args.SuppressKeyPress = true;
            DialogResult = DialogResult.Cancel;
        };
    }

    protected TextBox Field(string label, int x, int y, int width, int max, bool masked = false, string value = "")
    {
        Controls.Add(Theme.Label(label, x, y, width, 24, Theme.Body, Theme.Muted));
        return Theme.TextField(this, new Rectangle(x, y + 26, width, 34), label, max, masked, value);
    }

    protected SoftButton Action(string title, int x, int y, int width, bool primary, Action click)
    {
        var button = new SoftButton(title, primary) { Bounds = new Rectangle(x, y, width, 38) };
        button.Click += delegate { click(); };
        Controls.Add(button);
        return button;
    }

}

internal sealed class NoticeDialog : VaultDialog
{
    private NoticeDialog(string title, string message) : base(title, 460, 220)
    {
        StartPosition = FormStartPosition.CenterScreen;
        var explanation = Theme.Label(message, 24, 67, 412, 91, Theme.Body, Theme.Ink);
        explanation.AutoEllipsis = true;
        Controls.Add(explanation);
        AcceptButton = Action("知道了", 329, 164, 107, true, () => DialogResult = DialogResult.OK);
    }

    internal static void ShowMessage(string title, string message)
    {
        using (var dialog = new NoticeDialog(title, message)) dialog.ShowDialog();
    }
}

internal sealed class ConfirmDialog : VaultDialog
{
    private ConfirmDialog(string title, string question, bool destructive) : base(title, 435, 185)
    {
        Controls.Add(Theme.Label(question, 24, 61, 387, 55, Theme.Body, Theme.Muted));
        var cancel = Action("取消", 217, 124, 90, false, () => DialogResult = DialogResult.Cancel);
        var confirm = Action(destructive ? "删除" : "确认", 319, 124, 92, true, () => DialogResult = DialogResult.OK);
        confirm.Destructive = destructive;
        CancelButton = cancel;
    }

    internal static bool Ask(Form owner, string title, string question, bool destructive = false)
    {
        using (var dialog = new ConfirmDialog(title, question, destructive))
            return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

internal sealed class AppVerificationDialog : VaultDialog
{
    internal AppVerificationDialog(string application, string path) : base("确认应用", 498, 326)
    {
        var explanation = Theme.Label("已识别到「" + application + "」，但程序文件与上次验证时不同。请核对安装位置后更新验证。",
            24, 70, 450, 64, Theme.Body, Theme.Ink);
        explanation.AutoEllipsis = true;
        Controls.Add(explanation);
        Controls.Add(Theme.Label("应用安装位置", 24, 142, 450, 22, Theme.Small, Theme.Muted));
        var location = Theme.TextField(this, new Rectangle(24, 167, 450, 72), "应用安装位置", 2048, value: path);
        location.Multiline = true;
        location.ReadOnly = true;
        location.Bounds = new Rectangle(11, 6, 428, 60);
        var cancel = Action("取消", 252, 265, 102, false, () => DialogResult = DialogResult.Cancel);
        Action("更新验证", 366, 265, 108, true, () => DialogResult = DialogResult.OK);
        CancelButton = cancel;
        ActiveControl = cancel;
    }
}

internal sealed class GeneratorDialog : VaultDialog
{
    private readonly TextBox password;
    private readonly TextBox length;
    private readonly SoftCheckBox symbols;
    private readonly Label status;
    internal string Value { get; private set; }

    internal GeneratorDialog() : base("随机密码", 475, 269)
    {
        password = Field("新密码", 24, 65, 427, 96);
        password.ReadOnly = true;
        Controls.Add(Theme.Label("长度", 24, 137, 41, 36, Theme.Body, Theme.Muted));
        var fewer = Action("−", 65, 136, 32, false, () => ChangeLength(-1));
        fewer.AccessibleName = "减少密码长度";
        length = Theme.TextField(this, new Rectangle(100, 138, 50, 34), "密码长度", 2, value: "24");
        length.TextAlign = HorizontalAlignment.Center;
        var more = Action("+", 153, 136, 32, false, () => ChangeLength(1));
        more.AccessibleName = "增加密码长度";
        length.KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Up && args.KeyCode != Keys.Down) return;
            args.SuppressKeyPress = true;
            ChangeLength(args.KeyCode == Keys.Up ? 1 : -1);
        };
        symbols = new SoftCheckBox("包含符号") { Bounds = new Rectangle(201, 140, 115, 29), Checked = true };
        Controls.Add(symbols);
        Action("重新生成", 328, 135, 123, false, Generate);
        Action("保存为账号", 24, 201, 130, true, Finish);
        status = Theme.Label("", 171, 201, 280, 38, Theme.Small, Theme.Muted);
        Controls.Add(status);
        Generate();
    }

    private void ChangeLength(int step)
    {
        int current;
        if (!int.TryParse(length.Text, out current)) current = 24;
        length.Text = Math.Min(96, Math.Max(12, current + step)).ToString();
        status.Text = "";
    }

    private void Generate()
    {
        int count;
        if (!int.TryParse(length.Text, out count) || count < 12 || count > 96)
        {
            status.Text = "密码长度请输入 12 到 96。";
            length.Focus();
            length.SelectAll();
            return;
        }
        password.Text = Passwords.Generate(count, symbols.Checked);
        status.Text = "";
    }

    private void Finish()
    {
        Value = password.Text;
        password.Clear();
        DialogResult = DialogResult.OK;
    }
}

internal sealed class BackupDialog : VaultDialog
{
    private readonly VaultSession session;
    private readonly Label status;
    private readonly TextBox pairing;
    private IPAddress address;
    private readonly SoftButton receive;
    private readonly SoftButton send;
    private TransferReceiver receiver;

    internal BackupDialog(VaultSession session) : base("加密备份", 560, 352)
    {
        this.session = session;
        Controls.Add(Theme.Label("文件备份", 24, 71, 188, 26, Theme.BodyBold, Theme.Ink));
        Action("导出加密文件", 24, 106, 139, false, Export);
        Action("导入并合并", 174, 106, 129, false, Import);
        Controls.Add(Theme.Label("两台设备在同一局域网", 24, 164, 300, 26, Theme.BodyBold, Theme.Ink));
        IPAddress[] available = TransferReceiver.Addresses().ToArray();
        address = available.FirstOrDefault();
        var addressMenu = new SoftMenu(156);
        SoftButton addressChoice = null;
        addressChoice = Action(address == null ? "无可用网卡" : address.ToString(), 24, 196, 158, false,
            () => addressMenu.Show(addressChoice, new Point(0, addressChoice.Height)));
        addressChoice.AccessibleName = address == null ? "无可用网卡" : "接收网卡 " + address;
        addressChoice.LeadingIcon = "down";
        addressChoice.Enabled = address != null;
        addressChoice.Disposed += delegate { addressMenu.Dispose(); };
        foreach (IPAddress candidate in available)
        {
            addressMenu.Add(candidate.ToString(), () =>
            {
                address = candidate;
                addressChoice.Text = address.ToString();
                addressChoice.AccessibleName = "接收网卡 " + address;
            });
        }
        receive = Action("接收备份", 193, 196, 114, false, async () => await Receive());
        receive.Enabled = address != null;
        pairing = Field("配对码", 24, 242, 339, 60);
        send = Action("发送备份", 378, 263, 145, true, async () => await Send());
        status = Theme.Label("仅在发送或接收时使用网络。", 24, 309, 510, 34, Theme.Small, Theme.Muted);
        Controls.Add(status);
        FormClosed += delegate { if (receiver != null) receiver.Dispose(); };
    }

    private string PickPath()
    {
        string suggested = "KeyLib备份-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".softvault";
        using (var file = new BackupFilePicker(suggested, session.IsPrimaryPath))
            return file.ShowDialog(this) == DialogResult.OK ? file.SelectedPath : null;
    }

    private void Export()
    {
        string path = PickPath();
        if (path == null) return;
        try
        {
            VaultFormat.WriteAtomic(path, session.Snapshot());
            status.Text = "加密文件已保存。恢复时使用源密码库的主密码。";
        }
        catch (Exception error) { status.Text = error.Message; }
    }

    private async void Import()
    {
        string path;
        using (var file = new BackupFilePicker())
        {
            if (file.ShowDialog(this) != DialogResult.OK) return;
            path = file.SelectedPath;
        }
        using (var password = new BackupPasswordDialog())
        {
            if (password.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                status.Text = "正在验证备份…";
                var entries = await Task.Run(() =>
                {
                    using (var backup = VaultSession.Open(path, password.Value, false))
                        return backup.Entries.Select(item => item.Copy()).ToList();
                });
                if (!IsDisposed)
                    status.Text = "已合并 " + session.Merge(entries) + " 个新条目，已有条目保留。";
                entries.Clear();
            }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
        }
    }

    private async Task Receive()
    {
        string path = PickPath();
        if (path == null) return;
        try
        {
            receiver = new TransferReceiver(address);
            receive.Enabled = send.Enabled = false;
            pairing.ReadOnly = true;
            pairing.Text = receiver.Code;
            pairing.Focus();
            pairing.SelectAll();
            status.Text = "在发送设备粘贴此一次性配对码，2 分钟内有效。";
            await receiver.Receive(path, TimeSpan.FromMinutes(2));
            if (!IsDisposed) status.Text = "加密备份已接收。用「导入并合并」恢复。";
        }
        catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
        finally
        {
            if (receiver != null) receiver.Dispose();
            receiver = null;
            if (!IsDisposed)
            {
                pairing.Clear();
                pairing.ReadOnly = false;
                receive.Enabled = address != null;
                send.Enabled = true;
            }
        }
    }

    private async Task Send()
    {
        try
        {
            send.Enabled = receive.Enabled = false;
            status.Text = "正在发送加密备份…";
            await TransferReceiver.Send(pairing.Text, session.Snapshot());
            if (!IsDisposed)
            {
                pairing.Clear();
                status.Text = "接收设备已验证并保存加密备份。";
            }
        }
        catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
        finally
        {
            if (!IsDisposed)
            {
                send.Enabled = true;
                receive.Enabled = address != null;
            }
        }
    }
}

internal sealed class BackupPasswordDialog : VaultDialog
{
    internal string Value { get; private set; }

    internal BackupPasswordDialog() : base("解锁加密备份", 430, 192)
    {
        var password = Field("源密码库主密码", 24, 64, 382, 256, true);
        AcceptButton = Action("验证并合并", 262, 138, 144, true, () =>
        {
            Value = password.Text;
            password.Clear();
            DialogResult = DialogResult.OK;
        });
    }
}
