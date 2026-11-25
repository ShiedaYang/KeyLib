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
                NoticeDialog.ShowMessage("KeyLib 已在运行", "从任务栏通知区域打开已运行的 KeyLib。");
                return;
            }
            using (lease)
            using (var main = new MainWindow(directory))
                Application.Run(main);
        }
        catch (Exception error)
        {
            NoticeDialog.ShowMessage("无法启动 KeyLib", error.Message);
        }
    }
}
