using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
internal class SoftWindow : Form
{
    internal SoftWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        DoubleBuffered = true;
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (Width < 32 || Height < 32)
            return;
        using (GraphicsPath outline = Theme.Rounded(new Rectangle(0, 0, Width, Height), 18))
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
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath outline = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 18))
        using (var pen = new Pen(Theme.Line))
            args.Graphics.DrawPath(pen, outline);
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

internal sealed class SoftButton : Button
{
    private readonly bool primary;
    private readonly string glyph;
    private bool hovered;
    private bool pressed;
    internal bool Navigation { get; set; }
    internal bool Current { get; set; }

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
        graphics.Clear(Parent.BackColor);
        Color background = glyph != null ? Theme.Surface : Theme.Tint;
        Color foreground = Theme.Ink;
        if (hovered || pressed)
            background = pressed ? Theme.Line : Theme.TintHover;
        if (Navigation)
        {
            background = Current ? Theme.TintHover : hovered ? Theme.Tint : Parent.BackColor;
            foreground = Current ? Theme.Accent : Theme.Ink;
        }
        if (primary && Enabled)
        {
            background = pressed ? Theme.AccentPressed : hovered ? Theme.AccentHover : Theme.Accent;
            foreground = Color.White;
        }
        if (!Enabled)
            foreground = Theme.Muted;

        using (GraphicsPath area = Theme.Rounded(new Rectangle(1, 1, Width - 2, Height - 2), 9))
        using (var fill = new SolidBrush(background))
        {
            graphics.FillPath(fill, area);
            if (Focused && ShowFocusCues)
                using (var ring = new Pen(Theme.Accent, 1.5f))
                    graphics.DrawPath(ring, area);
        }
        if (glyph == null)
        {
            Rectangle textBounds = Navigation ? Rectangle.Inflate(ClientRectangle, -12, 0) : ClientRectangle;
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

internal sealed class BrandMark : Control
{
    internal BrandMark()
    {
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        PaintMark(args.Graphics, ClientRectangle);
    }

    internal static Icon CreateIcon()
    {
        using (var image = new Bitmap(32, 32))
        using (var graphics = Graphics.FromImage(image))
        {
            PaintMark(graphics, new Rectangle(0, 0, 32, 32));
            IntPtr handle = image.GetHicon();
            try
            {
                using (Icon original = Icon.FromHandle(handle)) return (Icon)original.Clone();
            }
            finally { DestroyIcon(handle); }
        }
    }

    private static void PaintMark(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath shape = Theme.Rounded(bounds, 10))
        using (var fill = new SolidBrush(Theme.Accent))
            graphics.FillPath(fill, shape);

        float x = bounds.Left + bounds.Width / 2f, y = bounds.Top + bounds.Height / 2f;
        using (var pen = new Pen(Color.White, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawLine(pen, x - 6, y - 8, x - 6, y + 8);
            graphics.DrawLine(pen, x - 6, y + 2, x + 5, y - 8);
            graphics.DrawLine(pen, x - 2, y - 1, x + 6, y + 8);
        }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

internal static class Theme
{
    internal static readonly Color Surface = Color.White;
    internal static readonly Color Sidebar = Color.FromArgb(246, 249, 254);
    internal static readonly Color Tint = Color.FromArgb(239, 245, 255);
    internal static readonly Color TintHover = Color.FromArgb(226, 238, 255);
    internal static readonly Color Ink = Color.FromArgb(22, 40, 65);
    internal static readonly Color Muted = Color.FromArgb(75, 96, 125);
    internal static readonly Color Accent = Color.FromArgb(22, 84, 194);
    internal static readonly Color AccentHover = Color.FromArgb(19, 70, 168);
    internal static readonly Color AccentPressed = Color.FromArgb(16, 57, 137);
    internal static readonly Color Line = Color.FromArgb(219, 229, 243);

    internal static readonly Font Heading = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold);
    internal static readonly Font Subheading = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
    internal static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f);
    internal static readonly Font BodyBold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    internal static readonly Font Small = new Font("Microsoft YaHei UI", 8.5f);
    internal static readonly Font Key = new Font("Segoe UI Semibold", 12f);
    internal static readonly Font Account = new Font("Segoe UI Semibold", 12f);

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
