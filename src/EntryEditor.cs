using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class EntryEditor : Panel
{
    private readonly TextBox name;
    private readonly TextBox user;
    private readonly TextBox password;
    private readonly TextBox origin;
    private readonly TextBox notes;
    private readonly SoftButton reveal;
    private readonly Label binding;
    private readonly Label status;
    private readonly VaultEntry draft;
    private readonly Func<Task<AppCatalog>> refreshCatalog;
    private readonly Action<VaultEntry> save;
    private AppCatalog catalog;

    internal readonly SoftButton SaveButton;
    internal readonly SoftButton CancelButton;
    internal bool ApplicationSelectionChanged { get; private set; }
    internal bool HasChanges
    {
        get
        {
            return name.Text != (draft.Name ?? "") || user.Text != (draft.User ?? "") ||
                password.Text != (draft.Password ?? "") || origin.Text != (draft.Origin ?? "") ||
                notes.Text != (draft.Notes ?? "") || ApplicationSelectionChanged;
        }
    }

    internal EntryEditor(VaultEntry entry, AppCatalog catalog, Func<Task<AppCatalog>> refreshCatalog,
        Action<VaultEntry> save, Action cancel, Size size)
    {
        Size = size;
        BackColor = Theme.Surface;
        draft = entry.Copy();
        this.catalog = catalog;
        this.refreshCatalog = refreshCatalog;
        this.save = save;
        name = Field("名称", 0, Width, 180, draft.Name);
        user = Field("用户名", 60, Width, 512, draft.User);
        password = Field("密码", 120, Width - 160, 2048, draft.Password, true);
        reveal = MainWindow.AddAction(this, "显示", Width - 152, 139, 64, false, () =>
        {
            password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
            UpdateRevealLabel();
        });
        UpdateRevealLabel();
        MainWindow.AddAction(this, "随机生成", Width - 80, 139, 80, false,
            () => password.Text = Passwords.Generate(24, true));
        origin = Field("网站地址（可选）", 180, Width, 512, draft.Origin);
        Controls.Add(Theme.Label("备注（可选）", 0, 240, Width, 20, Theme.Small, Theme.Muted));
        notes = Theme.TextField(this, new Rectangle(0, 260, Width, 48), "备注", 4000, value: draft.Notes);
        notes.Multiline = true;
        notes.AutoSize = false;
        notes.Bounds = new Rectangle(11, 5, Width - 22, 38);
        notes.AcceptsReturn = true;
        binding = Theme.Label("", 0, 316, Width - 110, 32, Theme.Small, Theme.Muted);
        binding.AutoEllipsis = true;
        Controls.Add(binding);
        MainWindow.AddAction(this, "选择应用", Width - 102, 314, 102, false, ChooseApplication);
        notes.TextChanged += delegate { UpdateBinding(); };
        UpdateBinding();
        status = Theme.Label("", 0, 352, Width, 22, Theme.Small, Theme.Muted);
        Controls.Add(status);

        const int gap = 12;
        int width = (Width - gap) / 2;
        SaveButton = MainWindow.AddAction(this, "保存", 0, Height - 35, width, true, Save);
        CancelButton = MainWindow.AddAction(this, "取消", width + gap, Height - 35, Width - width - gap, false, cancel);
    }

    private TextBox Field(string label, int y, int width, int limit, string value, bool masked = false)
    {
        Controls.Add(Theme.Label(label, 0, y, width, 20, Theme.Small, Theme.Muted));
        return Theme.TextField(this, new Rectangle(0, y + 20, width, 32), label, limit, masked, value);
    }

    internal void UpdateCatalog(AppCatalog next)
    {
        catalog = next;
        UpdateBinding();
    }

    internal void ShowError(string message) { status.Text = message; }

    internal void HidePassword()
    {
        password.UseSystemPasswordChar = true;
        UpdateRevealLabel();
    }

    private void UpdateRevealLabel()
    {
        reveal.Text = password.UseSystemPasswordChar ? "显示" : "隐藏";
        reveal.AccessibleName = password.UseSystemPasswordChar ? "显示密码" : "隐藏密码";
    }

    private async void ChooseApplication()
    {
        status.Text = "正在查找已安装应用…";
        try
        {
            AppCatalog refreshed = await refreshCatalog();
            if (IsDisposed) return;
            catalog = refreshed;
            status.Text = "";
            using (var picker = new AppPicker(catalog))
            {
                if (picker.ShowDialog(FindForm()) != DialogResult.OK || IsDisposed) return;
                string hash = picker.App == null ? null : DesktopTarget.Fingerprint(picker.App.Path);
                draft.AppHash = hash;
                draft.AppPath = picker.App == null ? null : picker.App.Path;
                draft.DisableAppDetection = picker.Disabled;
                ApplicationSelectionChanged = true;
                UpdateBinding();
            }
        }
        catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
    }

    private void UpdateBinding()
    {
        if (!string.IsNullOrEmpty(draft.AppPath))
        {
            InstalledApp app = catalog.ByPath(draft.AppPath);
            binding.Text = "关联应用：" + (app == null ? Path.GetFileName(draft.AppPath) : app.Name);
            return;
        }
        InstalledApp automatic = draft.DisableAppDetection ? null : catalog.MatchNotes(notes.Text);
        binding.Text = automatic == null ? "应用关联可留空。可在备注中填写应用名。" : "备注匹配：" + automatic.Name;
    }

    private void Save()
    {
        try
        {
            var candidate = draft.Copy();
            candidate.Name = name.Text.Trim();
            candidate.User = user.Text;
            candidate.Password = password.Text;
            candidate.Origin = VaultFormat.WebOrigin(origin.Text.Trim());
            if (origin.Text.Trim().Length != 0 && candidate.Origin.Length == 0)
                throw new InvalidDataException("请输入以 https:// 开头的网站地址。");
            candidate.Notes = notes.Text;
            candidate.Validate();
            save(candidate);
        }
        catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && password != null && !password.IsDisposed) password.Clear();
        base.Dispose(disposing);
    }
}
