using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

internal sealed class TransferReceiver : IDisposable
{
    private static readonly byte[] BackupAd = Encoding.ASCII.GetBytes("SoftVault LAN backup v1");
    private static readonly byte[] ReceiptAd = Encoding.ASCII.GetBytes("SoftVault LAN receipt v1");
    private readonly TcpListener listener;
    private readonly byte[] key = Sodium.Random(Sodium.KeyBytes);
    private readonly object gate = new object();
    private TcpClient peer;
    private bool disposed;
    internal readonly string Code;

    internal TransferReceiver(IPAddress address)
    {
        listener = new TcpListener(address, 0);
        listener.ExclusiveAddressUse = true;
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] codeData = VaultFormat.Join(address.GetAddressBytes(), new[] { (byte)(port >> 8), (byte)port }, key);
        Code = "SV1-" + Convert.ToBase64String(codeData).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Sodium.Clear(codeData);
    }

    internal async Task Receive(string path, TimeSpan timeout)
    {
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        if (await Task.WhenAny(accept, Task.Delay(timeout)).ConfigureAwait(false) != accept)
        {
            Dispose();
            try { await accept.ConfigureAwait(false); } catch (SocketException) { } catch (ObjectDisposedException) { }
            throw new TimeoutException("配对码已过期。");
        }

        using (TcpClient client = await accept.ConfigureAwait(false))
        {
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException();
                peer = client;
                listener.Stop();
            }
            byte[] sessionKey;
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException();
                sessionKey = (byte[])key.Clone();
            }
            try
            {
                await Task.Run(() =>
                {
                    using (var network = client.GetStream())
                    using (var deadline = new System.Threading.Timer(_ => client.Close(), null, 15000, -1))
                    {
                        network.ReadTimeout = network.WriteTimeout = 12000;
                        byte[] snapshot = Open(Binary.ReadFrame(network, VaultFormat.Limit + 40), sessionKey, BackupAd);
                        VaultFormat.Salt(snapshot);
                        byte[] digest;
                        using (var sha = SHA256.Create()) digest = sha.ComputeHash(snapshot);
                        lock (gate)
                        {
                            if (disposed) throw new OperationCanceledException();
                            VaultFormat.WriteAtomic(path, snapshot);
                        }
                        Binary.WriteFrame(network, Seal(digest, sessionKey, ReceiptAd));
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                Sodium.Clear(sessionKey);
                Dispose();
            }
        }
    }

    internal static async Task Send(string code, byte[] snapshot)
    {
        if (code == null || !code.Trim().StartsWith("SV1-", StringComparison.Ordinal))
            throw new FormatException("请输入接收设备显示的配对码。");
        string body = code.Trim().Substring(4).Replace('-', '+').Replace('_', '/');
        if (body.Length != 51) throw new FormatException("配对码长度无效。");
        byte[] payload = Convert.FromBase64String(body + "=");
        byte[] sessionKey = VaultFormat.Slice(payload, 6, Sodium.KeyBytes);
        IPAddress address = new IPAddress(VaultFormat.Slice(payload, 0, 4));
        int port = payload[4] * 256 + payload[5];
        Sodium.Clear(payload);
        try
        {
            if ((!IsPrivate(address) && !IPAddress.IsLoopback(address)) || port < 1024)
                throw new FormatException("配对码必须指向局域网设备。");
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                Task connect = client.ConnectAsync(address, port);
                if (await Task.WhenAny(connect, Task.Delay(6000)).ConfigureAwait(false) != connect)
                {
                    client.Close();
                    throw new TimeoutException("连接超时，请检查两台设备的网络。");
                }
                await connect.ConfigureAwait(false);
                await Task.Run(() =>
                {
                    using (var network = client.GetStream())
                    using (var deadline = new System.Threading.Timer(_ => client.Close(), null, 15000, -1))
                    {
                        network.ReadTimeout = network.WriteTimeout = 12000;
                        Binary.WriteFrame(network, Seal(snapshot, sessionKey, BackupAd));
                        byte[] receipt = Open(Binary.ReadFrame(network, 72), sessionKey, ReceiptAd);
                        using (var sha = SHA256.Create())
                            if (!Sodium.Equal(sha.ComputeHash(snapshot), receipt))
                                throw new CryptographicException("无法确认接收设备已保存备份。");
                    }
                }).ConfigureAwait(false);
            }
        }
        finally { Sodium.Clear(sessionKey); }
    }

    internal static IPAddress[] Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address).Where(IsPrivate).Distinct().ToArray();
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] octets = address.GetAddressBytes();
        return octets[0] == 10 || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
            (octets[0] == 192 && octets[1] == 168);
    }

    private static byte[] Seal(byte[] bytes, byte[] secret, byte[] ad)
    {
        byte[] nonce = Sodium.Random(Sodium.NonceBytes);
        return VaultFormat.Join(nonce, Sodium.Encrypt(bytes, ad, nonce, secret));
    }

    private static byte[] Open(byte[] packet, byte[] secret, byte[] ad)
    {
        if (packet.Length < Sodium.NonceBytes + Sodium.TagBytes) throw new InvalidDataException("备份包不完整。");
        return Sodium.Decrypt(VaultFormat.Slice(packet, Sodium.NonceBytes, packet.Length - Sodium.NonceBytes),
            ad, VaultFormat.Slice(packet, 0, Sodium.NonceBytes), secret);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            listener.Stop();
            if (peer != null) peer.Close();
            Sodium.Clear(key);
        }
    }
}
