using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MailTrim.Core;

namespace MailTrim.App;

public sealed class LocalStore
{
    // Runtime status only: never serialized or written to the event log.
    public Dictionary<Guid, MailboxCacheProgress> CacheProgress { get; } = [];
    public event Action<Guid, MailboxCacheProgress>? CacheProgressChanged;
    public void ReportCache(Guid profile, MailboxCacheProgress progress)
    {
        CacheProgress[profile] = progress;
        CacheProgressChanged?.Invoke(profile, progress);
    }
    public string Root { get; }
    public string RulesPath => Path.Combine(Root, "rules.json");
    public AppSettings Settings { get; }
    public FilterRules Rules { get; private set; }
    public bool RulesRecovered { get; }
    public bool RulesUpgradeAvailable { get; }
    public string DefaultRules => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rules", "default.json"));

    public LocalStore(string? isolatedDataRoot = null)
    {
        Root = isolatedDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailTrim");
        Directory.CreateDirectory(Root);
        // Explicit ACL; descendants inherit. No portable session data next to the executable.
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        var settingsPath = Path.Combine(Root, "settings.json");
        Settings = File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), FilterRules.Json) ?? throw new FormatException("Settings")
            : new AppSettings();
        if (Settings.Profiles is null || Settings.Profiles.Count > 30 || Settings.Profiles.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60 || !Enum.IsDefined(p.Provider)) || Settings.Profiles.Select(p => p.Id).Distinct().Count() != Settings.Profiles.Count)
            throw new FormatException("Profiles");
        if (Settings.Theme is not ("Light" or "Dark" or "System")) Settings.Theme = "System";
        if (!File.Exists(RulesPath)) AtomicWrite(RulesPath, DefaultRules);
        try
        {
            Rules = FilterRules.Parse(File.ReadAllText(RulesPath));
            var legacyRules = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "rules"), "legacy-*.json")
                .Select(path => FilterRules.Parse(File.ReadAllText(path))).ToArray();
            if (legacyRules.Any(legacy => JsonSerializer.Serialize(Rules, FilterRules.Json) == JsonSerializer.Serialize(legacy, FilterRules.Json)))
            {
                AtomicWrite(RulesPath + ".backup", File.ReadAllText(RulesPath));
                Rules = FilterRules.Parse(DefaultRules);
                AtomicWrite(RulesPath, JsonSerializer.Serialize(Rules, FilterRules.Json));
                Log("bundled_rules_upgraded");
            }
            else if (legacyRules.Any(legacy => Rules.Revision == legacy.Revision)) RulesUpgradeAvailable = true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            Rules = FilterRules.Parse(DefaultRules);
            RulesRecovered = true;
            Log("rules_fallback"); // Preserve the invalid file for repair.
        }
    }

    public string ProfilePath(Guid id) => Path.Combine(Root, "Profiles", id.ToString("N"));
    public void Save() => AtomicWrite(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(Settings, FilterRules.Json));
    public void SaveRules(string json)
    {
        var candidate = FilterRules.Parse(json);
        AtomicWrite(RulesPath, JsonSerializer.Serialize(candidate, FilterRules.Json));
        Rules = candidate;
        Log("rules_saved");
    }
    public void ReloadRules() => Rules = FilterRules.Parse(File.ReadAllText(RulesPath));
    private static void AtomicWrite(string path, string data)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(data); writer.Flush(); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    public void Log(string code)
    {
        // Event codes only; never URLs, labels, addresses, titles, exception messages or stack traces.
        if (code.Any(c => !(char.IsAsciiLetterLower(c) || c == '_'))) return;
        try
        {
            var path = Path.Combine(Root, "events.log");
            if (File.Exists(path) && new FileInfo(path).Length > 128_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {code}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
