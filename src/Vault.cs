using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]

internal static class Sodium
{
    internal const int KeyBytes = 32;
    internal const int NonceBytes = 24;
    internal const int TagBytes = 16;
    private const string Library = "libsodium.dll";

    static Sodium()
    {
        if (sodium_init() < 0)
            throw new CryptographicException("无法初始化加密库。");
    }

    internal static byte[] Random(int size)
    {
        var result = new byte[size];
        randombytes_buf(result, (UIntPtr)size);
        return result;
    }

    internal static int Uniform(int bound) { return (int)randombytes_uniform((uint)bound); }

    internal static byte[] Derive(string password, byte[] salt)
    {
        byte[] raw = Encoding.UTF8.GetBytes(password);
        byte[] key = new byte[KeyBytes];
        try
        {
            // Argon2id 1.3, three passes, 64 MiB. These parameters are fixed for vault format 1.
            if (crypto_pwhash(key, (ulong)key.Length, raw, (ulong)raw.Length, salt, 3, (UIntPtr)(64 * 1024 * 1024), 2) != 0)
            {
                Clear(key);
                throw new CryptographicException("无法派生密钥，请检查可用内存。");
            }
            return key;
        }
        finally { Clear(raw); }
    }

    internal static byte[] Encrypt(byte[] message, byte[] header, byte[] nonce, byte[] key)
    {
        byte[] cipher = new byte[message.Length + TagBytes];
        ulong size;
        if (crypto_aead_xchacha20poly1305_ietf_encrypt(cipher, out size, message, (ulong)message.Length,
            header, (ulong)header.Length, IntPtr.Zero, nonce, key) != 0 || size != (ulong)cipher.Length)
            throw new CryptographicException("加密失败。");
        return cipher;
    }

    internal static byte[] Decrypt(byte[] cipher, byte[] header, byte[] nonce, byte[] key)
    {
        if (cipher.Length < TagBytes)
            throw new InvalidDataException("加密数据不完整。");
        byte[] message = new byte[cipher.Length - TagBytes];
        ulong size;
        if (crypto_aead_xchacha20poly1305_ietf_decrypt(message, out size, IntPtr.Zero, cipher, (ulong)cipher.Length,
            header, (ulong)header.Length, nonce, key) != 0 || size != (ulong)message.Length)
        {
            Clear(message);
            throw new CryptographicException("密码错误，或文件无法通过完整性验证。");
        }
        return message;
    }

    internal static bool Equal(byte[] left, byte[] right)
    {
        return left.Length == right.Length && sodium_memcmp(left, right, (UIntPtr)left.Length) == 0;
    }

