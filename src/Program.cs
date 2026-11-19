using System;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == BrowserBridge.ExtensionOrigin)
        {
            BrowserBridge.RunNativeHost();
            return;
        }
        if (args.Length == 2 && args[0] == "--core-test")
        {
            CoreTest.Run(args[1]);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(appData, "KeyLib");
        string legacy = Path.Combine(appData, "SoftVault");
        if (!File.Exists(Path.Combine(directory, "vault.softvault")) && File.Exists(Path.Combine(legacy, "vault.softvault")))
            directory = legacy;
        if (args.Length == 2 && args[0] == "--test-ui") directory = Path.GetFullPath(args[1]);
        try
        {
            Directory.CreateDirectory(directory);
            FileStream lease;
            try { lease = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                MessageBox.Show("已有一个KeyLib实例正在运行。", "KeyLib");
                return;
            }
            using (lease)
            using (var main = new MainWindow(directory))
                Application.Run(main);
        }
        catch (Exception error)
        {
            MessageBox.Show("无法启动KeyLib。\r\n" + error.Message, "KeyLib", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
