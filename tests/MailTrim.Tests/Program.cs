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
var tracker = new NewMailTracker(); var profile = Guid.NewGuid();
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/old"]) == 0, "mail notification first snapshot is quiet");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new", "https://e.mail.ru/inbox/old"]) == 1, "new inbox head notifies once");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new", "https://e.mail.ru/inbox/old"]) == 0, "unchanged inbox does not notify again");
Check(tracker.Observe(Guid.NewGuid(), ["https://e.mail.ru/inbox/new"]) == 0, "notification baseline is isolated per account");
Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/old"]) == 0, "removed head does not announce old mail");
tracker.Clear(); Check(tracker.Observe(profile, ["https://e.mail.ru/inbox/new"]) == 0, "reenabling notifications resets baseline");Console.WriteLine($"{passed} checks passed.");

