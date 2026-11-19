using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class SettingsDialog : VaultDialog
{
    internal SettingsDialog(bool connected, Action<bool> changeBridge) : base("设置", 498, 300)
    {
        Controls.Add(Theme.Label("统一快捷键", 24, 63, 310, 26, Theme.BodyBold, Theme.Ink));
        var caps = new ShortcutKeys { Bounds = new Rectangle(24, 98, 300, 38) };
        caps.SetShortcut(MainWindow.Shortcut);
        Controls.Add(caps);
        Controls.Add(Theme.Label("Windows、Chrome 和 Edge 使用同一个组合键。", 24, 141, 453, 29, Theme.Small, Theme.Muted));

        var divider = new Panel { Bounds = new Rectangle(24, 186, 450, 1), BackColor = Theme.Line };
        Controls.Add(divider);
        Controls.Add(Theme.Label("浏览器连接", 24, 195, 238, 27, Theme.BodyBold, Theme.Ink));
        var toggle = new SoftButton(connected ? "关闭本机连接" : "启用本机连接", !connected)
        {
            Bounds = new Rectangle(330, 196, 144, 35)
        };
        Controls.Add(toggle);
        var status = Theme.Label(connected ? "本机连接已启用。" : "使用 Chrome 或 Edge 扩展前，先启用本机连接。",
            24, 245, 450, 37, Theme.Small, Theme.Muted);
        Controls.Add(status);
        toggle.Click += delegate
        {
            try
            {
                changeBridge(!connected);
                connected = !connected;
                toggle.Text = connected ? "关闭本机连接" : "启用本机连接";
                toggle.AccessibleName = toggle.Text;
                status.Text = connected ? "本机连接已启用。请加载发行包中的扩展文件夹。" : "本机连接已关闭。";
            }
            catch (Exception error) { status.Text = error.Message; }
        };
    }
}
