using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using MailTrim.App;
using MailTrim.Core;

namespace MailTrim.Smoke;

internal static class GmailRegistrationChecks
{
    public static void Run(LocalStore store, Action<bool, string> check)
    {
        var root = Path.Combine(store.Root, "gmail-registration-checks");
        var bundled = GmailClientRegistration.Create("bundled-fixture.apps.googleusercontent.com", "GOCSPX-fixture-bundled");
        var local = GmailClientRegistration.Create("local-fixture.apps.googleusercontent.com", "GOCSPX-fixture-local");
        var registrations = new GmailRegistrationStore(root, () => bundled);
        check(registrations.Load() == bundled, "Gmail browser sign-in uses the bundled app registration without an account JSON file");
        var absent = new GmailRegistrationStore(Path.Combine(root, "no-registration"), () => null);
        check(absent.Load() is null, "Gmail missing app registration is explicit rather than a fabricated client");
        absent.Clear(); absent.Clear();
        check(absent.Load() is null && !Directory.Exists(Path.Combine(root, "no-registration")),
            "clearing an unconfigured Gmail app registration is repeatable and does not create its missing data directory");

        registrations.Save(local);
        check(registrations.Load() == local && new GmailRegistrationStore(root, () => bundled).Load() == local,
            "Gmail app registration override survives restart and takes precedence over the bundled client");
        var path = Path.Combine(root, "Gmail", Guid.Empty.ToString("N"), "registration.bin");
        var encoded = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        check(!encoded.Contains(local.ClientId, StringComparison.Ordinal) && !encoded.Contains(local.ClientSecret, StringComparison.Ordinal),
            "Gmail app registration override is encrypted with Windows rather than saved as JSON");

        var account = new GmailVault(root, Guid.NewGuid());
        var reserved = new GmailVault(root, Guid.Empty);
        account.Save("credentials", "account-token-fixture"); account.Save("cache", "account-mail-fixture");
        reserved.Save("credentials", "reserved-token-fixture"); reserved.Save("cache", "reserved-mail-fixture");
        registrations.Clear();
        check(registrations.Load() == bundled && !File.Exists(path), "clearing an app registration restores the bundled client");
        check(account.Load<string>("credentials") == "account-token-fixture" && account.Load<string>("cache") == "account-mail-fixture"
            && reserved.Load<string>("credentials") == "reserved-token-fixture" && reserved.Load<string>("cache") == "reserved-mail-fixture",
            "changing or clearing Gmail app registration never removes account grants or message caches");
        var separate = new GmailRegistrationStore(Path.Combine(root, "another-data-root"), () => bundled);
        registrations.Save(local);
        check(separate.Load() == bundled, "Gmail app registration overrides remain isolated between installation data roots");

        var invalid = new[]
        {
            new GmailClientRegistration("", ""), new GmailClientRegistration(local.ClientId, ""),
            new GmailClientRegistration("web.example.test", "secret"), new GmailClientRegistration(".apps.googleusercontent.com", "secret"),
            new GmailClientRegistration("a.apps.googleusercontent.com.evil.test", "secret"),
            new GmailClientRegistration("a.apps.googleusercontent.com\n", "secret"),
            new GmailClientRegistration(".a.apps.googleusercontent.com", "secret"),
            new GmailClientRegistration(local.ClientId, "line\nbreak"), new GmailClientRegistration(local.ClientId, "space value"),
            new GmailClientRegistration(local.ClientId, "quote\"value"), new GmailClientRegistration(new string('a', 501) + ".apps.googleusercontent.com", "secret"),
            new GmailClientRegistration(local.ClientId, new string('s', 501))
        };
        check(invalid.All(value => !value.IsValid), "Gmail app registration validates both bounded Google client fields");
        bool rejected = false;
        try { registrations.Save(invalid[1]); } catch (FormatException) { rejected = true; }
        check(rejected && registrations.Load() == local, "an invalid Gmail registration cannot replace a working saved registration");
        rejected = false;
        try { GmailClientRegistration.Create(local.ClientId, ""); } catch (FormatException) { rejected = true; }
        check(rejected, "Gmail validated registration factory rejects a partial configuration");
        reserved.Save("registration", invalid[1]);
        check(registrations.Load() == bundled, "invalid persisted Gmail registration falls back to a valid bundled registration");
        check(new GmailRegistrationStore(Path.Combine(root, "invalid-bundle"), () => invalid[1]).Load() is null,
            "invalid bundled Gmail configuration cannot reach browser sign-in");
        check(!local.ToString().Contains(local.ClientId, StringComparison.Ordinal) && !local.ToString().Contains(local.ClientSecret, StringComparison.Ordinal),
            "Gmail registration diagnostic text does not expose client fields");

        Assembly Metadata(params (string Key, string Value)[] values)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GmailRegistrationFixture" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
            foreach (var value in values) assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [value.Key, value.Value]));
            return assembly;
        }
        check(GmailRegistrationStore.FromAssembly(Metadata((GmailRegistrationStore.ClientIdMetadata, bundled.ClientId),
            (GmailRegistrationStore.ClientSecretMetadata, bundled.ClientSecret))) == bundled,
            "Gmail app registration is read from build-generated assembly metadata");
        check(GmailRegistrationStore.FromAssembly(Metadata()) is null
            && GmailRegistrationStore.FromAssembly(Metadata((GmailRegistrationStore.ClientIdMetadata, bundled.ClientId))) is null
            && GmailRegistrationStore.FromAssembly(Metadata((GmailRegistrationStore.ClientIdMetadata, bundled.ClientId),
                (GmailRegistrationStore.ClientSecretMetadata, ""))) is null
            && GmailRegistrationStore.FromAssembly(Metadata((GmailRegistrationStore.ClientIdMetadata, bundled.ClientId),
                (GmailRegistrationStore.ClientIdMetadata, local.ClientId), (GmailRegistrationStore.ClientSecretMetadata, bundled.ClientSecret))) is null,
            "absent, partial, invalid and ambiguous Gmail assembly registrations are rejected");
        var packaged = GmailRegistrationStore.FromAssembly(typeof(GmailRegistrationStore).Assembly);
        var fields = typeof(GmailRegistrationStore).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var expectedId = fields.Single(m => m.Key == GmailRegistrationStore.ClientIdMetadata).Value ?? "";
        var expectedSecret = fields.Single(m => m.Key == GmailRegistrationStore.ClientSecretMetadata).Value ?? "";
        check((expectedId.Length == 0 && expectedSecret.Length == 0 && packaged is null)
            || packaged == GmailClientRegistration.Create(expectedId, expectedSecret),
            "compiled MailTrim retains either a complete Gmail app registration or an explicitly unconfigured state");
    }
}
