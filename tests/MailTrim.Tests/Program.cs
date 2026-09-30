using MailTrim.Core;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
foreach (var url in new[] { "https://e.mail.ru/inbox/", "https://account.mail.ru/login", "https://id.vk.com/auth" })
    Check(NavigationPolicy.IsInternal(url), "official origin " + url);
foreach (var url in new[] { "https://e.mail.ru.evil.test/", "https://evilmail.ru/", "http://e.mail.ru/", "https://e.mail.ru:444/", "https://evil@e.mail.ru/", "file:///C:/Windows/", "javascript:alert(1)", "https://news.mail.ru/" })
    Check(!NavigationPolicy.IsInternal(url), "reject internal " + url);
Check(!NavigationPolicy.IsExternal("file:///C:/Windows"), "no shell file launch");
Check(!NavigationPolicy.IsExternal("ms-settings:privacy"), "no arbitrary protocols");
Check(!NavigationPolicy.IsExternal("https://user:pass@example.org"), "no URL credentials");
var rules = FilterRules.Parse("""{"blockedDomains":["ads.example.com","mail.ru"],"allowedDomains":["safe.ads.example.com"]} """);
Check(rules.ShouldBlock("https://a.ads.example.com/x", true), "subdomain blocked");
Check(!rules.ShouldBlock("https://notads.example.com/x", true), "domain boundary");
Check(!rules.ShouldBlock("https://ads.example.com.evil.test/x", true), "suffix boundary");
Check(!rules.ShouldBlock("https://safe.ads.example.com/x", true), "allow wins");
Check(!rules.ShouldBlock("https://e.mail.ru/x", true), "mail protected");
Check(!rules.ShouldBlock("https://account.mail.ru/x", true), "auth protected");
Check(!rules.ShouldBlock("https://ads.example.com/x", false), "pause bypass");
rules.BlockedMailPathPrefixes = ["/api-proxy/rb-mimic/"];
Check(rules.ShouldBlock("https://e.mail.ru/api-proxy/rb-mimic/banner", true), "first-party ad proxy blocked");
Check(!rules.ShouldBlock("https://e.mail.ru/api/v1/messages", true), "message API protected");
Check(!rules.ShouldBlock("https://account.mail.ru/api-proxy/rb-mimic/banner", true), "auth remains protected from proxy rules");
Check(!rules.ShouldBlock("https://e.mail.ru/api-proxy/rb-mimic-safe/banner", true), "ad proxy path boundary");
Check(!rules.ShouldBlock("https://e.mail.ru/api-proxy/rb-mimic/banner", false), "pause bypasses ad proxy");
rules.AllowedDomains.Add("e.mail.ru");
Check(!rules.ShouldBlock("https://e.mail.ru/api-proxy/rb-mimic/banner", true), "explicit allow wins over ad proxy");
foreach (var json in new[] { "null", "{}", "{\"schemaVersion\":2}", "{\"blockedDomains\":[\"*.example.com\"]}", "{\"hideSelectors\":[\"body { color:red }\"]}", "{\"allowedDomains\":null}" })
{
    if (json == "{}") { Check(FilterRules.Parse(json).SchemaVersion == 1, "empty lists default"); continue; }
    var rejected = false;
    try { FilterRules.Parse(json); } catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { rejected = true; }
    Check(rejected, "invalid rules rejected " + json);
}
var script = CosmeticScript.Create(new FilterRules { HideSelectors = ["[data-testid=\"ad\"]"] }, true, false);
Check(script.Contains("window.top !== window") && script.Contains("location.origin !== 'https://e.mail.ru'"), "cosmetic origin and frame guard");
Check(!script.Contains("fetch(") && !script.Contains("chrome.webview"), "no script network/native bridge");
Check(CosmeticScript.Create(rules, false, true).Contains("const selectors = [];"), "paused cosmetics empty");
var notificationSettings = new AppSettings { UseSiteNotifications = true };
Check(SiteNotificationPolicy.CanDeliver(notificationSettings, "https://e.mail.ru/"), "opted-in official mail can notify");
foreach (var origin in new[] { "https://e.mail.ru.evil.test", "https://account.mail.ru", "http://e.mail.ru", "https://e.mail.ru:444", "https://user@e.mail.ru" })
    Check(!SiteNotificationPolicy.CanDeliver(notificationSettings, origin), "reject notification origin " + origin);
