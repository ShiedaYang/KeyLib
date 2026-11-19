using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

public sealed class BridgeRequest
{
    public string op { get; set; }
    public string origin { get; set; }
    public string id { get; set; }
    public string user { get; set; }
    public string password { get; set; }
    public int length { get; set; }
    public bool symbols { get; set; }
}

internal sealed class BrowserBridge : IDisposable
{
    internal const string ExtensionOrigin = "chrome-extension://ippimkiciffinfniconpbejooapmjpbk/";
    internal const string Host = "org.softvault.local";
    private const int RequestLimit = 32768;
    private const int ResponseLimit = 384 * 1024;
    private readonly object gate = new object();
    private readonly Thread thread;
    private readonly Func<BridgeRequest, object> handle;
    private NamedPipeServerStream listening;
    private bool disposed;

    internal BrowserBridge(Func<BridgeRequest, object> handle)
    {
        this.handle = handle;
        thread = new Thread(Serve) { IsBackground = true, Name = "KeyLib browser bridge" };
        thread.Start();
    }

    private static SecurityIdentifier UserSid()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.User;
    }

    private static string PipeName() { return Host + "." + UserSid().Value; }

    internal static void Register(string manifestPath, string appPath)
    {
        var manifest = new { name = Host, description = "KeyLib本机通道", path = appPath,
            type = "stdio", allowed_origins = new[] { ExtensionOrigin } };
        VaultFormat.WriteAtomic(manifestPath, Encoding.UTF8.GetBytes(VaultFormat.Json().Serialize(manifest)));
        foreach (string browser in new[] { @"Software\Google\Chrome", @"Software\Microsoft\Edge" })
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(browser + @"\NativeMessagingHosts\" + Host))
                key.SetValue("", manifestPath, RegistryValueKind.String);
        }
    }

    internal static void Unregister(string manifestPath)
    {
        foreach (string browser in new[] { @"Software\Google\Chrome", @"Software\Microsoft\Edge" })
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(browser + @"\NativeMessagingHosts", true))
                if (key != null) key.DeleteSubKey(Host, false);
        }
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
    }

    internal static void RunNativeHost()
    {
        using (Stream input = Console.OpenStandardInput())
        using (Stream output = Console.OpenStandardOutput())
        {
            try
            {
                byte[] request = Binary.ReadFrame(input, RequestLimit);
                using (var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut,
                    PipeOptions.None, TokenImpersonationLevel.Identification))
                using (var timeout = new Timer(_ => pipe.Dispose(), null, 4500, Timeout.Infinite))
                {
                    pipe.Connect(1500);
                    Binary.WriteFrame(pipe, request);
                    Binary.WriteFrame(output, Binary.ReadFrame(pipe, ResponseLimit));
                }
            }
            catch (Exception)
            {
                try { Binary.WriteFrame(output, Encoding.UTF8.GetBytes("{\"error\":\"请先运行并解锁KeyLib，再启用浏览器连接。\"}")); }
                catch (IOException) { }
            }
        }
    }

    private void Serve()
    {
        SecurityIdentifier sid = UserSid();
        var rules = new PipeSecurity();
        rules.SetAccessRuleProtection(true, false);
        rules.SetOwner(sid);
        rules.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        rules.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (true)
        {
            NamedPipeServerStream pipe;
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.None, 4096, 4096, rules);
                }
                catch (IOException) { return; }
                listening = pipe;
            }

            using (pipe)
            {
                try
                {
                    pipe.WaitForConnection();
                    using (var timeout = new Timer(_ => pipe.Dispose(), null, 4000, Timeout.Infinite))
                    {
                        byte[] bytes = Binary.ReadFrame(pipe, RequestLimit);
                        var request = VaultFormat.Json().Deserialize<BridgeRequest>(Encoding.UTF8.GetString(bytes));
                        byte[] response = Encoding.UTF8.GetBytes(VaultFormat.Json().Serialize(handle(request)));
                        if (response.Length > ResponseLimit)
                            response = Encoding.UTF8.GetBytes("{\"error\":\"匹配结果过多。\"}");
                        Binary.WriteFrame(pipe, response);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (UnauthorizedAccessException) { }
            }
            lock (gate) { if (listening == pipe) listening = null; }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            if (listening != null) listening.Dispose();
        }
    }
}
