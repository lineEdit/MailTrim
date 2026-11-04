using System.IO;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using MailTrim.Core.Gmail;

namespace MailTrim.Smoke;

internal static class GmailProfileChecks
{
    public static async Task Run(LocalStore store, Grid host, Action<bool, string> check)
    {
        var root = Path.Combine(store.Root, "gmail-cold-profile");
        var profile = new AccountProfile { Name = "Unconfigured Gmail", Provider = MailProvider.Gmail };
        var vault = new GmailVault(root, profile.Id);
        vault.ClearCache(); vault.Clear(); vault.Clear();
        check(!Directory.Exists(root), "clearing an unconfigured Gmail profile is idempotent and does not create a directory");

        var credentials = new GmailCredentials("synthetic.apps.googleusercontent.com", "secret", "refresh", "access", DateTimeOffset.UtcNow.AddHours(1));
        vault.Save("credentials", credentials);
        vault.Save("cache", new GmailCache([], [], "synthetic@example.test"));
        var directory = Path.Combine(root, "Gmail", profile.Id.ToString("N"));
        File.WriteAllText(Path.Combine(directory, "cache.bin.tmp"), "synthetic unfinished cache");
        vault.ClearCache(); vault.ClearCache();
        check(vault.Load<GmailCredentials>("credentials") == credentials
            && !File.Exists(Path.Combine(directory, "cache.bin")) && !File.Exists(Path.Combine(directory, "cache.bin.tmp")),
            "clearing Gmail cache removes its temporary file and retains account credentials");

        var other = new GmailVault(root, Guid.NewGuid()); other.Save("credentials", credentials);
        var registration = new GmailVault(root, Guid.Empty); registration.Save("registration", "synthetic shared registration");
        File.WriteAllText(Path.Combine(directory, "credentials.bin.tmp"), "synthetic unfinished credentials");
        vault.Clear(); vault.Clear();
        check(!Directory.EnumerateFileSystemEntries(directory).Any()
            && other.Load<GmailCredentials>("credentials") == credentials
            && registration.Load<string>("registration") == "synthetic shared registration",
            "clearing Gmail account data removes only that profile's data and preserves other accounts and app registration");

        // A directory occupying a file path is an actual I/O problem, not absent data.
        Directory.CreateDirectory(Path.Combine(directory, "credentials.bin"));
        bool denied = false;
        try { vault.Clear(); } catch (UnauthorizedAccessException) { denied = true; }
        check(denied, "Gmail cleanup does not hide access failures or an invalid credentials file path");

        var unconfigured = new AccountProfile { Name = "Fresh Gmail", Provider = MailProvider.Gmail };
        MailboxStatus? last = null;
        using var pane = new GmailPane(store, unconfigured, (_, value) => last = value, _ => { });
        host.Children.Add(pane);
        try
        {
            await pane.Ready;
            check(!pane.Connected && !pane.HasDraft
                && !Directory.Exists(Path.Combine(store.Root, "Gmail", unconfigured.Id.ToString("N"))),
                "a fresh Gmail profile opens without requiring or creating local account data files");
            await pane.ClearData(); await pane.ClearData();
            check(!pane.Connected && last?.Unread is null
                && !Directory.Exists(Path.Combine(store.Root, "Gmail", unconfigured.Id.ToString("N"))),
                "clearing a Gmail pane before its first sign-in succeeds repeatedly without DirectoryNotFoundException");
        }
        finally { host.Children.Remove(pane); }
    }
}
