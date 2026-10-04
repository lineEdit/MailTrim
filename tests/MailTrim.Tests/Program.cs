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
var observed = new DateTimeOffset(2026,10,4,10,0,0,TimeSpan.FromHours(3));
ReaderLetter Dated(string url, string date, DateTimeOffset? captured = null) => new("https://e.mail.ru/inbox/" + url, "s", "t", "p", date, CapturedAt: captured);
var ordered = ReaderChronology.NewestFirst(new[] { Dated("old", "17.12.25"), Dated("unknown", "19:59"), Dated("recent", "3 окт", observed), Dated("today", "0:20", observed) });
Check(ordered.Select(l => l.Url.Split('/').Last()).SequenceEqual(new[] { "today", "recent", "old", "unknown" }), "reader sorts captured and explicit dates newest first with unknown times last");
Check(ReaderChronology.DateKey(Dated("relative", "вчера 23:40", observed)) == new DateTimeOffset(2026,10,3,23,40,0,TimeSpan.FromHours(3)), "relative time uses capture day");
Check(ReaderChronology.DateKey(Dated("year", "31 дек", observed))?.Year == 2025, "short dates handle previous year");
Check(ReaderChronology.DateKey(Dated("bad", "99:99", observed)) is null, "invalid date is unknown rather than invented");
Check(ReaderChronology.DateKey(Dated("hint", "0:20", observed) with { DateHint = "30.09.2026 19:59" })?.Day == 30, "full official tooltip date wins over abbreviated display");
Check(ReaderChronology.DateKey(Dated("other-zone", "0:10", new DateTimeOffset(2026,1,1,0,20,0,TimeSpan.FromHours(14)))) == new DateTimeOffset(2026,1,1,0,10,0,TimeSpan.FromHours(14)), "capture time zone is preserved across a different local day");
var clean = ReaderData.Clean(new[] { new ReaderBlock("hello", "javascript:alert(1)"), new ReaderBlock("data", "data:image/png;base64,a"), new ReaderBlock("credentials", "https://user:pass@example.org/a"), new ReaderBlock("image", "https://example.org/a.png") });
Check(clean.Take(3).All(b => b.Image == "") && clean[3].Image.EndsWith("a.png"), "clean data rejects executable and credentialed image references");
Check(ReaderData.Clean(Enumerable.Repeat(new ReaderBlock(new string('a', 30_000), ""), 1000)).Sum(b => b.Text.Length + b.Image.Length) <= 500_000, "clean message data is bounded");
Check(ReaderData.Clean(new ReaderLetter("https://evil.test/mail", "s", "t", "p", "d")) is null, "clean data rejects external message URLs");
Check(ReaderData.Clean(new ReaderLetter("https://e.mail.ru/inbox/1", new string('s', 1000), "subject", "preview", "today"))?.Sender.Length == 500, "clean metadata is bounded");
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
Check(ReleasePackage.Name(new Version(0,12,9,0), DistributionKind.Standalone) == "MailTrim-0.12.9-win-x64.zip", "standalone update name stays compatible with old clients");
Check(ReleasePackage.Name(new Version(0,12,10,0), DistributionKind.Lite) == "MailTrim-0.12.10-win-x64-lite.zip", "lite updates preserve edition and use release version");
Check(ReleaseCatalog.Select("""{"tag_name":"v0.8.0","draft":false,"prerelease":false}""", false)?.Version == new Version(0,8,0,0), "single stable release normalized to assembly version");
Check(ReleaseCatalog.Select("""[{"tag_name":"v0.8.0","draft":false,"prerelease":true}]""", false) is null, "no stable releases is an empty channel");
Check(ReleaseCatalog.Select("""[{"tag_name":"v0.8.0","draft":false,"prerelease":true},{"tag_name":"v0.8.0","draft":false,"prerelease":false}]""", true)?.Prerelease == false, "stable release wins same-version tie");
Check(ReleaseCatalog.Select("""[{"tag_name":"v3.0.0"},{"tag_name":"v5.0.0","draft":null,"prerelease":false}]""", true) is null, "malformed release records cannot become updates");
var feedUri = new Uri("https://api.github.com/repos/example/test/releases");
var retryHandler = new FeedHandler(call => call == 1 ? throw new HttpRequestException("connection interrupted") : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(releases) });
using (var client = new HttpClient(retryHandler)) Check((await UpdateCatalogClient.Fetch(client, feedUri, true))?.Tag == "v0.10.0" && retryHandler.Calls == 2 && retryHandler.Version == new Version(2,0), "feed retries connection interruption with protocol negotiation");
var serverHandler = new FeedHandler(call => new(call == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = new StringContent(releases) });
using (var client = new HttpClient(serverHandler)) Check((await UpdateCatalogClient.Fetch(client, feedUri, true))?.Tag == "v0.10.0" && serverHandler.Calls == 2, "feed retries transient server error");
foreach (var (status, json, expected) in new[] {
    (429, "{}", UpdateCheckError.RateLimit), (404, "{}", UpdateCheckError.NotFound), (403, "{}", UpdateCheckError.AccessDenied), (200, "not json", UpdateCheckError.InvalidResponse) })
{
    var handler = new FeedHandler(_ => new((System.Net.HttpStatusCode)status) { Content = new StringContent(json) });
    using var client = new HttpClient(handler); bool correct = false;
    try { await UpdateCatalogClient.Fetch(client, feedUri, true); } catch (UpdateCheckException ex) { correct = ex.Error == expected; }
    Check(correct && handler.Calls == 1, "feed classifies error without retry storm: " + expected);
}
using (var client = new HttpClient(new FeedHandler(_ => throw new HttpRequestException("offline"))))
{
    bool correct = false; try { await UpdateCatalogClient.Fetch(client, feedUri, true); } catch (UpdateCheckException ex) { correct = ex.Error == UpdateCheckError.Network; }
    Check(correct, "feed reports exhausted network retry");
}
using (var cancelledFeed = new CancellationTokenSource())
using (var client = new HttpClient(new FeedHandler(_ => new(System.Net.HttpStatusCode.OK))))
{
    cancelledFeed.Cancel(); bool cancelledRequest = false;
    try { await UpdateCatalogClient.Fetch(client, feedUri, true, cancelledFeed.Token); } catch (OperationCanceledException) { cancelledRequest = true; }
    Check(cancelledRequest, "feed respects caller cancellation");
}
using (var client = new HttpClient(new FeedHandler(_ => throw new TaskCanceledException("timeout"))))
{
    bool correct = false; try { await UpdateCatalogClient.Fetch(client, feedUri, true); } catch (UpdateCheckException ex) { correct = ex.Error == UpdateCheckError.Timeout; }
    Check(correct, "feed classifies exhausted timeout retry");
}
using (var client = new HttpClient(new FeedHandler(_ => { var response = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden); response.Headers.Add("X-RateLimit-Remaining", "0"); return response; })))
{
    bool correct = false; try { await UpdateCatalogClient.Fetch(client, feedUri, true); } catch (UpdateCheckException ex) { correct = ex.Error == UpdateCheckError.RateLimit; }
    Check(correct, "feed recognizes GitHub exhausted quota");
}
using (var client = new HttpClient(new FeedHandler(_ => { var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }; response.Content.Headers.ContentLength = 4_000_001; return response; })))
{
    bool correct = false; try { await UpdateCatalogClient.Fetch(client, feedUri, true); } catch (UpdateCheckException ex) { correct = ex.Error == UpdateCheckError.ResponseTooLarge; }
    Check(correct, "feed rejects oversized response before reading");
}
async Task<bool> CompressedFeed(string json, string encoding, UpdateCheckError? expected = null)
{
    using var compressed = new MemoryStream();
    using (Stream encoder = encoding == "gzip"
        ? new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Compress, true)
        : new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionMode.Compress, true))
        encoder.Write(System.Text.Encoding.UTF8.GetBytes(json));
    var body = compressed.ToArray();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var address = new Uri($"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/releases");
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, true);
        bool negotiated = false; string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token)))
            if (line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase) && line.Contains(encoding, StringComparison.OrdinalIgnoreCase)) negotiated = true;
        var headers = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: {encoding}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, deadline.Token);
        await stream.WriteAsync(body, deadline.Token);
        return negotiated;
    });
    try
    {
        using var client = UpdateCatalogClient.CreateClient();
        bool correct;
        try { correct = (await UpdateCatalogClient.Fetch(client, address, true, deadline.Token))?.Tag == "v0.10.0" && expected is null; }
        catch (UpdateCheckException ex) { correct = ex.Error == expected; }
        return await server && correct;
    }
    finally { listener.Stop(); }
}
Check(await CompressedFeed(releases, "gzip"), "production update client negotiates and decodes gzip release feed");
Check(await CompressedFeed(releases, "br"), "production update client negotiates and decodes Brotli release feed");
Check(await CompressedFeed(releases + new string(' ', 4_000_001), "gzip", UpdateCheckError.ResponseTooLarge), "compressed release feed cannot bypass decompressed size limit");
if (args.Contains("--verify-update-feed"))
{
    using var client = UpdateCatalogClient.CreateClient(); client.DefaultRequestHeaders.UserAgent.ParseAdd("MailTrim-test/" + typeof(UpdateCatalogClient).Assembly.GetName().Version!.ToString(3));
    var latest = await UpdateCatalogClient.Fetch(client, new Uri("https://api.github.com/repos/lineEdit/MailTrim/releases?per_page=100"), true);
    Check(latest is not null, "live GitHub feed parsed by application HTTP client");
    Console.WriteLine("Latest published release: " + latest?.Tag);
}
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
    var windowsDirectory = MakePackage("windows-directory", "runtimes\\win-x64\\");
    UpdatePackage.Extract(windowsDirectory, Hash(windowsDirectory), Path.Combine(updateScratch, "windows-dir"), default);
    Check(Directory.Exists(Path.Combine(updateScratch, "windows-dir", "runtimes", "win-x64")), "Windows ZIP directory separators supported");
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

sealed class FeedHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Version? Version { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Version = request.Version; return Task.FromResult(response(++Calls));
    }
}
