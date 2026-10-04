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
            var letters = Directory.EnumerateFiles(directory, "*.bin").Select(ReadFile).Where(x => x is not null).Select(x => x!.Letter).ToDictionary(x => x.Url, StringComparer.Ordinal);
            foreach (var letter in ReadHeaders()) letters[letter.Url] = letters.TryGetValue(letter.Url, out var old) ? MergeMetadata(old, letter) : letter;
            return ReaderChronology.NewestFirst(letters.Values);
        }
    }
    public static ReaderLetter MergeMetadata(ReaderLetter old, ReaderLetter fresh) =>
        ReaderChronology.DateKey(fresh) is null
            ? fresh with { Date = old.Date, DateHint = old.DateHint, ReceivedAt = old.ReceivedAt, CapturedAt = old.CapturedAt }
            : old.ReceivedAt is not null && fresh.ReceivedAt is null ? fresh with { ReceivedAt = old.ReceivedAt } : fresh;
    private long StoredBytesExcept(string path) => Directory.EnumerateFiles(directory)
        .Where(p => p != path && (p.EndsWith(".bin") || Path.GetFileName(p) == "headers.index"))
        .Sum(p => new FileInfo(p).Length);
    private List<ReaderLetter> ReadHeaders()
    {
        try
        {
            var path = Path.Combine(directory, "headers.index");
            if (!File.Exists(path) || new FileInfo(path).Length > 16_000_000) return [];
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            try { return (JsonSerializer.Deserialize<List<ReaderLetter>>(bytes, FilterRules.Json) ?? []).Take(50_000).Select(ReaderData.Clean).OfType<ReaderLetter>().ToList(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException) { return []; }
    }
    public void SaveHeaders(IEnumerable<ReaderLetter> input, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var letters = ReadHeaders().GroupBy(x => x.Url).ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
            foreach (var raw in input)
                if (ReaderData.Clean(raw) is { } letter)
                    letters[letter.Url] = letters.TryGetValue(letter.Url, out var old) ? MergeMetadata(old, letter) : letter;
            if (letters.Count > 50_000) throw new IOException("cache_header_limit");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ReaderChronology.NewestFirst(letters.Values), FilterRules.Json);
            try
            {
                if (bytes.Length > 15_000_000) throw new IOException("cache_index_limit");
                var encrypted = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "headers.index");
                if (StoredBytesExcept(path) + encrypted.Length > 512_000_000) throw new IOException("cache_size_limit");
                token.ThrowIfCancellationRequested();
                File.WriteAllBytes(path + ".tmp", encrypted); File.Move(path + ".tmp", path, true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void RefreshMetadata(ReaderLetter letter)
    {
        lock (gate)
        {
            var old = ReadFile(PathFor(letter.Url));
            if (old is null || ReaderChronology.DateKey(letter) is null) return;
            if (ReaderChronology.DateKey(old.Letter) is not null && (old.Letter.ReceivedAt is not null || letter.ReceivedAt is null)) return;
            try { Save(letter, old.Blocks); }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { /* Keep the existing readable cache. */ }
        }
    }
    public List<ReaderBlock>? Get(string url)
    {
        lock (gate) return ReadFile(PathFor(url)) is { } item && item.Letter.Url == url ? item.Blocks : null;
    }
    public List<ReaderLetter> Search(string query, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        string[] paths;
        lock (gate) paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.bin") : [];
        var found = new List<ReaderLetter>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            CacheEntry? entry;
            lock (gate) entry = ReadFile(path);
            if (entry is null) continue;
            if (Matches(entry.Letter, query) || entry.Blocks.Any(b => b.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
                found.Add(entry.Letter);
        }
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var matches = found.ToDictionary(x => x.Url, StringComparer.Ordinal);
            foreach (var letter in ReadHeaders())
            {
                token.ThrowIfCancellationRequested();
                if (matches.ContainsKey(letter.Url) || Matches(letter, query)) matches[letter.Url] = matches.TryGetValue(letter.Url, out var old) ? MergeMetadata(old, letter) : letter;
            }
            return ReaderChronology.NewestFirst(matches.Values);
        }
    }
    public static bool Matches(ReaderLetter letter, string query) =>
        letter.Sender.Contains(query, StringComparison.OrdinalIgnoreCase)
        || letter.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
        || letter.Preview.Contains(query, StringComparison.OrdinalIgnoreCase);
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
                if (StoredBytesExcept(path) + encrypted.Length > 512_000_000)
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
            foreach (var path in Directory.EnumerateFiles(directory).Where(p => p.EndsWith(".bin") || p.EndsWith(".tmp") || Path.GetFileName(p) == "headers.index")) File.Delete(path);
        }
    }
    private sealed record CacheEntry(ReaderLetter Letter, List<ReaderBlock> Blocks);
}
