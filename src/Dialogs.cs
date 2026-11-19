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
        ClientSize = new Size(width, height);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var heading = Theme.Label(title, 24, 18, width - 95, 36, Theme.Subheading, Theme.Ink);
        Controls.Add(heading);
        MakeDraggable(heading);
        AddWindowButtons(false);
    }

    protected TextBox Field(string label, int x, int y, int width, int max, bool masked = false, string value = "")
    {
        Controls.Add(Theme.Label(label, x, y, width, 24, Theme.Body, Theme.Muted));
        var panel = new Panel { Bounds = new Rectangle(x, y + 26, width, 34), BackColor = Theme.Tint };
        var field = new TextBox
        {
            Bounds = new Rectangle(10, 7, width - 20, 23), Font = Theme.Body,
            BorderStyle = BorderStyle.None, MaxLength = max, Text = value ?? "",
            BackColor = Theme.Tint, ForeColor = Theme.Ink, AccessibleName = label, UseSystemPasswordChar = masked
        };
        panel.Controls.Add(field);
        Controls.Add(panel);
        return field;
    }

    protected SoftButton Action(string title, int x, int y, int width, bool primary, Action click)
    {
        var button = new SoftButton(title, primary) { Bounds = new Rectangle(x, y, width, 38) };
        button.Click += delegate { click(); };
        Controls.Add(button);
        return button;
    }

}

internal sealed class ConfirmDialog : VaultDialog
{
    private ConfirmDialog(string title, string question) : base(title, 435, 185)
    {
        Controls.Add(Theme.Label(question, 24, 61, 387, 55, Theme.Body, Theme.Muted));
        var cancel = Action("取消", 217, 124, 90, false, () => DialogResult = DialogResult.Cancel);
        Action("确认", 319, 124, 92, true, () => DialogResult = DialogResult.OK);
        CancelButton = cancel;
    }

