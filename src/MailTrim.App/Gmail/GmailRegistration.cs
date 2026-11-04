using System.IO;
using System.Reflection;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>The application's Google Desktop registration, not a Google account password or token.</summary>
public sealed record GmailClientRegistration(string ClientId, string ClientSecret)
{
    public bool IsValid => ValidClientId(ClientId) && ValidSecret(ClientSecret);

    public static GmailClientRegistration Create(string id, string secret)
    {
        var registration = new GmailClientRegistration(id, secret);
        if (!registration.IsValid) throw new FormatException("gmail_registration_invalid");
        return registration;
    }

    public override string ToString() => "Google Desktop app registration";

    private static bool ValidClientId(string? id)
    {
        const string suffix = ".apps.googleusercontent.com";
        return id is { Length: >= 27 and <= 500 } && id.Length > suffix.Length
            && char.IsAsciiLetterOrDigit(id[0])
            && id.EndsWith(suffix, StringComparison.Ordinal)
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private static bool ValidSecret(string? secret) => secret is { Length: >= 1 and <= 500 }
        && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>One registration per installation data root; account grants remain in their own vaults.</summary>
public sealed class GmailRegistrationStore
{
    public const string ClientIdMetadata = "MailTrimGmailClientId";
    public const string ClientSecretMetadata = "MailTrimGmailClientSecret";
    private readonly GmailVault vault;
    private readonly string directory;
    private readonly Func<GmailClientRegistration?> bundled;

    public GmailRegistrationStore(LocalStore store) : this(store.Root) { }
    public GmailRegistrationStore(string root) : this(root, () => FromAssembly(typeof(GmailRegistrationStore).Assembly)) { }
    public GmailRegistrationStore(string root, Func<GmailClientRegistration?> bundled)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        vault = new GmailVault(root, Guid.Empty);
        directory = Path.Combine(root, "Gmail", Guid.Empty.ToString("N"));
        this.bundled = bundled;
    }

    public GmailClientRegistration? Load()
    {
        var local = vault.Load<GmailClientRegistration>("registration");
        if (local is { IsValid: true }) return local;
        var builtIn = bundled();
        return builtIn is { IsValid: true } ? builtIn : null;
    }

    public void Save(GmailClientRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration.IsValid) throw new FormatException("gmail_registration_invalid");
        vault.Save("registration", registration);
    }

    public void Clear()
    {
        foreach (var name in new[] { "registration.bin", "registration.bin.tmp" })
        {
            try { File.Delete(Path.Combine(directory, name)); }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static GmailClientRegistration? FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var ids = metadata.Where(m => m.Key == ClientIdMetadata).ToArray();
        var secrets = metadata.Where(m => m.Key == ClientSecretMetadata).ToArray();
        if (ids.Length != 1 || secrets.Length != 1) return null;
        var registration = new GmailClientRegistration(ids[0].Value ?? "", secrets[0].Value ?? "");
        return registration.IsValid ? registration : null;
    }
}