notificationSettings.NotifyNewMail = false;
Check(!SiteNotificationPolicy.CanDeliver(notificationSettings, "https://e.mail.ru"), "mute rejects site notifications");
notificationSettings.NotifyNewMail = true; notificationSettings.UseSiteNotifications = false;
Check(!SiteNotificationPolicy.CanDeliver(notificationSettings, "https://e.mail.ru"), "polling and site notifications are mutually exclusive");
Check(new SavedWindowPosition(-1500, 10, -200, 800, false).IsValid, "window position supports left-hand monitor");
Check(!new SavedWindowPosition(0, 0, 0, 0, false).IsValid, "reject empty window bounds");
Check(!new SavedWindowPosition(int.MinValue, 0, int.MaxValue, 700, false).IsValid, "reject overflowing window coordinates");
var releases = """
[{"tag_name":"v0.8.0","draft":false,"prerelease":true},
 {"tag_name":"v99.0.0","draft":true,"prerelease":false},
 {"tag_name":"v0.10.0","draft":false,"prerelease":true},
 {"tag_name":"v0.7.0","draft":false,"prerelease":false},
 {"tag_name":"invalid","draft":false,"prerelease":false}]
""";
Check(ReleaseCatalog.Select(releases, true)?.Tag == "v0.10.0", "updates compare numeric versions and skip drafts");
Check(ReleaseCatalog.Select(releases, false)?.Tag == "v0.7.0", "stable channel excludes prereleases");
Check(ReleaseCatalog.Select("[]", true) is null, "empty release channel is not a network error");
Check(ReleaseCatalog.Select("""{"tag_name":"v0.8.0","draft":false,"prerelease":false}""", false)?.Version == new Version(0,8,0,0), "single stable release normalized to assembly version");
Check(ReleaseCatalog.Select("""[{"tag_name":"v0.8.0","draft":false,"prerelease":true}]""", false) is null, "no stable releases is an empty channel");
Check(ReleaseCatalog.Select("""[{"tag_name":"v0.8.0","draft":false,"prerelease":true},{"tag_name":"v0.8.0","draft":false,"prerelease":false}]""", true)?.Prerelease == false, "stable release wins same-version tie");
Check(ReleaseCatalog.Select("""[{"tag_name":"v3.0.0"},{"tag_name":"v5.0.0","draft":null,"prerelease":false}]""", true) is null, "malformed release records cannot become updates");
var tracker = new NewMailTracker(); var profile = Guid.NewGuid();
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/old"]) == 0, "mail notification first snapshot is quiet");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new", "https://e.mail.ru/inbox/old"]) == 1, "new inbox head notifies once");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new", "https://e.mail.ru/inbox/old"]) == 0, "unchanged inbox does not notify again");
Check(tracker.Observe(Guid.NewGuid(), ["https://e.mail.ru/inbox/new"]) == 0, "notification baseline is isolated per account");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/old"]) == 0, "removed head does not announce old mail");
tracker.Clear(); Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new"]) == 0, "reenabling notifications resets baseline");Console.WriteLine($"{passed} checks passed.");


var updateScratch = Path.Combine(Path.GetTempPath(), "MailTrim-package-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(updateScratch);
try
{
    string MakePackage(string name, string? extra = null, bool complete = true)
    {
        var path = Path.Combine(updateScratch, name + ".zip");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in complete ? new[] { "MailTrim.exe", "MailTrim.dll", "MailTrim.runtimeconfig.json", "rules/default.json" } : new[] { "MailTrim.exe" })
        { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("fixture"); }
        if (extra is not null) { using var writer = new StreamWriter(zip.CreateEntry(extra).Open()); writer.Write("fixture"); }
        return path;
    }
    string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    var valid = MakePackage("valid");
    UpdatePackage.Extract(valid, Hash(valid), Path.Combine(updateScratch, "valid-out"), default);
    Check(File.Exists(Path.Combine(updateScratch, "valid-out", "rules", "default.json")), "valid update extracted");
    foreach (var unsafeName in new[] { "../escape", "/absolute", "a/../../escape", "C:/escape", "file:stream", "CON.txt", "folder./file", "MAILTRIM.EXE" })
    {
        var path = MakePackage(Guid.NewGuid().ToString("N"), unsafeName);
        var rejected = false;
        try { UpdatePackage.Extract(path, Hash(path), Path.Combine(updateScratch, Guid.NewGuid().ToString("N")), default); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject unsafe update entry " + unsafeName);
    }
    var badHash = false;
    try { UpdatePackage.Extract(valid, new string('0', 64), Path.Combine(updateScratch, "bad-hash"), default); }
    catch (InvalidDataException) { badHash = true; }
    Check(badHash && !Directory.Exists(Path.Combine(updateScratch, "bad-hash")), "checksum failure leaves installation untouched");
    var incomplete = MakePackage("incomplete", complete: false);
    var missing = false;
    try { UpdatePackage.Extract(incomplete, Hash(incomplete), Path.Combine(updateScratch, "missing"), default); }
    catch (InvalidDataException) { missing = true; }
    Check(missing, "reject incomplete update");
    var cancelled = false;
    try { UpdatePackage.Extract(valid, Hash(valid), Path.Combine(updateScratch, "cancelled"), new CancellationToken(true)); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && !Directory.Exists(Path.Combine(updateScratch, "cancelled")), "cancel update before extraction");
}
finally { Directory.Delete(updateScratch, true); }
Console.WriteLine($"{passed} total checks passed.");
