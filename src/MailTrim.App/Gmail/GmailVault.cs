using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailTrim.Core.Gmail;

namespace MailTrim.App;

public sealed record GmailCredentials(string ClientId, string ClientSecret, string RefreshToken, string AccessToken, DateTimeOffset ExpiresAt);
public sealed record GmailCachedPage(string Label, GmailPage Page, DateTimeOffset CheckedAt);
public sealed record GmailCache(GmailCachedPage[] Pages, GmailMessage[] Bodies, string Address = "");

/// <summary>Windows current-user encryption with purpose and account-specific entropy.</summary>
public sealed class GmailVault(string root, Guid profile)
{
    private string PathFor(string purpose) => Path.Combine(root, "Gmail", profile.ToString("N"), purpose + ".bin");
    private byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("MailTrim.Gmail.v1." + profile.ToString("N") + "." + purpose);
    public T? Load<T>(string purpose)
    {
        try
        {
            var path = PathFor(purpose);
            if (!File.Exists(path) || new FileInfo(path).Length > 24_000_000) return default;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(purpose), DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<T>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return default; }
    }
    public void Save<T>(string purpose, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (plain.Length > 20_000_000) throw new IOException("gmail_cache_size");
            var bytes = ProtectedData.Protect(plain, Entropy(purpose), DataProtectionScope.CurrentUser);
            var path = PathFor(purpose); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void ClearCache() { File.Delete(PathFor("cache")); File.Delete(PathFor("cache") + ".tmp"); }
    public void Clear() { foreach (var purpose in new[] { "credentials", "cache" }) { File.Delete(PathFor(purpose)); File.Delete(PathFor(purpose) + ".tmp"); } }
}