    internal static void Clear(byte[] bytes)
    {
        if (bytes != null && bytes.Length > 0)
            sodium_memzero(bytes, (UIntPtr)bytes.Length);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sodium_init();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void randombytes_buf([Out] byte[] output, UIntPtr size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint randombytes_uniform(uint bound);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void sodium_memzero([In, Out] byte[] value, UIntPtr size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sodium_memcmp(byte[] first, byte[] second, UIntPtr size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_pwhash([Out] byte[] output, ulong outputSize, byte[] password, ulong passwordSize,
        byte[] salt, ulong opsLimit, UIntPtr memLimit, int algorithm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_aead_xchacha20poly1305_ietf_encrypt([Out] byte[] output, out ulong outputSize,
        byte[] message, ulong messageSize, byte[] ad, ulong adSize, IntPtr secretNonce, byte[] nonce, byte[] key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_aead_xchacha20poly1305_ietf_decrypt([Out] byte[] output, out ulong outputSize,
        IntPtr secretNonce, byte[] cipher, ulong cipherSize, byte[] ad, ulong adSize, byte[] nonce, byte[] key);
}

public sealed class VaultEntry
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
    public string AppPath { get; set; }
    public string AppHash { get; set; }
    public bool DisableAppDetection { get; set; }
    public string Origin { get; set; }
    public string Notes { get; set; }

    internal VaultEntry Copy() { return (VaultEntry)MemberwiseClone(); }
    public override string ToString() { return Name ?? ""; }

    internal static IOrderedEnumerable<VaultEntry> QuickFillOrder(IEnumerable<VaultEntry> matches)
    {
        return matches.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    internal void Validate()
    {
        Guid id;
        if (!Guid.TryParse(Id, out id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 180 ||
            User == null || User.Length > 512 || string.IsNullOrEmpty(Password) || Password.Length > 2048 ||
            (AppPath ?? "").Length > 2048 || (AppHash ?? "").Length > 64 ||
            (Origin ?? "").Length > 512 || (Notes ?? "").Length > 4000)
            throw new InvalidDataException("条目字段缺失或超出长度限制。");
        if (!string.IsNullOrEmpty(Origin) && Origin != VaultFormat.WebOrigin(Origin))
            throw new InvalidDataException("网址必须为有效的 HTTPS 来源。");
        if (string.IsNullOrEmpty(AppPath) != string.IsNullOrEmpty(AppHash) ||
            (!string.IsNullOrEmpty(AppPath) && (!Path.IsPathRooted(AppPath) ||
            AppHash.Length != 64 || AppHash.Any(c => !Uri.IsHexDigit(c)))))
            throw new InvalidDataException("应用绑定无效。");
    }

    internal bool SameData(VaultEntry other)
    {
        return Name == other.Name && User == other.User && Password == other.Password &&
            (AppPath ?? "") == (other.AppPath ?? "") && (AppHash ?? "") == (other.AppHash ?? "") &&
            DisableAppDetection == other.DisableAppDetection &&
            (Origin ?? "") == (other.Origin ?? "") && (Notes ?? "") == (other.Notes ?? "");
    }
}

internal static class VaultFormat
{
    internal const int Limit = 8 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SVLT0001");
    private const int HeaderSize = 8 + 16 + Sodium.NonceBytes;

    internal static JavaScriptSerializer Json()
    {
        return new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 12 };
    }

    internal static string WebOrigin(string url)
    {
        Uri uri;
        if (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
            !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo))
        {
            string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost + "]" : uri.IdnHost;
            return "https://" + host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        }
        return "";
    }

    internal static byte[] Read(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > Limit || input.Length < HeaderSize + Sodium.TagBytes + 2)
                throw new InvalidDataException("密码库的文件大小无效。");
            return Binary.ReadExactly(input, (int)input.Length);
        }
    }

    internal static byte[] Salt(byte[] envelope)
    {
        if (envelope.Length < HeaderSize + Sodium.TagBytes + 2 || envelope.Length > Limit ||
            !envelope.Take(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("无法识别此加密密码库格式。");
        return Slice(envelope, Magic.Length, 16);
    }

    internal static byte[] Encode(List<VaultEntry> entries, byte[] salt, byte[] key)
    {
        foreach (VaultEntry entry in entries) entry.Validate();
        byte[] plain = Encoding.UTF8.GetBytes(Json().Serialize(entries));
        try
        {
            if (plain.Length > Limit - HeaderSize - Sodium.TagBytes)
                throw new InvalidDataException("密码库达到大小上限。");
            byte[] nonce = Sodium.Random(Sodium.NonceBytes);
            byte[] header = Join(Magic, salt, nonce);
            return Join(header, Sodium.Encrypt(plain, header, nonce, key));
        }
        finally { Sodium.Clear(plain); }
    }

    internal static List<VaultEntry> Decode(byte[] envelope, byte[] key)
    {
        Salt(envelope);
        byte[] header = Slice(envelope, 0, HeaderSize);
        byte[] nonce = Slice(envelope, HeaderSize - Sodium.NonceBytes, Sodium.NonceBytes);
        byte[] plain = Sodium.Decrypt(Slice(envelope, HeaderSize, envelope.Length - HeaderSize), header, nonce, key);
        try
        {
            var entries = Json().Deserialize<List<VaultEntry>>(Encoding.UTF8.GetString(plain));
            if (entries == null || entries.Count > 20000)
                throw new InvalidDataException("条目列表无效。");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VaultEntry entry in entries)
            {
                if (entry == null || !ids.Add(entry.Id)) throw new InvalidDataException("条目标识重复。");
                entry.Validate();
            }
            return entries;
        }
        finally { Sodium.Clear(plain); }
    }

    internal static byte[] Join(params byte[][] values)
    {
        var result = new byte[values.Sum(v => v.Length)];
        int offset = 0;
        foreach (byte[] value in values)
        {
            Buffer.BlockCopy(value, 0, result, offset, value.Length);
            offset += value.Length;
        }
        return result;
    }

    internal static byte[] Slice(byte[] source, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(source, offset, result, 0, count);
        return result;
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(staging, path, null);
            else File.Move(staging, path);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}

internal sealed class VaultSession : IDisposable
{
    private readonly string path;
    private readonly byte[] salt;
    private byte[] key;
    internal List<VaultEntry> Entries { get; private set; }

    private VaultSession(string path, byte[] salt, byte[] key, List<VaultEntry> entries)
    {
        this.path = path;
        this.salt = salt;
        this.key = key;
        Entries = entries;
    }

    internal static VaultSession Open(string path, string password, bool create)
    {
        byte[] envelope = create ? null : VaultFormat.Read(path);
        if (create && (password.Length < 12 || password.Length > 256))
            throw new ArgumentException("主密码请输入 12 至 256 个字符。");
        if (create && File.Exists(path)) throw new IOException("这个密码库已经存在。");
        byte[] salt = create ? Sodium.Random(16) : VaultFormat.Salt(envelope);
        byte[] key = Sodium.Derive(password, salt);
        try
        {
            var entries = create ? new List<VaultEntry>() : VaultFormat.Decode(envelope, key);
            if (create) VaultFormat.WriteAtomic(path, VaultFormat.Encode(entries, salt, key));
            return new VaultSession(path, salt, key, entries);
        }
        catch { Sodium.Clear(key); throw; }
    }

    internal void Save(VaultEntry entry)
    {
        var next = Entries.Select(item => item.Copy()).ToList();
        int index = next.FindIndex(item => item.Id == entry.Id);
        if (index >= 0) next[index] = entry.Copy();
        else next.Add(entry.Copy());
        Commit(next);
    }

    internal void Remove(string id)
    {
        Commit(Entries.Where(item => item.Id != id).Select(item => item.Copy()).ToList());
    }

    internal void UpdateAppHash(string path, string hash)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("应用校验值无效。");
        var next = Entries.Select(item => item.Copy()).ToList();
        bool changed = false;
        foreach (VaultEntry entry in next)
        {
            if (!string.Equals(entry.AppPath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.AppHash, hash, StringComparison.OrdinalIgnoreCase)) continue;
            entry.AppHash = hash;
            changed = true;
        }
        if (changed) Commit(next);
    }

    internal int Merge(List<VaultEntry> incoming)
    {
        var next = Entries.Select(item => item.Copy()).ToList();
        foreach (VaultEntry item in incoming)
        {
            if (next.Any(entry => entry.SameData(item))) continue;
            var copy = item.Copy();
            if (next.Any(entry => entry.Id == copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
            next.Add(copy);
        }
        int added = next.Count - Entries.Count;
        if (added != 0) Commit(next);
        return added;
    }

    internal byte[] Snapshot() { return VaultFormat.Read(path); }

    internal bool IsPrimaryPath(string candidate)
    {
        return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    private void Commit(List<VaultEntry> next)
    {
        if (key == null) throw new InvalidOperationException("密码库已锁定。");
        VaultFormat.WriteAtomic(path, VaultFormat.Encode(next, salt, key));
        Entries = next;
    }

    public void Dispose()
    {
        Sodium.Clear(key);
        key = null;
        Entries.Clear();
    }
}

internal static class Passwords
{
    internal static string Generate(int length, bool symbols)
    {
        if (length < 12 || length > 96) throw new ArgumentOutOfRangeException("length");
        string[] groups = symbols
            ? new[] { "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz", "0123456789", "!@#$%^&*-_+=?." }
            : new[] { "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz", "0123456789" };
        string alphabet = string.Concat(groups);
        var value = new char[length];
        string generated;
        do
        {
            for (int index = 0; index < value.Length; index++) value[index] = alphabet[Sodium.Uniform(alphabet.Length)];
            generated = new string(value);
        }
        while (groups.Any(group => !generated.Any(character => group.IndexOf(character) >= 0)));
        Array.Clear(value, 0, value.Length);
        return generated;
    }
}

internal static class Binary
{
    internal static byte[] ReadExactly(Stream stream, int count)
    {
        var data = new byte[count];
        for (int offset = 0; offset < count;)
        {
            int size = stream.Read(data, offset, count - offset);
            if (size == 0) throw new EndOfStreamException("数据传输中断。");
            offset += size;
        }
        return data;
    }

    internal static void WriteFrame(Stream stream, byte[] data)
    {
        byte[] size = BitConverter.GetBytes(data.Length);
        stream.Write(size, 0, size.Length);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    internal static byte[] ReadFrame(Stream stream, int maximum)
    {
        int size = BitConverter.ToInt32(ReadExactly(stream, 4), 0);
        if (size <= 0 || size > maximum) throw new InvalidDataException("消息长度无效。");
        return ReadExactly(stream, size);
    }
}
