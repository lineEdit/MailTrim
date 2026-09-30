using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MailTrim.Core;

public static class UpdatePackage
{
    public static void Extract(string archive, string checksum, string destination, CancellationToken token)
    {
        if (!Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid checksum");
        using var input = File.OpenRead(archive);
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checksum mismatch");
        input.Position = 0;
        using var zip = new ZipArchive(input);
        if (zip.Entries.Count > 4000) throw new InvalidDataException("Too many entries");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny([':', '<', '>', '"', '|', '?', '*', '\0']) >= 0 ||
                Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)) ||
                !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Unsafe archive path");
            size = checked(size + entry.Length);
            if (size > 1_500_000_000) throw new InvalidDataException("Archive too large");
        }
        foreach (var required in new[] { "MailTrim.exe", "MailTrim.dll", "MailTrim.runtimeconfig.json", "rules/default.json" })
            if (!names.Contains(required)) throw new InvalidDataException("Incomplete package");
        if (Directory.Exists(destination)) throw new IOException("Destination already exists");
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }
}
