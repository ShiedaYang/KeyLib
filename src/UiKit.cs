using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
internal class SoftWindow : Form
{
    private const int CornerRadius = 18;
    private bool roundedFrame;

    internal SoftWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams style = base.CreateParams;
            if (WindowChrome.SupportsRoundedFrame) style.Style |= WindowChrome.BorderStyle;
            return style;
        }
    }

    protected void SetContentSize(int width, int height)
    {
        if (WindowChrome.SupportsRoundedFrame)
        {
            // FormBorderStyle.None normally makes WinForms treat the entire window as client area.
            Size border = IsHandleCreated
                ? new Size(Width - ClientSize.Width, Height - ClientSize.Height)
                : new Size(SystemInformation.BorderSize.Width * 2, SystemInformation.BorderSize.Height * 2);
            Size = new Size(width + border.Width, height + border.Height);
        }
        else ClientSize = new Size(width, height);
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        roundedFrame = WindowChrome.TryRound(Handle, false);
        if (roundedFrame)
        {
            Region previous = Region;
            Region = null;
            if (previous != null) previous.Dispose();
        }
        else UpdateFallbackRegion();
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (!WindowChrome.SupportsRoundedFrame || (IsHandleCreated && !roundedFrame))
            UpdateFallbackRegion();
    }

    private void UpdateFallbackRegion()
    {
        if (Width < 32 || Height < 32)
            return;
        using (GraphicsPath outline = Theme.Rounded(new Rectangle(0, 0, Width, Height), CornerRadius))
        {
            Region previous = Region;
            Region = new Region(outline);
            if (previous != null)
                previous.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (!roundedFrame) Theme.DrawWindowOutline(args.Graphics, ClientSize, CornerRadius);
    }

    protected void AddEdgePanel(Panel panel)
    {
        if (WindowChrome.SupportsRoundedFrame)
        {
            Controls.Add(panel);
            return;
        }
        EventHandler clip = delegate
        {
            if (ClientSize.Width < 3 || ClientSize.Height < 3) return;
            var interior = new Rectangle(1 - panel.Left, 1 - panel.Top, ClientSize.Width - 2, ClientSize.Height - 2);
            using (GraphicsPath path = Theme.Rounded(interior, CornerRadius - 1))
            {
                var region = new Region(path);
                region.Intersect(panel.ClientRectangle);
                Region previous = panel.Region;
                panel.Region = region;
                if (previous != null) previous.Dispose();
            }
        };
        SizeChanged += clip;
        panel.SizeChanged += clip;
        panel.LocationChanged += clip;
        panel.Disposed += delegate { SizeChanged -= clip; };
        Controls.Add(panel);
        clip(panel, EventArgs.Empty);
    }

    protected void AddWindowButtons(bool minimize, bool passive = false, Control parent = null)
    {
        Control host = parent ?? this;
        var close = new SoftButton("关闭", false, "cross")
        {
            Bounds = new Rectangle(host.ClientSize.Width - 48, 19, 30, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        if (passive)
            close.KeepTargetFocused();
        close.Click += delegate { Close(); };
        host.Controls.Add(close);
        if (minimize)
        {
            var hide = new SoftButton("最小化", false, "dash")
            {
                Bounds = new Rectangle(host.ClientSize.Width - 82, 19, 30, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            hide.Click += delegate { WindowState = FormWindowState.Minimized; };
            host.Controls.Add(hide);
        }
    }

    protected void MakeDraggable(Control surface)
    {
        surface.MouseDown += delegate(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            const int nonClientLeftDown = 0x00a1;
            const int caption = 2;
            Point screen = surface.PointToScreen(args.Location);
            int position = unchecked((screen.X & 0xffff) | (screen.Y << 16));
            ReleaseCapture();
            SendMessage(Handle, nonClientLeftDown, (IntPtr)caption, (IntPtr)position);
        };
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class FocusPanel : Panel
{
    internal FocusPanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = false;
    }
}

internal sealed class SoftButton : Button
{
    private readonly bool primary;
    private readonly string glyph;
    private bool hovered;
    private bool pressed;
    private bool current;
    internal bool Navigation { get; set; }
    internal bool Destructive { get; set; }
    internal string LeadingIcon { get; set; }
    internal bool Current
    {
        get { return current; }
        set { current = value; Invalidate(); }
    }

    internal SoftButton(string text, bool primary = false, string glyph = null)
    {
        Text = text;
        AccessibleName = text;
        Font = Theme.BodyBold;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        this.primary = primary;
        this.glyph = glyph;
    }

    internal void KeepTargetFocused()
    {
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnMouseEnter(EventArgs args)
    {
        base.OnMouseEnter(args);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        hovered = pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        pressed = args.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        Graphics graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inputFrame = Parent as SoftInputFrame;
        Color parentBackground = inputFrame == null ? Parent.BackColor : inputFrame.FillColor;
        graphics.Clear(parentBackground);
        bool emphasized = primary || Destructive || (!Navigation && Current);
        Color actionPressed = Destructive ? Theme.DangerPressed : Theme.AccentPressed;
        Color background = !Enabled || glyph != null ? parentBackground : Theme.Tint;
        Color foreground = Theme.Ink;
        if (Enabled && (hovered || pressed))
            background = pressed ? Theme.Line : Theme.TintHover;
        if (Navigation)
        {
            background = Current ? Theme.TintHover : hovered ? Theme.Tint : parentBackground;
            foreground = Current ? Theme.Accent : Theme.Ink;
        }
        if (emphasized && Enabled)
        {
            Color action = Destructive ? Theme.Danger : Theme.Accent;
            Color actionHover = Destructive ? Theme.DangerHover : Theme.AccentHover;
            background = pressed ? actionPressed : hovered ? actionHover : action;
            foreground = Color.White;
        }
        if (!Enabled)
            foreground = Theme.Muted;

        using (GraphicsPath area = Theme.Rounded(new Rectangle(1, 1, Width - 2, Height - 2), 9))
        using (var fill = new SolidBrush(background))
        {
            graphics.FillPath(fill, area);
            if (Focused && ShowFocusCues)
                using (var ring = new Pen(emphasized && Enabled ? actionPressed : Theme.Accent, 1.5f))
                    graphics.DrawPath(ring, area);
        }
        if (glyph == null)
        {
            Rectangle textBounds = Navigation ? Rectangle.Inflate(ClientRectangle, -12, 0) : ClientRectangle;
            if (LeadingIcon != null)
            {
                int inset = Navigation ? 12 : 13;
                Theme.DrawIcon(graphics, LeadingIcon, new Rectangle(inset, (Height - 16) / 2, 16, 16), foreground);
                textBounds.X = inset + 22;
                textBounds.Width = Width - textBounds.X - 6;
            }
            TextRenderer.DrawText(graphics, Text, Font, textBounds, foreground,
                (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        float x = Width / 2.0f;
        float y = Height / 2.0f;
        using (var pen = new Pen(Theme.Muted, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (glyph == "cross")
            {
                graphics.DrawLine(pen, x - 4, y - 4, x + 4, y + 4);
                graphics.DrawLine(pen, x + 4, y - 4, x - 4, y + 4);
            }
            else
                graphics.DrawLine(pen, x - 5, y, x + 5, y);
        }
    }
}

internal sealed class ShortcutKeys : Control
{
    private string[] keys = new string[0];

    internal ShortcutKeys()
    {
        DoubleBuffered = true;
        Font = Theme.Key;
        AccessibleRole = AccessibleRole.StaticText;
    }

    internal void SetShortcut(string shortcut)
    {
        keys = shortcut.Split('+');
        AccessibleName = shortcut;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        Graphics graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int left = 0;
        int spacing = (int)Math.Round(9 * graphics.DpiX / 96);
        int capHeight = Height - 3;
        foreach (string key in keys)
        {
            int width = TextRenderer.MeasureText(graphics, key, Font).Width + spacing * 3;
            var cap = new Rectangle(left, 1, width, capHeight);
            using (GraphicsPath outline = Theme.Rounded(cap, 10))
            using (var fill = new SolidBrush(Theme.Tint))
            using (var pen = new Pen(Theme.Line))
            {
                graphics.FillPath(fill, outline);
                graphics.DrawPath(pen, outline);
            }
            TextRenderer.DrawText(graphics, key, Font, cap, Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            left += width + spacing;
        }
    }
}

internal sealed class SoftMenu : ContextMenuStrip
{
    private readonly int itemWidth;
    private bool roundedFrame;

    internal SoftMenu(int itemWidth = 154)
    {
        this.itemWidth = itemWidth;
        Font = Theme.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Ink;
        Padding = new Padding(5);
        MinimumSize = new Size(itemWidth + Padding.Horizontal, 0);
        ShowImageMargin = ShowCheckMargin = false;
        Renderer = new MenuRenderer();
    }

    internal ToolStripMenuItem Add(string label, Action action)
    {
        var item = new MenuEntry(label, itemWidth);
        item.Click += delegate { action(); };
        Items.Add(item);
        return item;
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        Size preferred = base.GetPreferredSize(constrainingSize);
        int height = Padding.Vertical;
        int width = 0;
        foreach (ToolStripItem item in Items)
        {
            if (!item.Available) continue;
            Size entry = item.GetPreferredSize(constrainingSize);
            height += entry.Height + item.Margin.Vertical;
            if (!(item is ToolStripSeparator))
                width = Math.Max(width, entry.Width + item.Margin.Horizontal);
        }
        Size border = IsHandleCreated ? new Size(Width - ClientSize.Width, Height - ClientSize.Height) :
            WindowChrome.SupportsRoundedFrame ? SystemInformation.BorderSize + SystemInformation.BorderSize : Size.Empty;
        return new Size(Math.Max(preferred.Width, width + Padding.Horizontal + border.Width),
            Math.Max(preferred.Height, height + border.Height));
    }

    private sealed class MenuEntry : ToolStripMenuItem
    {
        private readonly int minimumWidth;

        internal MenuEntry(string label, int minimumWidth) : base(label)
        {
            this.minimumWidth = minimumWidth;
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size preferred = base.GetPreferredSize(constrainingSize);
            return new Size(Math.Max(minimumWidth, preferred.Width), Math.Max(34, preferred.Height));
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams style = base.CreateParams;
            if (WindowChrome.SupportsRoundedFrame) style.Style |= WindowChrome.BorderStyle;
            return style;
        }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        roundedFrame = WindowChrome.TryRound(Handle, true);
        if (roundedFrame)
        {
            Region previous = Region;
            Region = null;
            if (previous != null) previous.Dispose();
        }
        else UpdateFallbackRegion();
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (!WindowChrome.SupportsRoundedFrame || (IsHandleCreated && !roundedFrame))
            UpdateFallbackRegion();
    }

    private void UpdateFallbackRegion()
    {
        if (Width < 12 || Height < 12) return;
        using (GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width, Height), 10))
        {
            Region previous = Region;
            Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
    }

    private sealed class MenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs args)
        {
            args.Graphics.Clear(Theme.Surface);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs args)
        {
            if (!((SoftMenu)args.ToolStrip).roundedFrame)
                Theme.DrawWindowOutline(args.Graphics, args.ToolStrip.ClientSize, 10);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs args)
        {
            if (!args.Item.Selected || !args.Item.Enabled) return;
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Theme.Tint))
            using (GraphicsPath item = Theme.Rounded(new Rectangle(0, 1, args.Item.Width, args.Item.Height - 2), 7))
                args.Graphics.FillPath(fill, item);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args)
        {
            var textBounds = new Rectangle(12, 0, args.Item.Width - 24, args.Item.Height);
            TextRenderer.DrawText(args.Graphics, args.Text, args.TextFont, textBounds,
                args.Item.Enabled ? Theme.Ink : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs args)
        {
            using (var pen = new Pen(Theme.Line))
                args.Graphics.DrawLine(pen, 10, args.Item.Height / 2, args.Item.Width - 10, args.Item.Height / 2);
        }
    }
}

internal class SoftTextBox : TextBox
{
    private readonly SoftMenu editingMenu;

    internal SoftTextBox()
    {
        editingMenu = new SoftMenu(124);
        ToolStripMenuItem undo = editingMenu.Add("撤销", Undo);
        editingMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem cut = editingMenu.Add("剪切", Cut);
        ToolStripMenuItem copy = editingMenu.Add("复制", Copy);
        ToolStripMenuItem paste = editingMenu.Add("粘贴", Paste);
        ToolStripMenuItem selectAll = editingMenu.Add("全选", SelectAll);
        editingMenu.Opening += delegate
        {
            bool copyable = SelectionLength > 0 && !UseSystemPasswordChar && PasswordChar == '\0';
            undo.Enabled = !ReadOnly && CanUndo;
            cut.Enabled = !ReadOnly && copyable;
            copy.Enabled = copyable;
            paste.Enabled = !ReadOnly;
            selectAll.Enabled = TextLength > 0;
        };
        ContextMenuStrip = editingMenu;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && editingMenu != null) editingMenu.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class SoftInputFrame : Panel
{
    private Color fillColor = Theme.Tint;
    private Bitmap surface;
    private bool surfaceFocused;
    private float surfaceScale;

    internal Color FillColor
    {
        get { return fillColor; }
        set
        {
            if (fillColor == value) return;
            fillColor = value;
            ReleaseSurface();
            Invalidate();
        }
    }

    internal SoftInputFrame()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        if (Width < 3 || Height < 3) return;
        float scale = args.Graphics.DpiX / 96f;
        bool focused = ContainsFocus;
        if (surface == null || surfaceFocused != focused || surfaceScale != scale)
        {
            ReleaseSurface();
            surface = RenderSurface(scale, focused);
            surfaceFocused = focused;
            surfaceScale = scale;
        }
        args.Graphics.DrawImageUnscaled(surface, Point.Empty);
        base.OnPaint(args);
    }

    private Bitmap RenderSurface(float scale, bool focused)
    {
        const int samples = 4;
        using (var larger = new Bitmap(Width * samples, Height * samples, PixelFormat.Format32bppPArgb))
        {
            using (Graphics graphics = Graphics.FromImage(larger))
            {
                graphics.ScaleTransform(samples, samples);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float inset = scale / 2f;
                using (GraphicsPath shape = Theme.Rounded(
                    new RectangleF(inset, inset, Width - scale, Height - scale), 9f * scale))
                using (var fill = new SolidBrush(fillColor))
                using (var outline = new Pen(focused ? Theme.Accent : Theme.Line, scale))
                {
                    graphics.FillPath(fill, shape);
                    graphics.DrawPath(outline, shape);
                }
            }
            var result = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            using (var attributes = new ImageAttributes())
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(larger, new Rectangle(0, 0, Width, Height),
                    0, 0, larger.Width, larger.Height, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        ReleaseSurface();
        base.OnSizeChanged(args);
    }

    private void ReleaseSurface()
    {
        if (surface == null) return;
        surface.Dispose();
        surface = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseSurface();
        base.Dispose(disposing);
    }
}

internal sealed class SoftCheckBox : CheckBox
{
    internal SoftCheckBox(string text)
    {
        Text = AccessibleName = text;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(Parent.BackColor);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int y = (Height - 18) / 2;
        using (GraphicsPath box = Theme.Rounded(new Rectangle(2, y, 17, 17), 4))
        using (var fill = new SolidBrush(Checked ? Theme.Accent : Theme.Surface))
        using (var pen = new Pen(Theme.Accent))
        {
            args.Graphics.FillPath(fill, box);
            args.Graphics.DrawPath(pen, box);
        }
        if (Checked) Theme.DrawIcon(args.Graphics, "tick", new Rectangle(3, y + 1, 16, 16), Color.White);
        TextRenderer.DrawText(args.Graphics, Text, Font, new Rectangle(27, 0, Width - 27, Height),
            Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Theme.Accent))
            using (GraphicsPath focus = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
                args.Graphics.DrawPath(pen, focus);
    }
}

internal sealed class SoftScrollBar : Control
{
    private readonly Action<int> scroll;
    private int count;
    private int page;
    private int offset;
    private int dragDistance;

    internal SoftScrollBar(Action<int> scroll)
    {
        this.scroll = scroll;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    internal void SetPosition(int total, int visible, int first)
    {
        if (count == total && page == visible && offset == first) return;
        count = total;
        page = visible;
        offset = first;
        Invalidate();
    }

    private Rectangle Thumb()
    {
        int track = Math.Max(1, Height - 8);
        int length = Math.Min(track, Math.Max(23, track * page / Math.Max(1, count)));
        int y = 4 + (track - length) * offset / Math.Max(1, count - page);
        return new Rectangle((Width - 6) / 2, y, 6, length);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(Parent.BackColor);
        if (Height < 12 || count <= page) return;
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath shape = Theme.Rounded(Thumb(), 3))
        using (var fill = new SolidBrush(Capture ? Theme.Accent : Theme.ScrollThumb))
            args.Graphics.FillPath(fill, shape);
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || count <= page) return;
        Rectangle thumb = Thumb();
        dragDistance = args.Y >= thumb.Top && args.Y < thumb.Bottom ? args.Y - thumb.Top : thumb.Height / 2;
        Capture = true;
        ScrollTo(args.Y);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (Capture) ScrollTo(args.Y);
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button == MouseButtons.Left) Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs args)
    {
        base.OnMouseCaptureChanged(args);
        Invalidate();
    }

    private void ScrollTo(int mouseY)
    {
        int track = Math.Max(1, Height - 8 - Thumb().Height);
        int travel = Math.Max(0, Math.Min(track, mouseY - dragDistance - 4));
        scroll((int)Math.Round((double)(count - page) * travel / track));
    }
}

internal sealed class SoftListFrame : Panel
{
    private const int ScrollWidth = 13;
    private readonly SoftListBox list;
    private readonly SoftScrollBar bar;

    internal SoftListFrame(SoftListBox list)
    {
        this.list = list;
        Bounds = list.Bounds;
        Anchor = list.Anchor;
        BackColor = list.BackColor;
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        bar = new SoftScrollBar(first => { list.TopIndex = first; UpdateThumb(); });
        Controls.Add(list);
        Controls.Add(bar);
        list.DrawItem += delegate { UpdateThumb(); };
        UpdateScroll();
    }

    private int PageSize { get { return Math.Max(1, Height / Math.Max(1, list.ItemHeight)); } }

    internal void UpdateScroll()
    {
        bool overflow = list.Items.Count > PageSize;
        list.Bounds = new Rectangle(0, 0, Math.Max(1, Width - (overflow ? ScrollWidth : 0)), Height);
        bar.Bounds = new Rectangle(Width - ScrollWidth, 0, ScrollWidth, Height);
        bar.Visible = overflow;
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        bar.SetPosition(list.Items.Count, PageSize, Math.Max(0, list.TopIndex));
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (bar != null) UpdateScroll();
    }
}

internal sealed class SoftListBox : ListBox
{
    private const int ScrollStyles = 0x00300000;
    private int wheelDelta;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams style = base.CreateParams;
            style.Style &= ~ScrollStyles;
            return style;
        }
    }

    protected override void WndProc(ref Message message)
    {
        const int mouseWheel = 0x020a;
        const int styleChanging = 0x007c;
        const int windowStyle = -16;
        if (message.Msg == styleChanging && message.WParam.ToInt64() == windowStyle)
        {
            int next = Marshal.ReadInt32(message.LParam, sizeof(int));
            Marshal.WriteInt32(message.LParam, sizeof(int), next & ~ScrollStyles);
        }
        if (message.Msg != mouseWheel) { base.WndProc(ref message); return; }
        wheelDelta += unchecked((short)(message.WParam.ToInt64() >> 16));
        int steps = wheelDelta / SystemInformation.MouseWheelScrollDelta;
        wheelDelta %= SystemInformation.MouseWheelScrollDelta;
        if (steps == 0 || Items.Count == 0) return;
        int page = Math.Max(1, ClientSize.Height / ItemHeight);
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines < 0) lines = page;
        int last = Math.Max(0, Items.Count - page);
        TopIndex = Math.Min(last, Math.Max(0, TopIndex - steps * lines));
        message.Result = IntPtr.Zero;
    }
}

internal sealed class SoftNoteFrame : Panel
{
    private readonly NoteTextBox note;
    private readonly SoftScrollBar bar;
    private const int BarWidth = 13;

    internal SoftNoteFrame(Rectangle bounds, string value)
    {
        Bounds = bounds;
        BackColor = Theme.Surface;
        note = new NoteTextBox
        {
            Text = value, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink, AccessibleName = "备注"
        };
        bar = new SoftScrollBar(first => note.ScrollTo(first));
        Controls.Add(note);
        Controls.Add(bar);
        note.HandleCreated += delegate { LayoutNote(); };
        note.Scrolled += UpdateThumb;
        LayoutNote();
    }

    private int LinesInView { get { return Math.Max(1, Height / note.Font.Height); } }

    private void LayoutNote()
    {
        note.Bounds = new Rectangle(0, 0, Width, Height);
        bool overflow = note.IsHandleCreated && note.LineCount > LinesInView;
        if (overflow) note.Width = Width - BarWidth;
        bar.Bounds = new Rectangle(Width - BarWidth, 0, BarWidth, Height);
        bar.Visible = overflow;
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        if (note.IsHandleCreated) bar.SetPosition(note.LineCount, LinesInView, note.FirstLine);
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (bar != null) LayoutNote();
    }

    private sealed class NoteTextBox : SoftTextBox
    {
        private const int GetLineCount = 0x00ba;
        private const int LineScroll = 0x00b6;
        private const int GetFirstVisibleLine = 0x00ce;
        private int wheelDelta;
        internal event Action Scrolled;
        internal int LineCount { get { return SendMessage(Handle, GetLineCount, IntPtr.Zero, IntPtr.Zero).ToInt32(); } }
        internal int FirstLine { get { return SendMessage(Handle, GetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32(); } }

        internal void ScrollTo(int first)
        {
            SendMessage(Handle, LineScroll, IntPtr.Zero, (IntPtr)(first - FirstLine));
            if (Scrolled != null) Scrolled();
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            wheelDelta += args.Delta;
            int steps = wheelDelta / SystemInformation.MouseWheelScrollDelta;
            wheelDelta %= SystemInformation.MouseWheelScrollDelta;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0) lines = Math.Max(1, Height / Font.Height);
            if (steps != 0) ScrollTo(Math.Max(0, FirstLine - steps * lines));
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x000f && Scrolled != null) Scrolled();
        }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}

internal sealed class BrandMark : Control
{
    private static readonly Bitmap Artwork = LoadArtwork();

    internal BrandMark()
    {
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        args.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        args.Graphics.DrawImage(Artwork, ClientRectangle);
    }

    internal static Icon CreateIcon(Size size)
    {
        using (Stream resource = typeof(BrandMark).Assembly.GetManifestResourceStream("KeyLib.Brand.ico"))
            return new Icon(resource, size);
    }

    private static Bitmap LoadArtwork()
    {
        using (Icon icon = CreateIcon(new Size(128, 128)))
            return icon.ToBitmap();
    }
}

internal static class Theme
{
    internal static readonly Color Surface = Color.White;
    internal static readonly Color Sidebar = Color.FromArgb(245, 248, 255);
    internal static readonly Color Tint = Color.FromArgb(237, 243, 255);
    internal static readonly Color TintHover = Color.FromArgb(225, 235, 255);
    internal static readonly Color Ink = Color.FromArgb(20, 39, 70);
    internal static readonly Color Muted = Color.FromArgb(71, 94, 128);
    internal static readonly Color Accent = Color.FromArgb(25, 94, 231);
    internal static readonly Color AccentHover = Color.FromArgb(19, 79, 200);
    internal static readonly Color AccentPressed = Color.FromArgb(16, 61, 163);
    internal static readonly Color Danger = Color.FromArgb(180, 35, 48);
    internal static readonly Color DangerHover = Color.FromArgb(156, 27, 40);
    internal static readonly Color DangerPressed = Color.FromArgb(129, 21, 34);
    internal static readonly Color Line = Color.FromArgb(215, 227, 248);
    internal static readonly Color WindowOutline = Color.FromArgb(191, 197, 207);
    internal static readonly Color ScrollThumb = Color.FromArgb(130, 157, 197);

    internal static readonly Font Heading = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold);
    internal static readonly Font Subheading = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
    internal static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f);
    internal static readonly Font BodyBold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    internal static readonly Font Small = new Font("Microsoft YaHei UI", 8.5f);
    internal static readonly Font Key = new Font("Segoe UI Semibold", 12f);
    internal static readonly Font Account = new Font("Segoe UI Semibold", 12f);

    internal static void DrawWindowOutline(Graphics graphics, Size size, int radius)
    {
        if (size.Width < 2 || size.Height < 2) return;
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using (var pen = new Pen(WindowOutline))
        using (GraphicsPath outline = Rounded(new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1), radius))
            graphics.DrawPath(pen, outline);
        graphics.Restore(state);
    }

    internal static Panel FramedPanel(Rectangle bounds, Color background)
    {
        var panel = new Panel { Bounds = bounds, BackColor = background };
        EventHandler clip = delegate
        {
            if (panel.Width < 3 || panel.Height < 3) return;
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, panel.Width, panel.Height), 9))
            {
                Region previous = panel.Region;
                panel.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        };
        panel.SizeChanged += clip;
        clip(panel, EventArgs.Empty);
        panel.Paint += delegate(object sender, PaintEventArgs args)
        {
            if (panel.Width < 3 || panel.Height < 3) return;
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Line))
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, panel.Width - 1, panel.Height - 1), 9))
                args.Graphics.DrawPath(pen, path);
        };
        return panel;
    }

    internal static TextBox TextField(Control parent, Rectangle bounds, string label, int max, bool masked = false, string value = "")
    {
        var field = new SoftInputFrame { Bounds = bounds };
        var input = new SoftTextBox
        {
            // Leave room for descenders: native auto-sizing can crop '_' after unmasking a password.
            AutoSize = false,
            Bounds = new Rectangle(11, 3, bounds.Width - 22, bounds.Height - 6),
            Font = Body, BorderStyle = BorderStyle.None, MaxLength = max, Text = value ?? "",
            BackColor = Tint, ForeColor = Ink, AccessibleName = label, UseSystemPasswordChar = masked
        };
        input.GotFocus += delegate { field.Invalidate(); };
        input.LostFocus += delegate { field.Invalidate(); };
        field.MouseDown += delegate { input.Focus(); };
        field.Controls.Add(input);
        parent.Controls.Add(field);
        return input;
    }

    internal static void DrawIcon(Graphics graphics, string icon, Rectangle bounds, Color color)
    {
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
        using (var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            switch (icon)
            {
                case "vault":
                    graphics.DrawRectangle(pen, 2, 4, 12, 10);
                    graphics.DrawLine(pen, 4, 2, 12, 2);
                    graphics.DrawLine(pen, 6, 8, 10, 8);
                    break;
                case "folder":
                    graphics.DrawLines(pen, new[] { new Point(2, 13), new Point(2, 4), new Point(7, 4),
                        new Point(9, 6), new Point(14, 6), new Point(14, 13), new Point(2, 13) });
                    break;
                case "tick":
                    graphics.DrawLines(pen, new[] { new Point(4, 8), new Point(7, 11), new Point(12, 5) });
                    break;
                case "down":
                    graphics.DrawLines(pen, new[] { new Point(4, 6), new Point(8, 10), new Point(12, 6) });
                    break;
                case "search":
                    graphics.DrawEllipse(pen, 2, 2, 8, 8);
                    graphics.DrawLine(pen, 9, 9, 14, 14);
                    break;
                case "plus":
                    graphics.DrawLine(pen, 3, 8, 13, 8);
                    graphics.DrawLine(pen, 8, 3, 8, 13);
                    break;
                case "generator":
                    graphics.DrawArc(pen, 2, 2, 12, 12, 30, 290);
                    graphics.DrawLines(pen, new[] { new Point(11, 5), new Point(15, 5), new Point(15, 1) });
                    break;
                case "backup":
                    graphics.DrawLines(pen, new[] { new Point(2, 9), new Point(2, 14), new Point(14, 14), new Point(14, 9) });
                    graphics.DrawLine(pen, 8, 2, 8, 10);
                    graphics.DrawLines(pen, new[] { new Point(5, 7), new Point(8, 10), new Point(11, 7) });
                    break;
                case "settings":
                    graphics.DrawLine(pen, 2, 4, 14, 4);
                    graphics.DrawLine(pen, 2, 8, 14, 8);
                    graphics.DrawLine(pen, 2, 12, 14, 12);
                    graphics.DrawLine(pen, 6, 2, 6, 6);
                    graphics.DrawLine(pen, 10, 6, 10, 10);
                    graphics.DrawLine(pen, 5, 10, 5, 14);
                    break;
                case "lock":
                    graphics.DrawArc(pen, 4, 1, 8, 9, 180, 180);
                    graphics.DrawLine(pen, 4, 5, 4, 7);
                    graphics.DrawLine(pen, 12, 5, 12, 7);
                    graphics.DrawRectangle(pen, 2, 7, 12, 7);
                    break;
            }
        }
        graphics.Restore(state);
    }

    internal static Label Label(string text, int x, int y, int width, int height, Font font, Color color)
    {
        return new Label
        {
            Text = text, Bounds = new Rectangle(x, y, width, height),
            TextAlign = ContentAlignment.MiddleLeft, Font = font, ForeColor = color,
            UseMnemonic = false
        };
    }

    internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var shape = new GraphicsPath();
        float size = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        shape.AddArc(rectangle.X, rectangle.Y, size, size, 180, 90);
        shape.AddArc(rectangle.Right - size, rectangle.Y, size, size, 270, 90);
        shape.AddArc(rectangle.Right - size, rectangle.Bottom - size, size, size, 0, 90);
        shape.AddArc(rectangle.X, rectangle.Bottom - size, size, size, 90, 90);
        shape.CloseFigure();
        return shape;
    }
}
