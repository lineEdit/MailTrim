using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailTrim.Core;

namespace MailTrim.App;

public sealed class MessageCache
{
    private readonly string directory;
    private readonly byte[] entropy;
    private readonly object gate = new();
    public MessageCache(string root, Guid profile)
    {
        directory = Path.Combine(root, "ReaderCache", profile.ToString("N"));
        entropy = profile.ToByteArray();
    }
    private string PathFor(string url) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".bin");
    public List<ReaderLetter> List()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*.bin").Select(ReadFile).Where(x => x is not null).Select(x => x!.Letter).ToList();
        }
    }
    public List<ReaderBlock>? Get(string url)
    {
        lock (gate) return ReadFile(PathFor(url)) is { } item && item.Letter.Url == url ? item.Blocks : null;
    }
    private CacheEntry? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16_000_000) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            try
            {
                var item = JsonSerializer.Deserialize<CacheEntry>(bytes, FilterRules.Json);
                return item?.Letter is not null && NavigationPolicy.IsMail(item.Letter.Url) && item.Blocks is { Count: > 0 }
                    && item.Blocks.All(b => b is not null && b.Text is not null && b.Image is not null) ? item : null;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public void Save(ReaderLetter letter, List<ReaderBlock> blocks)
    {
        if (!NavigationPolicy.IsMail(letter.Url) || blocks.Count == 0) return;
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheEntry(letter, blocks), FilterRules.Json);
            try
            {
                if (bytes.Length > 15_000_000) throw new IOException("cache_item_limit");
                var encrypted = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
                var path = PathFor(letter.Url);
                // Explicit, bounded storage. Never silently evict a mailbox being downloaded.
                if (Directory.EnumerateFiles(directory, "*.bin").Sum(p => new FileInfo(p).Length) + encrypted.Length > 512_000_000)
                    throw new IOException("cache_size_limit");
                File.WriteAllBytes(path + ".tmp", encrypted);
                File.Move(path + ".tmp", path, true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory).Where(p => p.EndsWith(".bin") || p.EndsWith(".tmp"))) File.Delete(path);
        }
    }
    private sealed record CacheEntry(ReaderLetter Letter, List<ReaderBlock> Blocks);
}
