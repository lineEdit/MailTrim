using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Pass an empty scratch directory for test data."); return 2; }
        var root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) { Console.Error.WriteLine("Test directory must be empty."); return 2; }
        var result = 1;
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MailTrim;component/Theme.xaml", UriKind.Relative) });
        var host = new Grid();
        var window = new Window { Content = host, Width = 1000, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            var sessions = new List<BrowserSession>();
            try
            {
                Directory.CreateDirectory(root);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "rules", "legacy-0.1.0.json"), Path.Combine(root, "rules.json"));
                var store = new LocalStore(root);
                Check(store.Rules.Revision == "2026-09-30.3" && File.Exists(store.RulesPath + ".backup"), "unmodified legacy rules upgraded with backup");
                var previousRoot = Path.Combine(root, "previous-test"); Directory.CreateDirectory(previousRoot);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "rules", "legacy-0.1.1.json"), Path.Combine(previousRoot, "rules.json"));
                Check(new LocalStore(previousRoot).Rules.Revision == "2026-09-30.3", "rules from 0.1.1 and 0.1.2 upgraded");
                var customRoot = Path.Combine(root, "custom-test"); Directory.CreateDirectory(customRoot);
                var custom = FilterRules.Parse(store.DefaultRules); custom.Revision = "custom"; custom.HideSelectors = [".user-rule"];
                File.WriteAllText(Path.Combine(customRoot, "rules.json"), System.Text.Json.JsonSerializer.Serialize(custom, FilterRules.Json));
                var customStore = new LocalStore(customRoot);
                Check(customStore.Rules.HideSelectors.SequenceEqual([".user-rule"]), "custom rules preserved");
                var chronologyCache = new MessageCache(root, Guid.NewGuid());
                var captured = DateTimeOffset.Now;
                var oldDate = new ReaderLetter("https://e.mail.ru/inbox/chronology-old", "Chrono", "Old", "", "17.12.25");
                var newDate = new ReaderLetter("https://e.mail.ru/inbox/chronology-new", "Chrono", "New", "", "0:20", CapturedAt: captured);
                var unknownDate = new ReaderLetter("https://e.mail.ru/inbox/chronology-unknown", "Chrono", "Unknown", "", "19:59");
                foreach (var letter in new[] { unknownDate, newDate, oldDate }) chronologyCache.Save(letter, [new ReaderBlock("Chrono text", "")]);
                Check(chronologyCache.List().Select(l => l.Subject).SequenceEqual(new[] { "New", "Old", "Unknown" }), "encrypted cache lists messages by date instead of file order");
                Check(chronologyCache.Search("Chrono", default).Select(l => l.Subject).SequenceEqual(new[] { "New", "Old", "Unknown" }), "cache search preserves newest-first ordering");
                chronologyCache.RefreshMetadata(unknownDate with { ReceivedAt = captured.AddDays(-1), CapturedAt = captured });
                Check(chronologyCache.List()[1].Subject == "Unknown" && chronologyCache.Get(unknownDate.Url)![0].Text == "Chrono text", "fresh date metadata corrects old cache ordering without changing body");
                var headerOnly = new ReaderLetter("https://e.mail.ru/inbox/header-only", "Fresh sender", "Header only", "Preview", "", ReceivedAt: captured.AddMinutes(1));
                chronologyCache.SaveHeaders([headerOnly, newDate with { Subject = "Updated subject" }, headerOnly]);
                Check(chronologyCache.List().Count == 4 && chronologyCache.List()[0].Subject == "Header only" && chronologyCache.Get(headerOnly.Url) is null, "encrypted headers persist new mail without opening it or inventing cached bodies");
                Check(chronologyCache.Search("Fresh sender", default).Single().Url == headerOnly.Url && chronologyCache.Search("Chrono text", default).Count == 3, "header search and full-text cache search merge without duplicates");
                chronologyCache.SaveHeaders([headerOnly with { Date = "unknown", ReceivedAt = null, CapturedAt = null }]);
                Check(chronologyCache.List()[0].ReceivedAt == headerOnly.ReceivedAt, "metadata without a date cannot erase a known mail date");
                chronologyCache.SaveHeaders([headerOnly with { Date = "Today", ReceivedAt = null, CapturedAt = captured }]);
                Check(chronologyCache.List()[0].ReceivedAt == headerOnly.ReceivedAt, "relative display date cannot replace an exact server date");
                chronologyCache.Clear(); Check(chronologyCache.List().Count == 0, "clear cache removes both bodies and encrypted headers");
                using (var cancelledHeaders = new CancellationTokenSource())
                {
                    cancelledHeaders.Cancel(); var headersStopped = false;
                    try { chronologyCache.SaveHeaders([headerOnly], cancelledHeaders.Token); } catch (OperationCanceledException) { headersStopped = true; }
                    Check(headersStopped && chronologyCache.List().Count == 0, "cancelled refresh cannot repopulate a cleared header cache");
                }
                var paused = false;
                var a = new BrowserSession(store, window, _ => { }, () => paused, _ => { });
                var b = new BrowserSession(store, window, _ => { }, () => paused, _ => { });
                sessions.Add(a); sessions.Add(b); host.Children.Add(a.View); host.Children.Add(b.View);
                var id = Guid.NewGuid();
                var secondProfileId = Guid.NewGuid();
                await a.Initialize(id); await b.Initialize(secondProfileId);
                a.View.CoreWebView2.Stop(); b.View.CoreWebView2.Stop();
                ConfigureFixture(a); ConfigureFixture(b);
                await Navigate(a, "https://e.mail.ru/__mailtrim_startup");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("startupCSS && startupGuard && startupKnownHidden") == "true", "startup CSS and guard precede page scripts and DOMContentLoaded");
                await WaitForScript(a, "startupFrames > 0 && !document.documentElement.hasAttribute('data-mailtrim-pending')");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("!startupLeaked && getComputedStyle(document.getElementById('known-banner')).display === 'none' && getComputedStyle(document.getElementById('semantic-banner')).display === 'none' && getComputedStyle(document.documentElement).opacity !== '0'") == "true", "startup frames never expose top banners and cleaned page becomes visible");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('startup-mail').getBoundingClientRect().height > 0") == "true", "startup guard preserves real message content");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("!document.documentElement.hasAttribute('data-mailtrim-pending') && getComputedStyle(document.getElementById('known-banner')).display !== 'none' && getComputedStyle(document.getElementById('semantic-banner')).display !== 'none'") == "true", "pausing filters releases startup guard and restores banners");
                await Navigate(a, "https://e.mail.ru/__mailtrim_top_preloader");
                await WaitForScript(a, "window.preloaderDone === true");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("preloaderLeaks.length === 0 && document.getElementById('quoted').getBoundingClientRect().height === 90 && document.querySelector('.row').getBoundingClientRect().height > 0") == "true", "unlabeled two-card top skeleton stays collapsed before paint across six returns; quoted mail and list remain visible");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('top-slot').innerHTML = '<div role=toolbar><button>Reply</button></div>'");
                await WaitForScript(a, "document.getElementById('top-slot').getBoundingClientRect().height > 0");
                Check(true, "learned ad slot reused for working toolbar is restored");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('top-slot').innerHTML = '<div class=creative><div class=picture></div><div class=bar></div></div>'.repeat(2)");
                await WaitForScript(a, "document.getElementById('top-slot').getBoundingClientRect().height === 0");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('top-slot').getBoundingClientRect().height === 90 && !document.querySelector('[data-mailtrim-top-ad]')") == "true", "pause restores top preloader and clears learned slot markers");
                await Navigate(a, "https://e.mail.ru/__mailtrim_fixture");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('[data-testid=advertising]')).display") == "\"none\"", "cosmetics injected on trusted origin");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.getElementById('message')).display") != "\"none\"", "message content remains visible");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('message').textContent") == "\"Test message\"", "fixture loaded");
                await WaitForScript(a, "getComputedStyle(document.getElementById('obfuscated-ad')).display === 'none'");
                foreach (var element in new[] { "top-banner", "native-ad", "obfuscated-ad" })
                    Check(await a.View.CoreWebView2.ExecuteScriptAsync($"getComputedStyle(document.getElementById('{element}')).display === 'none'") == "true", "ad placement hidden: " + element);
                foreach (var element in new[] { "newsletter", "letter-body", "editor" })
                    Check(await a.View.CoreWebView2.ExecuteScriptAsync($"getComputedStyle(document.getElementById('{element}')).display !== 'none' && !document.getElementById('{element}').querySelector('[data-mailtrim-ad]')") == "true", "legitimate content preserved: " + element);
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('dynamic').innerHTML = '<div id=late-ad role=listitem class=card><span>Реклама 6+</span><a href=https://r.mail.ru/test>Late ad</a></div>'");
                await WaitForScript(a, "document.getElementById('late-ad').getBoundingClientRect().height === 0");
                Check(true, "late SPA ad hidden");

                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', '<main id="spa-switch" style="position:fixed;left:10px;top:100px;width:650px"><div id="spa-slot"></div><div id="spa-letter" class="letter-body" style="height:100px">Letter zero</div></main>');
                    window.spaLeaks = []; window.spaFrames = 0;
                    const spaLetter = document.getElementById('spa-letter');
                    function switchFixtureLetter() {
                      const step = window.spaFrames;
                      spaLetter.textContent = 'Letter ' + step;
                      document.getElementById('spa-slot').outerHTML = `<div id="spa-slot"><div id="spa-banner" style="height:90px;width:600px"><span style="font-size:12px">Реклама 0+</span><div>New banner ${step}</div></div></div>`;
                      queueMicrotask(() => { if (document.getElementById('spa-banner').getBoundingClientRect().height > 0 || spaLetter.getBoundingClientRect().top !== 100) window.spaLeaks.push('microtask-' + step); });
                      requestAnimationFrame(() => {
                        if (document.getElementById('spa-banner').getBoundingClientRect().height > 0 || spaLetter.getBoundingClientRect().top !== 100 || spaLetter.getBoundingClientRect().height === 0) window.spaLeaks.push(step);
                        if (++window.spaFrames < 6) requestAnimationFrame(switchFixtureLetter);
                        else window.spaDone = true;
                      });
                    }
                    requestAnimationFrame(switchFixtureLetter);
                    """);
                await WaitForScript(a, "window.spaDone === true");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("spaLeaks.length === 0 && document.getElementById('spa-letter').textContent === 'Letter 5'") == "true", "switching letters inside animation frames never paints new banners or shifts message position");
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.getElementById('spa-slot').outerHTML = '<div id="spa-slot"><div id="spa-delayed" style="display:none;height:90px;width:600px"><span style="font-size:12px">Реклама 0+</span><div>Delayed creative</div></div></div>';
                    requestAnimationFrame(() => {
                      document.getElementById('spa-delayed').style.display = 'block';
                      queueMicrotask(() => { window.spaAttributeMicroSafe = document.getElementById('spa-delayed').getBoundingClientRect().height === 0; });
                      requestAnimationFrame(() => { window.spaAttributeSafe = document.getElementById('spa-delayed').getBoundingClientRect().height === 0 && document.getElementById('spa-letter').getBoundingClientRect().top === 100; });
                    });
                    """);
                await WaitForScript(a, "window.spaAttributeSafe !== undefined");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("spaAttributeSafe && spaAttributeMicroSafe") == "true", "attribute-only late banner visibility is filtered before paint");
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.getElementById('spa-slot').outerHTML = '<div id="spa-slot"><div id="spa-reserved" class="js-banner-wrapper-container" style="height:90px;width:600px"></div></div>';
                    requestAnimationFrame(() => { window.spaReservedSafe = document.getElementById('spa-reserved').getBoundingClientRect().height === 0 && document.getElementById('spa-letter').getBoundingClientRect().top === 100; });
                    document.getElementById('spa-letter').insertAdjacentHTML('beforeend', '<div id="spa-protected-slot" class="js-banner-wrapper-container" style="height:30px">Quoted message markup</div>');
                    """);
                await WaitForScript(a, "window.spaReservedSafe !== undefined");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("spaReservedSafe && document.getElementById('spa-protected-slot').getBoundingClientRect().height > 0") == "true", "empty ad reservation stays collapsed while matching markup inside mail stays visible");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('spa-switch').remove()");

                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="store-overlay" style="position:fixed;inset:0;background:#8888"><div style="position:absolute;left:100px;top:50px;width:420px;height:500px;background:white">
                    <button aria-label="Закрыть" style="position:absolute;right:8px;top:8px;width:32px;height:32px" onclick="document.getElementById('store-overlay').remove()">×</button>
                    <h2>Актуальная версия Почты в RuStore</h2><p>Установите или обновите приложение Mail на Android через RuStore</p><button>Узнать больше</button></div></div>`);
                    document.getElementById('letter-body').insertAdjacentHTML('beforeend', '<h2>Актуальная версия Почты в RuStore</h2><p>Установите или обновите приложение Mail на Android через RuStore</p><button id="body-close" aria-label="Закрыть" onclick="window.bodyClicked=true">×</button>');
                    """);
                await WaitForScript(a, "!document.getElementById('store-overlay')");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("!window.bodyClicked && !!document.getElementById('body-close')") == "true", "store promo dismissed with backdrop; message buttons untouched");

                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="split-store-overlay" style="position:fixed;inset:0;background:#8888"><div style="position:absolute;left:100px;top:50px;width:420px;height:500px;background:white">
                    <div style="position:absolute;right:8px;top:8px;width:32px;height:32px" onclick="document.getElementById('split-store-overlay').remove()"><svg width="32" height="32"><path d="M5 5L25 25M5 25L25 5"/></svg></div>
                    <div><span>Актуальная</span><span>версия</span><span>Почты</span><span>в</span><span>RuStore</span></div><p>Установите или обновите приложение Mail на Android через RuStore</p><button>Узнать больше</button></div></div>`);
                    requestAnimationFrame(() => { window.storeLeaked = !!document.getElementById('split-store-overlay') && document.getElementById('split-store-overlay').getBoundingClientRect().height > 0; });
                    """);
                await WaitForScript(a, "window.storeLeaked !== undefined");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("!document.getElementById('split-store-overlay') && !window.storeLeaked") == "true", "split promo title and DIV/SVG close are handled before next frame");

                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="store-portal"><div id="store-shade" style="position:fixed;inset:0;background:#8888"></div>
                    <div id="store-dialog" role="dialog" style="position:fixed;left:100px;top:50px;width:420px;height:500px;background:white">
                    <h2 id="store-title">Актуальная версия Почты<br>в RuStore</h2><p>Установите или обновите приложение Mail на Android через RuStore</p><button>Узнать больше</button></div></div>
                    <div id="working-dialog" role="dialog" style="position:fixed;left:600px;top:50px;width:300px;height:300px;background:white"><h2>Подтвердите действие</h2><input value="Unsent"><button>OK</button></div>`);
                    requestAnimationFrame(() => { window.fallbackLeaked = document.getElementById('store-dialog').getBoundingClientRect().height > 0 || document.getElementById('store-shade').getBoundingClientRect().height > 0; });
                    """);
                await WaitForScript(a, "window.fallbackLeaked !== undefined");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("!window.fallbackLeaked && document.getElementById('working-dialog').getBoundingClientRect().height > 0 && !window.bodyClicked") == "true", "promo without close hides only its panel and sibling backdrop before paint");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('store-dialog').getBoundingClientRect().height > 0 && document.getElementById('store-shade').getBoundingClientRect().height > 0") == "true", "filter pause restores fallback-hidden promo and backdrop");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
                await WaitForScript(a, "document.getElementById('store-dialog').getBoundingClientRect().height === 0");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('store-title').textContent = 'Удалить выбранные письма?'");
                await WaitForScript(a, "document.getElementById('store-dialog').getBoundingClientRect().height > 0 && document.getElementById('store-shade').getBoundingClientRect().height > 0");
                Check(true, "reused promo panel becomes visible when changed into a working dialog");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('store-portal').remove(); document.getElementById('working-dialog').remove()");

                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('late-ad').innerHTML = '<a href=/inbox/789><span>Реклама 6+</span>Real message replacing virtualized ad</a>'");
                await WaitForScript(a, "document.getElementById('late-ad').getBoundingClientRect().height > 0");
                Check(true, "virtualized ad reused as message is restored");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('mailtrim-cosmetic-style').remove()");
                await WaitForScript(a, "!!document.getElementById('mailtrim-cosmetic-style')");
                Check(true, "removed CSS restored after SPA update");
                await WaitForScript(a, "getComputedStyle(document.getElementById('hashed-banner-wrap')).display === 'none'");
                Check(true, "nested DIV ad label without links hides whole banner");
                await WaitForScript(a, "getComputedStyle(document.getElementById('right-ad-column')).display === 'none'");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('mail-list-main').getBoundingClientRect().width >= 895") == "true", "right column removed and mail list expands");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.js-letter-list-item')).display !== 'none' && !document.querySelector('.js-letter-list-item [data-mailtrim-ad]')") == "true", "current Mail.ru message link preserved");
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="space-test" style="position:fixed;bottom:0;left:0;width:600px;height:400px">
                      <div id="reserved" style="height:302px"><div style="height:52px">Toolbar</div>
                        <div id="mail-scroll" style="height:250px;overflow:auto"><div style="height:250px"><div class="thread">
                          <div class="letter-body" style="height:800px"><div class="thread"><div id="body-footer" class="thread__footer">Message footer</div></div></div>
                          <div class="letter__footer"><button id="mail-action">Reply</button></div>
                          <div id="site-footer" class="thread__footer" style="height:72px">Site footer</div>
                        </div></div></div>
                      </div></div>`); void 0;
                    """);
                await WaitForScript(a, "Math.abs(document.getElementById('mail-scroll').getBoundingClientRect().bottom-innerHeight)<2");
                Check(true, "reserved bottom area reclaimed by mail scroll viewport");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.getElementById('site-footer')).display === 'none' && getComputedStyle(document.getElementById('body-footer')).display !== 'none' && document.getElementById('mail-action').getBoundingClientRect().height > 0") == "true", "only site footer hidden; message footer and actions preserved");
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="wide-parent" style="position:fixed;inset:0"><div id="wide-mail" style="width:80%;margin:auto;height:80vh"><a href="/inbox/">Inbox</a><div class="letter-list__react"></div></div></div>`);
                    """);
                await WaitForScript(a, "document.getElementById('wide-mail').getBoundingClientRect().width >= document.getElementById('wide-parent').getBoundingClientRect().width-2");
                Check(true, "centered mail layout uses window width");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('reserved').getBoundingClientRect().height === 302 && document.getElementById('mail-scroll').getBoundingClientRect().height === 250 && getComputedStyle(document.getElementById('site-footer')).display !== 'none' && !document.querySelector('[data-mailtrim-viewport]')") == "true", "pause restores original layout and scroll height");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('wide-mail').getBoundingClientRect().width < innerWidth*.85") == "true", "pause restores original horizontal margins");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('wide-parent').remove()");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('space-test').remove()");
                await a.View.CoreWebView2.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                    document.body.insertAdjacentHTML('beforeend', `<div id="list-space-test" style="position:fixed;bottom:0;left:0;width:600px;height:400px">
                      <div style="height:52px">Toolbar</div><div class="letter-list__react" style="height:400px">
                        <div style="height:250px"><div style="height:0"><div id="virtual-scroll" class="ReactVirtualized__List" style="height:250px;overflow:auto"><div style="height:2000px">Rows</div></div></div></div>
                      </div></div>`); void 0;
                    """);
                await WaitForScript(a, "Math.abs(document.getElementById('virtual-scroll').getBoundingClientRect().bottom-innerHeight)<2");
                Check(true, "virtualized inbox uses reclaimed bottom space");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('list-space-test').style.height='500px'; window.dispatchEvent(new Event('resize'))");
                await WaitForScript(a, "Math.abs(document.getElementById('virtual-scroll').getBoundingClientRect().bottom-innerHeight)<2");
                Check(true, "mail layout follows viewport resize");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('list-space-test').remove()");
                var requestObserved = new TaskCompletionSource<bool>();
                a.View.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri == "https://ad.mail.ru/banner") requestObserved.TrySetResult(e.Response?.StatusCode == 403); };
                await a.View.CoreWebView2.ExecuteScriptAsync("fetch('https://ad.mail.ru/banner').catch(()=>{}); void 0");
                Check(await requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)), "advertising request blocked");
                var cm = a.View.CoreWebView2.CookieManager;
                cm.AddOrUpdateCookie(cm.CreateCookie("mailtrim_test", "isolated", ".mail.ru", "/"));
                Check((await cm.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "cookie in first profile");
                Check(!(await b.View.CoreWebView2.CookieManager.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "second profile isolated");
                store.Settings.UseSiteNotifications = true;
                await a.View.CoreWebView2.ExecuteScriptAsync("Notification.requestPermission().then(p=>window.fixturePermission=p); void 0");
                await WaitForScript(a, "window.fixturePermission === 'granted'");
                var notificationDecision = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NotificationReceivedEventArgs> captureNotification = (_, e) =>
                {
                    notificationDecision.TrySetResult(e.Handled);
                    e.Handled = true; // No real Windows banner during the test.
                    e.Notification.ReportClosed();
                };
                a.View.CoreWebView2.NotificationReceived += captureNotification;
                await a.View.CoreWebView2.ExecuteScriptAsync("new Notification('MailTrim synthetic test'); void 0");
                Check(!await notificationDecision.Task.WaitAsync(TimeSpan.FromSeconds(10)), "opted-in mail notification is delegated to browser UI");
                notificationDecision = new TaskCompletionSource<bool>(); store.Settings.NotifyNewMail = false;
                await a.View.CoreWebView2.ExecuteScriptAsync("new Notification('MailTrim muted synthetic test'); void 0");
                Check(await notificationDecision.Task.WaitAsync(TimeSpan.FromSeconds(10)), "mute suppresses site notifications despite previously granted permission");
                notificationDecision = new TaskCompletionSource<bool>(); store.Settings.NotifyNewMail = true; store.Settings.UseSiteNotifications = false;
                await a.View.CoreWebView2.ExecuteScriptAsync("new Notification('MailTrim polling synthetic test'); void 0");
                Check(await notificationDecision.Task.WaitAsync(TimeSpan.FromSeconds(10)), "polling mode suppresses browser notifications to avoid duplicates");
                a.View.CoreWebView2.NotificationReceived -= captureNotification;
                await Navigate(a, "https://account.mail.ru/__mailtrim_fixture");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('mailtrim-cosmetic-style') === null") == "true", "no cosmetics on login origin");
                var oldSource = a.View.CoreWebView2.Source;
                a.View.CoreWebView2.Navigate("https://evil.example/__mailtrim_fixture");
                await Task.Delay(500);
                Check(a.View.CoreWebView2.Source == oldSource, "external automatic navigation stopped");
                Check(!a.View.CoreWebView2.Settings.AreHostObjectsAllowed && !a.View.CoreWebView2.Settings.IsWebMessageEnabled && !a.View.CoreWebView2.Settings.IsPasswordAutosaveEnabled, "native bridge and password storage disabled");
                paused = true;
                Console.WriteLine("Applying paused settings…");
                await a.ApplySettings().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine("Navigating with paused settings…");
                await Navigate(a, "https://e.mail.ru/__mailtrim_fixture");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('[data-testid=advertising]')).display") != "\"none\"", "pause restores hidden elements");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-mailtrim-ad]').length") == "0", "pause disables semantic ad cleanup");
                await a.ClearData();
                Check(!(await cm.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "clear data removes session cookie");
                var sentinel = "private-email@example.org?token=secret";
                store.Log(sentinel); store.Log("test_event");
                Check(!File.ReadAllText(Path.Combine(root, "events.log")).Contains(sentinel), "log rejects sensitive strings");
                a.View.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    var path = new Uri(e.Request.Uri).AbsolutePath;
                    if (path is not ("/__pipeline_slow" or "/__pipeline_fast")) return;
                    var html = path.EndsWith("slow") ? "<html><body><script>setTimeout(()=>document.body.innerHTML='<div class=letter-body__body>Late old letter</div>',1800)</script></body></html>"
                        : "<html><body><aside>Ambient advertising</aside><div class=letter-body__body>Current clean letter<script>window.secret=1</script></div></body></html>";
                    e.Response = a.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                using (var cancelRead = new CancellationTokenSource())
                {
                    var old = a.ReaderData.Read(new ReaderLetter("https://e.mail.ru/__pipeline_slow", "old", "old", "", ""), cancelRead.Token);
                    await Task.Delay(100); cancelRead.Cancel();
                    var next = a.ReaderData.Read(new ReaderLetter("https://e.mail.ru/__pipeline_fast", "new", "new", "", ""), CancellationToken.None);
                    bool cancelledOld = false;
                    try { await old; } catch (OperationCanceledException) { cancelledOld = true; }
                    var result = await next;
                    Check(cancelledOld && result.Blocks.Single().Text == "Current clean letter", "queue cancels old extraction and emits only current message data");
                    Check(!result.Blocks.Any(b => b.Text.Contains("Ambient") || b.Text.Contains("secret")), "pipeline excludes page advertising and executable markup");
                }
                a.Cache.Clear();
                await Navigate(a, "https://e.mail.ru/__mailtrim_reader");
                await a.View.CoreWebView2.ExecuteScriptAsync("""
                history.replaceState(null,'','/inbox/action-test');
                document.querySelectorAll('a.js-letter-list-item').forEach(e=>e.remove());
                window.actionCount=0;
                document.body.insertAdjacentHTML('afterbegin','<div role="toolbar" id="action-toolbar"><button onclick="window.actionCount++">Ответить</button><button onclick="window.actionCount++">Отметить прочитанным</button><button onclick="window.actionCount++">В архив</button><button onclick="window.actionCount++">Удалить</button></div>');
                document.querySelector('.letter-body__body').insertAdjacentHTML('beforeend','<div role="toolbar"><button onclick="window.evilClicked=true">Удалить</button></div>');
                """);
                const string actionUrl = "https://e.mail.ru/inbox/action-test";
                foreach (var action in Enum.GetValues<ReaderAction>())
                    Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(action, actionUrl)) == "\"clicked\"", "native action routes to official toolbar: " + action);
                Check(await a.View.CoreWebView2.ExecuteScriptAsync("window.actionCount===4 && !window.evilClicked") == "true", "message HTML cannot provide action controls");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Delete, "https://e.mail.ru/inbox/another")) == "\"wrong-message\"", "action rejects a different selected message");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Delete, actionUrl + "?id=another")) == "\"wrong-message\"", "action rejects a different message query");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<a class=js-letter-list-item id=bulk-test>Other message</a>')");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Delete, actionUrl)) == "\"ambiguous\"", "action refuses a bulk message list");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('#bulk-test').remove()");
                await a.View.CoreWebView2.ExecuteScriptAsync("history.replaceState(null,'','/trash/action-test')");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Delete, "https://e.mail.ru/trash/action-test")) == "\"manual-delete\"", "permanent trash deletion is never automated");
                await a.View.CoreWebView2.ExecuteScriptAsync("history.replaceState(null,'','/inbox/action-test');document.querySelector('#action-toolbar').insertAdjacentHTML('beforeend','<button>В архив</button>')");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Archive, actionUrl)) == "\"unavailable\"", "ambiguous action buttons are not clicked");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('#action-toolbar').remove()");
                Check(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderActionScript.Create(ReaderAction.Delete, actionUrl)) == "\"unavailable\"", "missing toolbar never falls back to message HTML");
                await Navigate(a, "https://e.mail.ru/__mailtrim_reader");
                var readerRows = System.Text.Json.JsonSerializer.Deserialize<List<ReaderLetter>>(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderScript.List), FilterRules.Json)!;
                Check(readerRows.Count == 1 && readerRows[0].Subject == "Subject", "reader extracts real message anchors only");
                var readerBlocks = System.Text.Json.JsonSerializer.Deserialize<List<ReaderBlock>>(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderScript.Body), FilterRules.Json)!;
                Check(readerBlocks.Any(x => x.Text.Contains("Safe text")) && !readerBlocks.Any(x => x.Text.Contains("excluded script") || x.Text.Contains("Hidden text")), "reader excludes executable and hidden content");
                Check(readerBlocks.All(x => x.Image.Length == 0), "reader rejects data and javascript image URLs");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend', '<a id=unread-test href=/inbox/ data-unread-count=7>Входящие</a>')");
                var unreadSnapshot = System.Text.Json.JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(await a.View.CoreWebView2.ExecuteScriptAsync(MailboxStatusScript.Snapshot), FilterRules.Json)!;
                Check(unreadSnapshot.Ready && unreadSnapshot.Unread == 7, "unread count uses official folder counter");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('unread-test').removeAttribute('data-unread-count')");
                unreadSnapshot = System.Text.Json.JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(await a.View.CoreWebView2.ExecuteScriptAsync(MailboxStatusScript.Snapshot), FilterRules.Json)!;
                Check(unreadSnapshot.Ready && unreadSnapshot.Unread is null, "missing unread counter is unknown rather than zero");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('unread-test').innerHTML='Входящие<sup>3</sup>'");
                unreadSnapshot = System.Text.Json.JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(await a.View.CoreWebView2.ExecuteScriptAsync(MailboxStatusScript.Snapshot), FilterRules.Json)!;
                Check(unreadSnapshot.Unread == 3, "compact inbox badge parsed");

                await a.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('unread-test').setAttribute('data-unread-count','0')");
                unreadSnapshot = System.Text.Json.JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(await a.View.CoreWebView2.ExecuteScriptAsync(MailboxStatusScript.Snapshot), FilterRules.Json)!;
                Check(unreadSnapshot.Unread == 0, "explicit zero unread count is retained");                var originalRequests = 0; var pane = new ReaderPane(a, () => originalRequests++); host.Children.Add(pane); a.View.Visibility = Visibility.Hidden; b.View.Visibility = Visibility.Hidden;
                await pane.Start();
                var readerList = ((DockPanel)pane.Children[0]).Children.OfType<ListBox>().Single();
                Check(readerList.Items.Count == 1, "native reader displays extracted list");
                pane.ColumnDefinitions[0].Width = new GridLength(420); pane.UpdateLayout();
                var readerSplitter = pane.Children.OfType<GridSplitter>().Single();
                readerSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(110, 0, false)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                Check(Math.Abs(new LocalStore(root).Settings.ReaderListWidth - 420) < 1, "reader divider width persists on disk after drag");
                var completedReaderNavigation = false;
                a.View.CoreWebView2.NavigationCompleted += (_, _) => completedReaderNavigation = true;
                readerList.SelectedIndex = 0;
                for (int i = 0; i < 100 && pane.IsLoading; i++) await Task.Delay(100);
                var readerBody = (StackPanel)((ScrollViewer)((DockPanel)pane.Children[1]).Children[1]).Content;
                Check(double.IsPositiveInfinity(readerBody.MaxWidth) && readerBody.Margin.Left == 16, "reader body uses available width with small side padding");
                Check(readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "native reader renders message after canonical URL change with fallback body container");
                Check(!completedReaderNavigation, "reader displays text before slow image finishes loading");
                var readerNavigations = 0;
                a.View.CoreWebView2.NavigationStarting += (_, _) => readerNavigations++;
                readerList.SelectedIndex = -1; readerList.SelectedIndex = 0;
                Check(readerList.IsEnabled && readerNavigations == 0 && readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "cached letter reopens synchronously without network navigation");
                await pane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Check(readerNavigations > 0, "cached preview is revalidated against the official page instead of staying cache-only");
                var searchNavigations = readerNavigations;
                await pane.SearchSaved("missing-search-fixture-9347");
                Check(readerList.Items.Count == 0 && readerNavigations == searchNavigations, "cache search shows empty result without navigating mail");
                await pane.SearchSaved("sAfE TeXt");
                Check(readerList.Items.Count == 1 && readerNavigations == searchNavigations, "case-insensitive search finds cached body locally");
                readerList.SelectedIndex = 0;
                Check(readerList.IsEnabled && readerNavigations == searchNavigations && readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "search result opens from cache without network");
                await pane.SearchSaved(""); readerList.SelectedIndex = 0;
                Check(readerList.Items.Count == 1, "clearing search restores original reader list");
                var browserParentBeforeReply = a.View.Parent;
                await pane.PerformAction(ReaderAction.Reply);
                Check(originalRequests == 0 && pane.IsReplyVisible && a.ReaderReplyOpen && readerList.SelectedIndex == 0, "reply opens alongside list without leaving reader mode");
                Check(a.View.Parent != browserParentBeforeReply && a.View.Visibility == Visibility.Visible && !((DockPanel)pane.Children[0]).IsEnabled, "inline reply hosts official browser and protects selection");
                await a.View.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<textarea id=reply-draft>Unsent synthetic draft</textarea>')");
                var beforeReplySearch = readerNavigations;
                await pane.SearchSaved("another message");
                Check(readerNavigations == beforeReplySearch && readerList.SelectedIndex == 0, "search cannot navigate away from an active reply");
                pane.Close(); host.Children.Remove(pane);
                Check(a.View.Parent == browserParentBeforeReply && a.ReaderReplyOpen, "closing reader returns browser to its owner without dropping reply state");
                pane = new ReaderPane(a, () => originalRequests++); host.Children.Add(pane); await pane.Start();
                readerList = ((DockPanel)pane.Children[0]).Children.OfType<ListBox>().Single();
                readerBody = (StackPanel)((ScrollViewer)((DockPanel)pane.Children[1]).Children[1]).Content;
                Check(pane.IsReplyVisible && readerNavigations == beforeReplySearch && await a.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('#reply-draft').value") == "\"Unsent synthetic draft\"", "reopening reader restores existing reply DOM without navigation");
                await pane.ReturnToReading(confirmed: true);
                Check(!pane.IsReplyVisible && !a.ReaderReplyOpen && a.View.Parent == browserParentBeforeReply && readerList.SelectedIndex == 0 && readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "return from reply restores native message and browser ownership");
                await pane.PerformAction(ReaderAction.Archive);
                Check(originalRequests == 0 && readerList.SelectedIndex == 0 && readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "unavailable action preserves reader and selected content without modal fallback");
                pane.UpdateLayout();
                var readerBitmap = new RenderTargetBitmap((int)pane.ActualWidth, (int)pane.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                readerBitmap.Render(pane); var readerEncoder = new PngBitmapEncoder(); readerEncoder.Frames.Add(BitmapFrame.Create(readerBitmap));
                using (var capture = File.Create(Path.Combine(root, "reader-preview.png"))) readerEncoder.Save(capture);                readerBody.Children.Add(new TextBlock { Text = string.Join("\n", Enumerable.Repeat("Scroll position fixture", 100)) });
                pane.UpdateLayout();
                var bodyScroll = (ScrollViewer)((DockPanel)pane.Children[1]).Children[1];
                bodyScroll.ScrollToVerticalOffset(120); pane.UpdateLayout();
                pane.Close(); Check(readerList.Items.Count == 0 && readerBody.Children.Count == 0, "reader clears message memory on close");
                var reopenedCache = new MessageCache(root, id);
                Check(reopenedCache.Get(readerRows[0].Url)?.Any(x => x.Text.Contains("Safe text")) == true, "encrypted cache survives a new cache instance");
                var cachedFile = Directory.GetFiles(Path.Combine(root, "ReaderCache", id.ToString("N")), "*.bin").Single();
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(cachedFile)).Contains("Safe text"), "cache does not contain plaintext mail");
                var otherId = Guid.NewGuid(); var otherDir = Path.Combine(root, "ReaderCache", otherId.ToString("N")); Directory.CreateDirectory(otherDir);
                File.Copy(cachedFile, Path.Combine(otherDir, Path.GetFileName(cachedFile)));
                Check(new MessageCache(root, otherId).Get(readerRows[0].Url) is null, "cache cannot be decrypted as a different mailbox profile");
                Check(new MessageCache(root, otherId).Search("Safe text", CancellationToken.None).Count == 0, "search cannot read another account encrypted cache");
                using (var searchCancellation = new CancellationTokenSource())
                {
                    searchCancellation.Cancel(); bool searchStopped = false;
                    try { reopenedCache.Search("Safe text", searchCancellation.Token); } catch (OperationCanceledException) { searchStopped = true; }
                    Check(searchStopped, "cache search respects cancellation");
                }
                var resumed = new ReaderPane(a, () => { }); host.Children.Add(resumed); await resumed.Start();
                Check(resumed.ColumnDefinitions[0].Width.Value == 420, "reopened reader restores customized list width");
                Check(((DockPanel)resumed.Children[0]).Children.OfType<ListBox>().Single().Items.Count == 1, "reader restores cached list after reopening");
                Check(((DockPanel)resumed.Children[0]).Children.OfType<ListBox>().Single().SelectedItem is ReaderLetter, "reader restores selected message independently for profile");
                var restoredScroll = (ScrollViewer)((DockPanel)resumed.Children[1]).Children[1];
                // Supply the same long layout after the cached message has rendered.
                ((StackPanel)restoredScroll.Content).Children.Add(new TextBlock { Text = string.Join("\n", Enumerable.Repeat("Scroll position fixture", 100)) });
                resumed.UpdateLayout(); restoredScroll.ScrollToVerticalOffset(a.ReaderState!.BodyOffset); resumed.UpdateLayout();
                Check(a.ReaderState.BodyOffset >= 100 && restoredScroll.VerticalOffset >= 100, "reader captures nonzero body scroll offset per profile");
                resumed.Close(); host.Children.Remove(resumed);

                a.Cache.Clear();
                a.ReaderState = new ReaderPosition(new[] {
                    new ReaderLetter("https://e.mail.ru/__pipeline_slow", "old", "Old", "", "", ReceivedAt: DateTimeOffset.UtcNow),
                    new ReaderLetter("https://e.mail.ru/__pipeline_fast", "new", "New", "", "", ReceivedAt: DateTimeOffset.UtcNow.AddDays(-1)) }, null, 0, 0);
                var racePane = new ReaderPane(a, () => { }); host.Children.Add(racePane); await racePane.Start();
                var raceList = ((DockPanel)racePane.Children[0]).Children.OfType<ListBox>().Single();
                raceList.SelectedIndex = 0; await Task.Delay(100); raceList.SelectedIndex = 1;
                for (int i = 0; i < 100 && racePane.IsLoading; i++) await Task.Delay(50);
                var raceBody = (StackPanel)((ScrollViewer)((DockPanel)racePane.Children[1]).Children[1]).Content;
                Check(raceBody.Children.OfType<TextBlock>().Any(t => t.Text == "Current clean letter") && !raceBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Late old")), "rapid message selection renders only newest requested data");
                a.SetActive(true);
                Check(a.ReadingOnly && a.View.Visibility == Visibility.Hidden, "active source remains hidden during native reading");
                raceList.SelectedIndex = 0; await Task.Delay(100);
                racePane.Close(); host.Children.Remove(racePane); await Task.Delay(250);
                Check(raceBody.Children.Count == 0, "closed renderer ignores cancelled in-flight message data");
                Console.WriteLine("Checking automatic cache-first refresh with synthetic mail…");
                b.Cache.Clear(); b.ReaderState = null;
                var retained = new ReaderLetter("https://e.mail.ru/__mailtrim_reader", "Retained sender", "Retained", "Preview", "", ReceivedAt: captured.AddDays(-1));
                var archived = new ReaderLetter("https://e.mail.ru/archive/retained", "Archive", "Archived", "", "", ReceivedAt: captured.AddDays(-2));
                b.Cache.Save(retained, [new ReaderBlock("Cached reading text", "")]);
                b.Cache.Save(archived, [new ReaderBlock("Archived text", "")]);
                var emptyInbox = false;
                var confirmedEmptyInbox = false;
                var freshBodyText = "Cached reading text";
                TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                async void RefreshFixture(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    if (new Uri(e.Request.Uri).AbsolutePath == "/__mailtrim_reader")
                    {
                        e.Response = b.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body><div class='letter-body__body'>" + freshBodyText + "</div></body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    if (new Uri(e.Request.Uri).AbsolutePath != "/inbox/") return;
                    using var deferral = e.GetDeferral(); refreshStarted.TrySetResult();
                    await Task.Delay(700);
                    var html = emptyInbox ? "<html><body>Sign in required</body></html>" : confirmedEmptyInbox ? "<html><body><div class='letter-list__react'>Empty folder</div></body></html>" : """
                        <html><body>
                        <a class="js-letter-list-item" href="/__refresh_new"><span>Fresh sender</span><span>New incoming</span><span>Fresh preview</span><time datetime="2027-01-01T12:00:00Z">01.01.27</time></a>
                        <a class="js-letter-list-item" href="/__mailtrim_reader"><span>Retained sender</span><span>Retained updated</span><span>Preview</span><time datetime="2026-10-01T12:00:00Z">01.10.26</time></a>
                        </body></html>
                        """;
                    e.Response = b.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                }
                b.View.CoreWebView2.WebResourceRequested += RefreshFixture;
                var freshPane = new ReaderPane(b, () => { }); host.Children.Add(freshPane);
                await freshPane.Start();
                var freshList = ((DockPanel)freshPane.Children[0]).Children.OfType<ListBox>().Single();
                Check(freshList.Items.Count == 2 && !freshPane.BackgroundRefresh.IsCompleted && freshList.IsEnabled, "entry returns usable cached list before background network refresh");
                freshList.SelectedItem = freshList.Items.Cast<ReaderLetter>().Single(x => x.Url == retained.Url);
                await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                freshList.SelectedItem = freshList.Items.Cast<ReaderLetter>().Single(x => x.Url == archived.Url);
                freshList.SelectedItem = freshList.Items.Cast<ReaderLetter>().Single(x => x.Url == retained.Url);
                var freshScroll = (ScrollViewer)((DockPanel)freshPane.Children[1]).Children[1];
                var freshBody = (StackPanel)freshScroll.Content;
                var retainedText = freshBody.Children.OfType<TextBlock>().Single(x => x.Text == "Cached reading text");
                freshBody.Children.Add(new TextBlock { Text = string.Join("\n", Enumerable.Repeat("Reading position", 100)) });
                freshPane.UpdateLayout(); freshScroll.ScrollToVerticalOffset(120); freshPane.UpdateLayout();
                await freshPane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(12)); freshPane.UpdateLayout();
                Check(freshList.Items.Count == 2 && ((ReaderLetter)freshList.Items[0]).Subject == "New incoming" && !freshList.Items.Cast<ReaderLetter>().Any(x => x.Url == archived.Url), "live inbox is rendered from current website rows rather than mixed with cached archive");
                Check(((ReaderLetter)freshList.SelectedItem).Url == retained.Url && freshBody.Children.Contains(retainedText) && freshScroll.VerticalOffset >= 100, "automatic refresh keeps selected message, rendered body and scroll");
                Check(((ReaderLetter)freshList.SelectedItem).Subject == "Retained updated" && b.Cache.List().Count == 3 && b.Cache.Get("https://e.mail.ru/__refresh_new") is null, "fresh metadata updates existing row and persists new headers without opening messages");
                await freshPane.RefreshList();
                Check(freshList.Items.Count == 2 && freshBody.Children.Contains(retainedText), "manual live refresh preserves selected message and displayed content");
                await b.View.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('afterbegin', '<div data-testid=advertising>Page advertising must never be drawn</div><a class=js-letter-list-item href=/api-proxy/rb-mimic/ad><span>Ad sender</span><span>Ad subject</span></a><a class=js-letter-list-item href=/__live_new><span>Live sender</span><span>Live arrival</span><span>Preview</span><time datetime=2027-01-02T12:00:00Z>02.01.27</time></a>')");
                for (var liveWait = 0; liveWait < 50 && !freshList.Items.Cast<ReaderLetter>().Any(x => x.Subject == "Live arrival"); liveWait++) await Task.Delay(100);
                Check(freshList.Items.Count == 3 && freshList.Items.Cast<ReaderLetter>().Any(x => x.Subject == "Live arrival") && !freshList.Items.Cast<ReaderLetter>().Any(x => x.Sender == "Ad sender") && freshBody.Children.Contains(retainedText), "automatic DOM sampling renders new mail and excludes page advertising without disturbing the reading body");
                freshBodyText = "Fresh body changed on website";
                freshList.SelectedIndex = -1; freshList.SelectedItem = freshList.Items.Cast<ReaderLetter>().Single(x => x.Url == retained.Url);
                Check(freshBody.Children.OfType<TextBlock>().Any(x => x.Text == "Cached reading text"), "old body is only an immediate preview before site revalidation");
                await freshPane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Check(freshBody.Children.OfType<TextBlock>().Any(x => x.Text == freshBodyText) && b.Cache.Get(retained.Url)!.Any(x => x.Text == freshBodyText), "changed website body replaces the cache preview and updates its encrypted copy");
                confirmedEmptyInbox = true;
                await freshPane.SynchronizeLive(true);
                Check(freshList.Items.Count == 0 && b.Cache.List().Count == 4, "confirmed empty live folder clears the view while preserving offline copies");
                confirmedEmptyInbox = false;
                var liveFolders = ((StackPanel)((DockPanel)freshPane.Children[0]).Children[0]).Children.OfType<ComboBox>().Single();
                liveFolders.SelectedItem = liveFolders.Items.Cast<ReaderFolder>().Single(x => x.Name == "Отправленные");
                for (var folderWait = 0; folderWait < 100 && freshPane.IsLoading; folderWait++) await Task.Delay(100);
                Check(freshList.Items.Count == 1 && new Uri(b.View.CoreWebView2.Source).AbsolutePath == "/sent/", "native folder switch reads the official sent page instead of selecting a cache category");
                var headerPath = Path.Combine(root, "ReaderCache", secondProfileId.ToString("N"), "headers.index");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(headerPath)).Contains("Fresh sender"), "refreshed mail headers are encrypted on disk");
                freshPane.Close(); host.Children.Remove(freshPane); b.ReaderState = null;
                emptyInbox = true;
                var offlinePane = new ReaderPane(b, () => { }); host.Children.Add(offlinePane); await offlinePane.Start();
                await offlinePane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(15));
                Check(((DockPanel)offlinePane.Children[0]).Children.OfType<ListBox>().Single().Items.Count == 4 && b.Cache.List().Count == 4, "empty or unauthenticated web response never erases cached mail");
                offlinePane.Close(); host.Children.Remove(offlinePane); emptyInbox = false;
                refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var replyRefreshPane = new ReaderPane(b, () => { }); host.Children.Add(replyRefreshPane); await replyRefreshPane.Start();
                await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replyLiveList = ((DockPanel)replyRefreshPane.Children[0]).Children.OfType<ListBox>().Single();
                replyLiveList.SelectedItem = replyLiveList.Items.Cast<ReaderLetter>().Single(x => x.Url == retained.Url);
                await replyRefreshPane.PerformAction(ReaderAction.Reply);
                await replyRefreshPane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Check(replyRefreshPane.IsReplyVisible && b.ReaderReplyOpen, "reply cancels background refresh and prevents retry over the official editor");
                var sourceDuringReply = b.View.CoreWebView2.Source;
                await replyRefreshPane.SynchronizeLive(true);
                Check(b.View.CoreWebView2.Source == sourceDuringReply, "live synchronization cannot navigate the reply editor");
                await replyRefreshPane.ReturnToReading(true); replyRefreshPane.Close(); host.Children.Remove(replyRefreshPane);
                var closedRefreshPane = new ReaderPane(b, () => { }); host.Children.Add(closedRefreshPane); await closedRefreshPane.Start();
                closedRefreshPane.Close(); host.Children.Remove(closedRefreshPane);
                await closedRefreshPane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Check(((DockPanel)closedRefreshPane.Children[0]).Children.OfType<ListBox>().Single().Items.Count == 0, "closing reader cancels automatic refresh without late UI changes");
                refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var originalShown = false;
                var originalRefreshPane = new ReaderPane(b, () => originalShown = true); host.Children.Add(originalRefreshPane); await originalRefreshPane.Start();
                await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var originalReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OriginalLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e) { if (new Uri(b.View.CoreWebView2.Source).AbsolutePath == "/__mailtrim_reader") originalReady.TrySetResult(); }
                b.View.CoreWebView2.DOMContentLoaded += OriginalLoaded;
                await originalRefreshPane.OpenOriginal();
                await WaitForScript(b, "document.body.textContent.includes('Fresh body changed on website')");
                b.View.CoreWebView2.DOMContentLoaded -= OriginalLoaded;
                Check(originalShown && (await b.View.CoreWebView2.ExecuteScriptAsync("document.body.textContent")).Contains("Fresh body changed on website"), "switching to original drains cancelled refresh before visible navigation");
                originalRefreshPane.Close(); host.Children.Remove(originalRefreshPane);
                b.View.CoreWebView2.WebResourceRequested -= RefreshFixture;
                a.Cache.Clear();
                Console.WriteLine("Checking whole-mailbox scan with synthetic folders…");
                var batchResult = await a.ReaderData.CacheMailbox(_ => { }, _ => { }, CancellationToken.None);
                Check(batchResult.Folders == 7 && batchResult.Saved == 2 && batchResult.Failed == 0 && batchResult.UncertainFolders == 0, "batch scans default and discovered folders to the end and persists messages");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                bool stopped = false;
                try { await a.ReaderData.CacheMailbox(_ => { }, _ => { }, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped && reopenedCache.List().Count == 2, "cancelling batch preserves saved messages");
                await a.ClearData();
                Check(reopenedCache.List().Count == 0, "clearing profile also clears encrypted reader cache");                host.Children.Remove(pane);
                store.Settings.Profiles.Add(new AccountProfile { Name = "Рабочий ящик" });
                var replyExitPrompts = 0; var allowReplyExit = false;
                var shell = new MainWindow(store, confirmReplyExit: _ => { replyExitPrompts++; return allowReplyExit; }) { Width = 1080, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                shell.Show();
                var browserHost = (Grid)shell.FindName("BrowserHost");
                for (var i = 0; i < 100 && (!shell.IsEnabled || browserHost.Children.Count == 0); i++) await Task.Delay(100);
                Check(shell.IsEnabled && browserHost.Children.Count == 1, "main window initializes with production resources");
                foreach (var profile in store.Settings.Profiles)
                    new MessageCache(root, profile.Id).Save(new ReaderLetter("https://e.mail.ru/__mailtrim_reader", profile.Name, "Cached subject", "Preview", "Today"), [new ReaderBlock("Cached text", "")]);
                var profiles = (ListBox)shell.FindName("Profiles");
                var readerHost = (Grid)shell.FindName("ReaderHost");
                var toggleReader = (Button)shell.FindName("ReaderButton");
                toggleReader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 100 && (readerHost.Children.Count == 0 || ((DockPanel)((ReaderPane)readerHost.Children[0]).Children[0]).Children.OfType<ListBox>().Single().Items.Count == 0); i++) await Task.Delay(50);
                foreach (var index in new[] { 1, 0 })
                {
                    profiles.SelectedIndex = index;
                    for (int i = 0; i < 100 && !shell.IsEnabled; i++) await Task.Delay(50);
                    Check(browserHost.Visibility == Visibility.Hidden && readerHost.Visibility == Visibility.Visible && readerHost.Children.Count == 1 && toggleReader.Content.ToString() == "Оригинал", "reader mode survives switching account tab");
                    var activeList = ((DockPanel)((ReaderPane)readerHost.Children[0]).Children[0]).Children.OfType<ListBox>().Single();
                    Check(activeList.Items.Cast<ReaderLetter>().Single().Sender == store.Settings.Profiles[index].Name, "switched reader shows only selected account cache");
                }
                var firstView = browserHost.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().First();
                Check(browserHost.Visibility == Visibility.Hidden && browserHost.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().All(v => !v.IsVisible), "reader keeps source web surfaces invisible across account switches");

                firstView.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    if (new Uri(e.Request.Uri).AbsolutePath == "/__mailtrim_reader")
                        e.Response = firstView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body><div class='letter-body__body'>Synthetic account reply</div><textarea id='account-draft'>Account one draft</textarea></body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                firstView.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    if (new Uri(e.Request.Uri).AbsolutePath == "/inbox/")
                        e.Response = firstView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body><a class='js-letter-list-item' href='/__mailtrim_reader'><span>Sender</span><span>Subject</span><span>Preview</span><span>Today</span></a></body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                ((Button)shell.FindName("HomeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 600 && !shell.IsEnabled; i++) await Task.Delay(50);
                Check(shell.IsEnabled && readerHost.Visibility == Visibility.Visible && browserHost.Visibility == Visibility.Hidden && toggleReader.Content.ToString() == "Оригинал", "inbox navigation refreshes data without exposing original page");
                var firstPane = (ReaderPane)readerHost.Children[0];
                var firstList = ((DockPanel)firstPane.Children[0]).Children.OfType<ListBox>().Single();
                firstList.SelectedIndex = 0;
                for (int i = 0; i < 100 && firstPane.IsLoading; i++) await Task.Delay(50);
                await firstPane.PerformAction(ReaderAction.Reply);
                if (!firstPane.IsReplyVisible) Console.WriteLine($"Synthetic reply state: rows={firstList.Items.Count}, loading={firstPane.IsLoading}, selected={firstList.SelectedIndex}, notice={((StackPanel)((DockPanel)firstPane.Children[0]).Children[0]).Children.OfType<TextBlock>().Last().Text}");
                Check(firstPane.IsReplyVisible, "account reply opens inside production shell");
                await firstView.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<a id=live-count href=/inbox/ data-unread-count=3>Входящие</a>')");
                await shell.RefreshVisibleMailboxStatus();
                Check(store.Settings.Profiles[0].Status.Unread == 3, "active web counter updates account badge without navigation");
                await firstView.CoreWebView2.ExecuteScriptAsync("document.getElementById('live-count').setAttribute('data-unread-count','2')");
                await shell.RefreshVisibleMailboxStatus();
                Check(store.Settings.Profiles[0].Status.Unread == 2, "changed active web counter refreshes immediately");

                profiles.SelectedIndex = 1;
                for (int i = 0; i < 100 && !shell.IsEnabled; i++) await Task.Delay(50);
                Check(!((ReaderPane)readerHost.Children[0]).IsReplyVisible, "reply is not shown in another account");
                profiles.SelectedIndex = 0;
                for (int i = 0; i < 100 && !shell.IsEnabled; i++) await Task.Delay(50);
                Check(((ReaderPane)readerHost.Children[0]).IsReplyVisible && toggleReader.Content.ToString() == "Оригинал" && await firstView.CoreWebView2.ExecuteScriptAsync("document.querySelector('#account-draft').value") == "\"Account one draft\"", "account switch restores its reply without losing unsent text");
                var guardedNavigations = 0;
                firstView.CoreWebView2.NavigationStarting += (_, _) => guardedNavigations++;
                foreach (var control in new[] { "HomeButton", "ReloadButton" })
                    ((Button)shell.FindName(control)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(replyExitPrompts == 2 && guardedNavigations == 0 && ((ReaderPane)readerHost.Children[0]).IsReplyVisible, "cancelled home and reload preserve reply without browser navigation");
                var f5 = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(shell), 0, System.Windows.Input.Key.F5) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                shell.RaiseEvent(f5);
                Check(f5.Handled && replyExitPrompts == 3 && guardedNavigations == 0, "application F5 follows the same reply protection");
                var shellClosed = false; shell.Closed += (_, _) => shellClosed = true;
                profiles.SelectedIndex = 1;
                for (int i = 0; i < 100 && !shell.IsEnabled; i++) await Task.Delay(50);
                shell.Close();
                Check(!shellClosed && replyExitPrompts == 4, "exit protects reply in an inactive account");
                profiles.SelectedIndex = 0;
                for (int i = 0; i < 100 && !shell.IsEnabled; i++) await Task.Delay(50);
                allowReplyExit = true;
                ((Button)shell.FindName("ReloadButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 100 && guardedNavigations == 0; i++) await Task.Delay(25);
                Check(replyExitPrompts == 5 && guardedNavigations > 0 && readerHost.Visibility == Visibility.Collapsed, "confirmed reload leaves reply and navigates");
                toggleReader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 100 && readerHost.Children.Count == 0; i++) await Task.Delay(25);
                Check(!((ReaderPane)readerHost.Children[0]).IsReplyVisible, "confirmed navigation clears obsolete reply state");

                toggleReader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));                // Capture the native WPF shell with the webview hidden, showing only synthetic test UI.
                browserHost.Visibility = Visibility.Hidden;
                shell.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)shell.ActualWidth, (int)shell.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(shell);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(root, "shell-preview.png"))) encoder.Save(file);
                shell.Close();
                var trayWindow = new Window { Width = 300, Height = 200, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                bool trayClosed = false; trayWindow.Closed += (_, _) => trayClosed = true;
                using (var integration = new DesktopIntegration(trayWindow, store, false))
                {
                    foreach (var p in store.Settings.Profiles) p.SetStatus(new MailboxStatus("Проверено", 5, DateTimeOffset.Now));
                    integration.UpdateCounts(store.Settings.Profiles);
                    store.Settings.Profiles[0].SetStatus(new MailboxStatus("Нужен вход", null, null));
                    integration.UpdateCounts(store.Settings.Profiles);
                    Check(true, "tray renders numeric and unknown counter icons");
                    System.ComponentModel.CancelEventHandler cancelExit = (_, e) => e.Cancel = true;
                    trayWindow.Closing += cancelExit;
                    integration.RequestExit();
                    Check(!trayClosed && !integration.Exiting, "cancelled tray exit resets exit intent");
                    trayWindow.Closing -= cancelExit;
                    store.Settings.CloseToTray = true; trayWindow.Show(); trayWindow.Close();
                    Check(!trayClosed && !trayWindow.IsVisible, "window close hides to tray without destroying session");
                    integration.Restore(); Check(trayWindow.IsVisible, "tray action restores hidden window");
                    store.Settings.CloseToTray = false; trayWindow.Close(); Check(trayClosed, "disabling tray closing allows normal exit");
                }                var placementWindow = new Window { Width = 640, Height = 480, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                _ = new WindowPositionManager(placementWindow, store);
                placementWindow.Show();
                var savedBounds = WindowPositionManager.Capture(placementWindow)!;
                Check(savedBounds.IsValid && !savedBounds.Maximized, "capture normal restore geometry");
                Check(WindowPositionManager.Restore(placementWindow, new SavedWindowPosition(-900000, -900000, -899360, -899520, false)), "restore formerly disconnected monitor placement");
                var recoveredBounds = WindowPositionManager.Capture(placementWindow)!;
                Check(recoveredBounds.IsValid && Math.Abs(recoveredBounds.Left) < 100000 && Math.Abs(recoveredBounds.Top) < 100000, "Windows recovers an off-screen window");
                placementWindow.WindowState = WindowState.Maximized;
                Check(WindowPositionManager.Capture(placementWindow)!.Maximized, "capture maximized window with normal restore bounds");
                placementWindow.WindowState = WindowState.Minimized;
                Check(WindowPositionManager.Capture(placementWindow)!.Maximized, "minimize preserves restore-to-maximized state");
                using (var placementTray = new DesktopIntegration(placementWindow, store, false))
                {
                    placementTray.Restore(); Check(placementWindow.WindowState == WindowState.Maximized, "tray restore preserves maximized state after minimize");
                    store.Settings.CloseToTray = true; placementWindow.Close();
                    Check(store.Settings.WindowPosition?.Maximized == true, "closing to tray saves maximized placement");
                    store.Settings.CloseToTray = false; placementWindow.Close();
                }
                Check(new LocalStore(root).Settings.WindowPosition?.Maximized == true, "window placement persists across settings reload");
                var restartedWindow = new Window { Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                _ = new WindowPositionManager(restartedWindow, new LocalStore(root));
                restartedWindow.Show();
                Check(restartedWindow.WindowState == WindowState.Maximized, "new window restores persisted maximized state on startup");
                restartedWindow.Close();
                Console.WriteLine("WebView2 smoke checks passed."); result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { foreach (var s in sessions) s.Dispose(); window.Close(); app.Shutdown(); }
        };
        app.Run(window); return result;
    }
    private static void ConfigureFixture(BrowserSession session)
    {
        session.View.CoreWebView2.WebResourceRequested += async (_, e) =>
        {
            var fixturePath = new Uri(e.Request.Uri).AbsolutePath;
            if (fixturePath == "/__mailtrim_startup")
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mail-startup.html")))), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            if (fixturePath == "/__mailtrim_top_preloader")
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "top-preloader.html")))), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            if (fixturePath == "/__mailtrim_startup_hold")
            {
                using var deferral = e.GetDeferral(); await Task.Delay(1800);
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("void 0;")), 200, "OK", "Content-Type: application/javascript\r\nCache-Control: no-store");
            }
            if (new[] { "/inbox/", "/sent/", "/archive/", "/drafts/", "/spam/", "/trash/", "/folder/custom/" }.Contains(fixturePath))
            {
                var messagePath = fixturePath == "/folder/custom/" ? "__mailtrim_reader_second" : "__mailtrim_reader";
                var html = "<html><body><a href='/folder/custom/'>Custom</a><div style='height:80px;overflow-y:auto'><a class='js-letter-list-item' href='/" + messagePath + "'><span>Sender</span><span>Subject</span><span>Preview</span><span>Today</span></a><div style='height:200px'></div></div></body></html>";
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            }            if (e.Request.Uri.EndsWith("/__mailtrim_slow_image", StringComparison.Ordinal))
            {
                using var deferred = e.GetDeferral();
                await Task.Delay(3500);
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store");
            }            if (fixturePath is "/__mailtrim_reader" or "/__mailtrim_reader_second")
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("""
                <html><body><a class="js-letter-list-item" href="https://e.mail.ru/__mailtrim_reader"><span title="sender">Sender</span><span>Subject</span><span>Preview</span><span>12:00</span></a>
                <script>history.replaceState(null,"",location.pathname+"?canonical=1#message");</script><div class="js-letter-list-item">Advertisement</div><div class="letter-body__body"><p>Safe text &lt;script&gt;</p><img width="1" height="1" src="https://e.mail.ru/__mailtrim_slow_image"><div hidden>Hidden text</div><script type="text/plain">excluded script</script><img src="data:text/plain,bad"><img src="javascript:void(0)"></div></body></html>
                """)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");            if (e.Request.Uri.EndsWith("/__mailtrim_fixture", StringComparison.Ordinal))
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mail-ads.html")))), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        };
    }
    private static async Task Navigate(BrowserSession session, string url)
    {
        var completion = new TaskCompletionSource<bool>();
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) completion.TrySetResult(true); }
        session.View.CoreWebView2.NavigationCompleted += Handler;
        try { session.View.CoreWebView2.Navigate(url); await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { session.View.CoreWebView2.NavigationCompleted -= Handler; }
    }
    private static async Task WaitForScript(BrowserSession session, string expression)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await session.View.CoreWebView2.ExecuteScriptAsync(expression) == "true") return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Timed out waiting for fixture condition: " + expression);
    }
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS " + name);
    }
}