    internal static bool Ask(Form owner, string title, string question)
    {
        using (var dialog = new ConfirmDialog(title, question))
            return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

internal sealed class EntryDialog : VaultDialog
{
    private readonly TextBox name;
    private readonly TextBox user;
    private readonly TextBox password;
    private readonly TextBox origin;
    private readonly TextBox notes;
    private readonly Label binding;
    private readonly Label status;
    private readonly VaultEntry draft;
    internal VaultEntry Saved { get; private set; }

    internal EntryDialog(VaultEntry entry, DesktopTarget target, string generated)
        : base(entry == null ? "快速保存账号" : "编辑账号", 498, 473)
    {
        draft = entry == null ? new VaultEntry { Id = Guid.NewGuid().ToString("N") } : entry.Copy();
        if (target != null)
        {
            draft.AppPath = target.Path;
            draft.AppHash = target.Hash;
            if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = target.Name;
        }
        if (generated != null) draft.Password = generated;
        name = Field("名称", 24, 62, 450, 180, value: draft.Name);
        user = Field("用户名", 24, 130, 450, 512, value: draft.User);
        password = Field("密码", 24, 198, 246, 2048, true, draft.Password);
        SoftButton reveal = null;
        reveal = Action("显示", 281, 224, 72, false, () =>
        {
            password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
            reveal.Text = password.UseSystemPasswordChar ? "显示" : "隐藏";
        });
        Action("随机生成", 363, 224, 111, false, () => password.Text = Passwords.Generate(24, true));
        origin = Field("网站地址（可选）", 24, 264, 450, 512, value: draft.Origin);
        notes = Field("备注（可选）", 24, 330, 320, 4000, value: draft.Notes);
        binding = Theme.Label("", 24, 394, 320, 22, Theme.Small, Theme.Muted);
        UpdateBinding();
        Controls.Add(binding);
        Action("选择应用", 354, 355, 120, false, ChooseApplication);
        var save = Action("保存", 354, 415, 120, true, Save);
        AcceptButton = save;
        status = Theme.Label("", 24, 421, 326, 34, Theme.Small, Theme.Muted);
        Controls.Add(status);
    }

    private void ChooseApplication()
    {
        using (var browse = new OpenFileDialog { Title = "绑定 Windows 应用", Filter = "应用程序 (*.exe)|*.exe" })
        {
            if (browse.ShowDialog(this) != DialogResult.OK) return;
            if (DesktopTarget.IsBrowser(Path.GetFileName(browse.FileName)))
            {
                status.Text = "浏览器账号请按 HTTPS 网址绑定。";
                return;
            }
            try
            {
                draft.AppHash = DesktopTarget.Fingerprint(browse.FileName);
                draft.AppPath = browse.FileName;
                UpdateBinding();
            }
            catch (Exception error) { status.Text = error.Message; }
        }
    }

    private void UpdateBinding()
    {
        binding.Text = string.IsNullOrEmpty(draft.AppPath) ? "应用未绑定" :
            "已绑定 · " + Path.GetFileName(draft.AppPath);
    }

    private void Save()
    {
        try
        {
            draft.Name = name.Text.Trim();
            draft.User = user.Text;
            draft.Password = password.Text;
            draft.Origin = VaultFormat.WebOrigin(origin.Text.Trim());
            if (origin.Text.Trim().Length != 0 && draft.Origin.Length == 0)
                throw new InvalidDataException("请输入以 https:// 开头的网站地址。");
            draft.Notes = notes.Text;
            draft.Validate();
            Saved = draft;
            password.Clear();
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}

internal sealed class GeneratorDialog : VaultDialog
{
    private readonly TextBox password;
    private readonly NumericUpDown length;
    private readonly CheckBox symbols;
    internal string Value { get; private set; }

    internal GeneratorDialog() : base("随机密码", 475, 260)
    {
        password = Field("新密码", 24, 65, 427, 96);
        password.ReadOnly = true;
        length = new NumericUpDown { Bounds = new Rectangle(63, 142, 64, 27), Minimum = 12, Maximum = 96,
            Value = 24, Font = Theme.Body, AccessibleName = "长度" };
        Controls.Add(Theme.Label("长度", 24, 139, 42, 29, Theme.Body, Theme.Muted));
        Controls.Add(length);
        symbols = new CheckBox { Bounds = new Rectangle(147, 142, 136, 27), Text = "包含符号", Checked = true,
            ForeColor = Theme.Ink, Font = Theme.Body };
        Controls.Add(symbols);
        Action("重新生成", 328, 135, 123, false, Generate);
        Action("保存为账号", 24, 193, 130, true, Finish);
        Generate();
    }

    private void Generate()
    {
        password.Text = Passwords.Generate((int)length.Value, symbols.Checked);
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
    private readonly ComboBox addresses;
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
        addresses = new ComboBox { Bounds = new Rectangle(24, 202, 158, 27), FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Body, AccessibleName = "接收网卡 IPv4 地址" };
        foreach (IPAddress address in TransferReceiver.Addresses()) addresses.Items.Add(address);
        if (addresses.Items.Count > 0) addresses.SelectedIndex = 0;
        Controls.Add(addresses);
        receive = Action("接收备份", 190, 196, 114, false, async () => await Receive());
        receive.Enabled = addresses.Items.Count > 0;
        pairing = Field("配对码", 24, 242, 339, 60);
        send = Action("发送备份", 378, 263, 145, true, async () => await Send());
        status = Theme.Label("仅在发送或接收时使用网络。", 24, 309, 510, 34, Theme.Small, Theme.Muted);
        Controls.Add(status);
        FormClosed += delegate { if (receiver != null) receiver.Dispose(); };
    }

    private string PickPath()
    {
        using (var file = new SaveFileDialog { Title = "保存加密备份", FileName = "KeyLib备份-" +
            DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".softvault", Filter = "KeyLib加密备份 (*.softvault)|*.softvault" })
        {
            if (file.ShowDialog(this) != DialogResult.OK) return null;
            if (!session.IsPrimaryPath(file.FileName)) return file.FileName;
            status.Text = "请选择不同位置，保留正在使用的密码库。";
            return null;
        }
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
        using (var file = new OpenFileDialog { Title = "打开加密备份", Filter = "KeyLib加密备份 (*.softvault)|*.softvault" })
        {
            if (file.ShowDialog(this) != DialogResult.OK) return;
            path = file.FileName;
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
            receiver = new TransferReceiver((IPAddress)addresses.SelectedItem);
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
                receive.Enabled = addresses.Items.Count > 0;
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
                receive.Enabled = addresses.Items.Count > 0;
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
