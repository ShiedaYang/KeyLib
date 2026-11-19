using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CoreTest
{
    internal static void Run(string report)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string password = "Offline-demo-master-with-length";
        const string secret = "UniquelyIdentifiableTestSecret!8";
        string first = Path.Combine(dir, "primary.softvault");
        string second = Path.Combine(dir, "received.softvault");
        try
        {
            using (VaultSession vault = VaultSession.Open(first, password, true))
            {
                vault.Save(new VaultEntry { Id = Guid.NewGuid().ToString("N"), Name = "Sample",
                    User = "SampleLogin", Password = secret, Origin = "https://example.com" });
                Assert(!Encoding.UTF8.GetString(vault.Snapshot()).Contains(secret), "no readable password in vault");
                Assert(!Encoding.UTF8.GetString(vault.Snapshot()).Contains("SampleLogin"), "no readable username in vault");
                using (var reopen = VaultSession.Open(first, password, false))
                    Assert(reopen.Entries.Single().Password == secret, "correct master can decrypt saved entry");
                Reject(() => VaultSession.Open(first, "incorrect-password", false), "wrong master rejected");
                byte[] corrupted = vault.Snapshot();
                corrupted[corrupted.Length - 1] ^= 1;
                string invalid = Path.Combine(dir, "modified.softvault");
                VaultFormat.WriteAtomic(invalid, corrupted);
                Reject(() => VaultSession.Open(invalid, password, false), "altered vault rejected");
                File.AppendAllText(report, "PASS encrypted persistence, open, wrong password, tamper\r\n");

                string[] generated = Enumerable.Range(0, 40).Select(_ => Passwords.Generate(24, true)).ToArray();
                Assert(generated.Distinct().Count() == 40, "fresh random samples");
                Assert(generated.All(item => item.Length == 24 && item.Any(char.IsUpper) && item.Any(char.IsLower)
                    && item.Any(char.IsDigit) && item.Any(c => !char.IsLetterOrDigit(c))), "generated policy");
                File.AppendAllText(report, "PASS password generator\r\n");

                using (var receiver = new TransferReceiver(IPAddress.Loopback))
                {
                    Task incoming = receiver.Receive(second, TimeSpan.FromSeconds(12));
                    TransferReceiver.Send(receiver.Code, vault.Snapshot()).GetAwaiter().GetResult();
                    incoming.GetAwaiter().GetResult();
                }
                using (var imported = VaultSession.Open(second, password, false))
                {
                    Assert(imported.Entries.Single().Password == secret, "received backup restores");
                    Assert(vault.Merge(imported.Entries) == 0, "merge avoids duplicates");
                    using (var recipient = VaultSession.Open(Path.Combine(dir, "merged.softvault"), "Recipient master passphrase", true))
                    {
                        Assert(recipient.Merge(imported.Entries) == 1, "backup can be re-encrypted with recipient master");
                        Assert(recipient.Entries[0].Password == secret, "merged entry intact");
                    }
                }
                File.AppendAllText(report, "PASS encrypted transfer over real TCP loopback, receipt, restore and merge\r\n");
            }
            Assert(VaultFormat.WebOrigin("https://EXAMPLE.com:8443/path?q=1") == "https://example.com:8443", "origin scope");
            Assert(VaultFormat.WebOrigin("http://example.com").Length == 0, "non-HTTPS rejected");
            using (var bridge = new BrowserBridge(request => new { accepted = request.origin == "https://example.com" }))
            using (var native = Process.Start(new ProcessStartInfo(Application.ExecutablePath, BrowserBridge.ExtensionOrigin)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true
            }))
            {
                Binary.WriteFrame(native.StandardInput.BaseStream, Encoding.UTF8.GetBytes("{\"op\":\"list\",\"origin\":\"https://example.com\"}"));
                string response = Encoding.UTF8.GetString(Binary.ReadFrame(native.StandardOutput.BaseStream, 4096));
                Assert(response.Contains("\"accepted\":true"), "native stdio and per-user named pipe roundtrip: " + response);
                Assert(native.WaitForExit(5000), "native process exits after request");
            }
            File.AppendAllText(report, "PASS browser origin validation and native messaging IPC round trip\r\nPASS core suite\r\n");
        }
        catch (Exception error) { File.AppendAllText(report, "FAIL " + error + "\r\n"); }
        finally
        {
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }

    private static void Assert(bool ok, string test)
    {
        if (!ok) throw new InvalidOperationException(test);
    }

    private static void Reject(Func<VaultSession> callback, string test)
    {
        try { using (callback()) { } }
        catch (CryptographicException) { return; }
        throw new InvalidOperationException(test);
    }
}
