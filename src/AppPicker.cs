using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

internal sealed class AppPicker : VaultDialog
{
    private readonly AppCatalog catalog;
    private readonly SoftListBox apps;
    private readonly SoftListFrame appRows;
    private readonly TextBox filter;
    private readonly Label status;
    private readonly SoftButton link;
    private readonly AppIconCache icons = new AppIconCache();
    internal InstalledApp App { get; private set; }
    internal bool Disabled { get; private set; }

    internal AppPicker(AppCatalog catalog) : base("选择已安装应用", 524, 446)
    {
        this.catalog = catalog;
        filter = Field("搜索应用名称", 24, 65, 476, 160);
        apps = new SoftListBox
        {
            Bounds = new Rectangle(24, 138, 476, 204), BackColor = Theme.Sidebar, ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 43,
            IntegralHeight = false, AccessibleName = "已安装应用"
        };
        appRows = new SoftListFrame(apps);
        Controls.Add(appRows);
        apps.DrawItem += DrawApp;
        apps.DoubleClick += delegate { Choose(); };
        status = Theme.Label("", 24, 351, 476, 28, Theme.Small, Theme.Muted);
        Controls.Add(status);
        Action("按备注匹配", 24, 388, 110, false, () => Finish(null, false));
        Action("不关联应用", 144, 388, 110, false, () => Finish(null, true));
        link = Action("关联选中应用", 355, 388, 145, true, Choose);
        AcceptButton = link;
        filter.TextChanged += delegate { Refresh(); };
        apps.SelectedIndexChanged += delegate { link.Enabled = apps.SelectedIndex >= 0; };
        Refresh();
        ActiveControl = filter;
    }

    private new void Refresh()
    {
        string term = filter.Text.Trim();
        apps.BeginUpdate();
        try
        {
            apps.Items.Clear();
            foreach (InstalledApp application in catalog.Items)
                if (application.Names.Any(name => name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
                    apps.Items.Add(application);
            if (apps.Items.Count > 0) apps.SelectedIndex = 0;
        }
        finally { apps.EndUpdate(); }
        appRows.UpdateScroll();
        status.Text = apps.Items.Count == 0 ? "没有找到匹配的应用。" : apps.Items.Count + " 个应用";
        link.Enabled = apps.SelectedIndex >= 0;
    }

    private void Choose()
    {
        var choice = apps.SelectedItem as InstalledApp;
        if (choice != null) Finish(choice, false);
    }

    private void Finish(InstalledApp app, bool disabled)
    {
        App = app;
        Disabled = disabled;
        DialogResult = DialogResult.OK;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) icons.Clear();
        base.Dispose(disposing);
    }

    private void DrawApp(object sender, DrawItemEventArgs args)
    {
        if (args.Index < 0) return;
        var app = (InstalledApp)((ListBox)sender).Items[args.Index];
        bool selected = (args.State & DrawItemState.Selected) != 0;
        using (var brush = new SolidBrush(selected ? Theme.TintHover : Theme.Sidebar))
            args.Graphics.FillRectangle(brush, args.Bounds);
        var iconBounds = new Rectangle(args.Bounds.X + 10, args.Bounds.Y + 9, 24, 24);
        Bitmap icon = icons.Get(app.Path);
        if (icon == null) Theme.DrawIcon(args.Graphics, "vault", iconBounds, Theme.Muted);
        else
        {
            GraphicsState previous = args.Graphics.Save();
            args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            args.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            args.Graphics.DrawImage(icon, iconBounds);
            args.Graphics.Restore(previous);
        }
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(args.Graphics, app.Name, Theme.BodyBold,
            new Rectangle(args.Bounds.X + 44, args.Bounds.Y + 2, args.Bounds.Width - 55, 22), Theme.Ink, flags);
        TextRenderer.DrawText(args.Graphics, System.IO.Path.GetFileName(app.Path), Theme.Small,
            new Rectangle(args.Bounds.X + 44, args.Bounds.Y + 22, args.Bounds.Width - 55, 18), Theme.Muted, flags);
        if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
    }
}
