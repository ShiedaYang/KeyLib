using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class BackupFilePicker : VaultDialog
{
    private readonly bool save;
    private readonly Func<string, bool> protectedPath;
    private readonly TextBox location;
    private readonly TextBox fileName;
    private readonly SoftListBox files;
    private readonly SoftListFrame fileRows;
    private readonly Label status;
    private readonly SoftButton accept;
    private readonly SoftButton up;
    private readonly SoftButton newFolder;
    private string folder;
    private int browseRequest;

    internal string SelectedPath { get; private set; }

    internal BackupFilePicker(string suggestedName = null, Func<string, bool> protectedPath = null)
        : base(suggestedName == null ? "打开加密备份" : "保存加密备份", 612, 523)
    {
        save = suggestedName != null;
        this.protectedPath = protectedPath;
        Controls.Add(Theme.Label("文件夹", 24, 65, 260, 24, Theme.BodyBold, Theme.Ink));
        location = Theme.TextField(this, new Rectangle(24, 93, 468, 34), "文件夹路径", 2048);
        Action("前往", 502, 91, 86, false, GoToLocation);
        Action("磁盘", 24, 139, 67, false, () => Browse(null));
        up = Action("上一级", 99, 139, 82, false, GoUp);
        if (save)
            newFolder = Action("新建文件夹", 470, 139, 118, false, CreateFolder);

        var browser = Theme.FramedPanel(new Rectangle(24, 190, 564, 184), Theme.Surface);
        Controls.Add(browser);
        browser.Controls.Add(Theme.Label("名称", 14, 3, 320, 28, Theme.BodyBold, Theme.Ink));
        files = new SoftListBox
        {
            Bounds = new Rectangle(1, 35, 562, 142), BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, ForeColor = Theme.Ink, Font = Theme.Body,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 35, IntegralHeight = false,
            AccessibleName = "文件和文件夹"
        };
        fileRows = new SoftListFrame(files);
        browser.Controls.Add(fileRows);
        files.DrawItem += DrawFile;
        files.SelectedIndexChanged += delegate
        {
            var selected = files.SelectedItem as FileRow;
            if (selected != null && !selected.Directory) fileName.Text = selected.Name;
        };
        files.DoubleClick += delegate { ChooseRow(); };

        fileName = Field("文件名", 24, 383, 564, 2048, value: suggestedName);
        status = Theme.Label("", 24, 459, 344, 43, Theme.Small, Theme.Muted);
        Controls.Add(status);
        CancelButton = Action("取消", 387, 463, 85, false, () => DialogResult = DialogResult.Cancel);
        accept = Action(save ? "保存备份" : "打开备份", 482, 463, 106, true, ConfirmPath);
        AcceptButton = accept;
        Shown += delegate
        {
            string start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Browse(start.Length > 0 && Directory.Exists(start) ? start : null);
        };
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter && location.Focused) { GoToLocation(); return true; }
        if (keyData == Keys.Enter && files.Focused) { ChooseRow(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void GoToLocation()
    {
        string path = location.Text.Trim().Trim('"');
        if (path.Length > 0) Browse(Environment.ExpandEnvironmentVariables(path));
        else Browse(null);
    }

    private void GoUp()
    {
        if (folder == null) return;
        DirectoryInfo parent = Directory.GetParent(folder);
        Browse(parent == null ? null : parent.FullName);
    }

    private async void Browse(string target)
    {
        int request = ++browseRequest;
        accept.Enabled = false;
        status.Text = "正在读取文件夹…";
        try
        {
            string next = target == null ? null : Path.GetFullPath(target);
            FileRow[] result = await Task.Run(() => ReadFolder(next));
            if (IsDisposed || request != browseRequest) return;
            folder = next;
            location.Text = folder ?? "";
            files.BeginUpdate();
            try
            {
                files.Items.Clear();
                files.Items.AddRange(result);
            }
            finally { files.EndUpdate(); }
            fileRows.UpdateScroll();
            if (!save) fileName.Clear();
            status.Text = result.Length > 0 ? "" : "此文件夹内没有加密备份。";
        }
        catch (Exception error)
        {
            if (!IsDisposed && request == browseRequest)
            {
                location.Text = folder ?? "";
                status.Text = error.Message;
            }
        }
        finally
        {
            if (!IsDisposed && request == browseRequest)
            {
                up.Enabled = folder != null;
                if (newFolder != null) newFolder.Enabled = folder != null;
                accept.Enabled = true;
            }
        }
    }

    private static FileRow[] ReadFolder(string directory)
    {
        if (directory == null)
            return Directory.GetLogicalDrives().Select(path => new FileRow(path, path, true)).ToArray();
        return Directory.EnumerateDirectories(directory)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new FileRow(path, Path.GetFileName(path), true))
            .Concat(Directory.EnumerateFiles(directory, "*.softvault")
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .Select(path => new FileRow(path, Path.GetFileName(path), false)))
            .ToArray();
    }

    private void ChooseRow()
    {
        if (!accept.Enabled) return;
        var selected = files.SelectedItem as FileRow;
        if (selected == null) return;
        if (selected.Directory) Browse(selected.Path);
        else ConfirmPath();
    }

    private void CreateFolder()
    {
        if (folder == null || !accept.Enabled) return;
        try
        {
            using (var dialog = new FolderNameDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = Path.Combine(folder, dialog.FolderName);
                Directory.CreateDirectory(path);
                Browse(path);
            }
        }
        catch (Exception error) { status.Text = error.Message; }
    }

    private void ConfirmPath()
    {
        try
        {
            string typed = fileName.Text.Trim().Trim('"');
            if (typed.Length == 0)
            {
                var selected = files.SelectedItem as FileRow;
                if (selected != null && selected.Directory) Browse(selected.Path);
                else status.Text = "请选择文件或输入文件名。";
                return;
            }
            if (!Path.IsPathRooted(typed) && folder == null)
            {
                status.Text = "请先选择文件夹，或输入绝对路径。";
                return;
            }
            string path = Path.GetFullPath(Path.IsPathRooted(typed) ? typed : Path.Combine(folder, typed));
            if (Directory.Exists(path)) { Browse(path); return; }
            if (Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                status.Text = "文件名中含有无效字符。";
                return;
            }
            if (save && !Path.HasExtension(path)) path += ".softvault";
            if (save && !Directory.Exists(Path.GetDirectoryName(path)))
            {
                status.Text = "请选择一个存在的文件夹。";
                return;
            }
            if (!save && !File.Exists(path))
            {
                status.Text = "找不到该备份文件。";
                return;
            }
            if (protectedPath != null && protectedPath(path))
            {
                status.Text = "请选择其他位置，保留正在使用的密码库。";
                return;
            }
            if (save && File.Exists(path) && !ConfirmDialog.Ask(this, "替换已有备份",
                "「" + Path.GetFileName(path) + "」已存在。是否替换这个文件？")) return;
            SelectedPath = path;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) { status.Text = error.Message; }
    }

    private static void DrawFile(object sender, DrawItemEventArgs args)
    {
        if (args.Index < 0) return;
        var row = (FileRow)((ListBox)sender).Items[args.Index];
        bool selected = (args.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? Theme.TintHover : Theme.Surface))
            args.Graphics.FillRectangle(fill, args.Bounds);
        Theme.DrawIcon(args.Graphics, row.Directory ? "folder" : "vault",
            new Rectangle(args.Bounds.Left + 13, args.Bounds.Top + 9, 16, 16), Theme.Accent);
        TextRenderer.DrawText(args.Graphics, row.Name, Theme.Body,
            new Rectangle(args.Bounds.Left + 39, args.Bounds.Top, args.Bounds.Width - 47, args.Bounds.Height), Theme.Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
    }

    private sealed class FileRow
    {
        internal readonly string Path;
        internal readonly string Name;
        internal readonly bool Directory;

        internal FileRow(string path, string name, bool directory)
        {
            Path = path;
            Name = name;
            Directory = directory;
        }

        public override string ToString() { return Name; }
    }

    private sealed class FolderNameDialog : VaultDialog
    {
        internal string FolderName { get; private set; }

        internal FolderNameDialog() : base("新建文件夹", 398, 211)
        {
            var name = Field("文件夹名称", 24, 66, 350, 255);
            var error = Theme.Label("", 24, 123, 350, 25, Theme.Small, Theme.Muted);
            Controls.Add(error);
            AcceptButton = Action("新建", 270, 153, 104, true, () =>
            {
                string value = name.Text.Trim();
                if (value.Length == 0 || value == "." || value == ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error.Text = "请输入有效的文件夹名称。";
                    return;
                }
                FolderName = value;
                DialogResult = DialogResult.OK;
            });
        }
    }
}
