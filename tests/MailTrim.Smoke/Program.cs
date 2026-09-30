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
                var paused = false;
                var a = new BrowserSession(store, window, _ => { }, () => paused, _ => { });
                var b = new BrowserSession(store, window, _ => { }, () => paused, _ => { });
                sessions.Add(a); sessions.Add(b); host.Children.Add(a.View); host.Children.Add(b.View);
                var id = Guid.NewGuid();
                await a.Initialize(id); await b.Initialize(Guid.NewGuid());
                a.View.CoreWebView2.Stop(); b.View.CoreWebView2.Stop();
                ConfigureFixture(a); ConfigureFixture(b);
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
                var requestObserved = new TaskCompletionSource<bool>();
                a.View.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri == "https://ad.mail.ru/banner") requestObserved.TrySetResult(e.Response?.StatusCode == 403); };
                await a.View.CoreWebView2.ExecuteScriptAsync("fetch('https://ad.mail.ru/banner').catch(()=>{}); void 0");
                Check(await requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)), "advertising request blocked");
                var cm = a.View.CoreWebView2.CookieManager;
                cm.AddOrUpdateCookie(cm.CreateCookie("mailtrim_test", "isolated", ".mail.ru", "/"));
                Check((await cm.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "cookie in first profile");
                Check(!(await b.View.CoreWebView2.CookieManager.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "second profile isolated");
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
                await Navigate(a, "https://e.mail.ru/__mailtrim_reader");
                var readerRows = System.Text.Json.JsonSerializer.Deserialize<List<ReaderLetter>>(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderScript.List), FilterRules.Json)!;
                Check(readerRows.Count == 1 && readerRows[0].Subject == "Subject", "reader extracts real message anchors only");
                var readerBlocks = System.Text.Json.JsonSerializer.Deserialize<List<ReaderBlock>>(await a.View.CoreWebView2.ExecuteScriptAsync(ReaderScript.Body), FilterRules.Json)!;
                Check(readerBlocks.Any(x => x.Text.Contains("Safe text")) && !readerBlocks.Any(x => x.Text.Contains("excluded script") || x.Text.Contains("Hidden text")), "reader excludes executable and hidden content");
                Check(readerBlocks.All(x => x.Image.Length == 0), "reader rejects data and javascript image URLs");
                var pane = new ReaderPane(a, () => { }); host.Children.Add(pane); a.View.Visibility = Visibility.Hidden; b.View.Visibility = Visibility.Hidden;
                await pane.Start();
                var readerList = ((DockPanel)pane.Children[0]).Children.OfType<ListBox>().Single();
                Check(readerList.Items.Count == 1, "native reader displays extracted list");
                var completedReaderNavigation = false;
                a.View.CoreWebView2.NavigationCompleted += (_, _) => completedReaderNavigation = true;
                readerList.SelectedIndex = 0;
                for (int i = 0; i < 100 && !readerList.IsEnabled; i++) await Task.Delay(100);
                var readerBody = (StackPanel)((ScrollViewer)((DockPanel)pane.Children[1]).Children[1]).Content;
                Check(readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "native reader renders message after canonical URL change with fallback body container");
                Check(!completedReaderNavigation, "reader displays text before slow image finishes loading");
                var readerNavigations = 0;
                a.View.CoreWebView2.NavigationStarting += (_, _) => readerNavigations++;
                readerList.SelectedIndex = -1; readerList.SelectedIndex = 0;
                Check(readerList.IsEnabled && readerNavigations == 0 && readerBody.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Safe text")), "cached letter reopens synchronously without network navigation");
                pane.UpdateLayout();
                var readerBitmap = new RenderTargetBitmap((int)pane.ActualWidth, (int)pane.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                readerBitmap.Render(pane); var readerEncoder = new PngBitmapEncoder(); readerEncoder.Frames.Add(BitmapFrame.Create(readerBitmap));
                using (var capture = File.Create(Path.Combine(root, "reader-preview.png"))) readerEncoder.Save(capture);                pane.Close(); Check(readerList.Items.Count == 0 && readerBody.Children.Count == 0, "reader clears message memory on close");
                var reopenedCache = new MessageCache(root, id);
                Check(reopenedCache.Get(readerRows[0].Url)?.Any(x => x.Text.Contains("Safe text")) == true, "encrypted cache survives a new cache instance");
                var cachedFile = Directory.GetFiles(Path.Combine(root, "ReaderCache", id.ToString("N")), "*.bin").Single();
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(cachedFile)).Contains("Safe text"), "cache does not contain plaintext mail");
                var otherId = Guid.NewGuid(); var otherDir = Path.Combine(root, "ReaderCache", otherId.ToString("N")); Directory.CreateDirectory(otherDir);
                File.Copy(cachedFile, Path.Combine(otherDir, Path.GetFileName(cachedFile)));
                Check(new MessageCache(root, otherId).Get(readerRows[0].Url) is null, "cache cannot be decrypted as a different mailbox profile");
                var resumed = new ReaderPane(a, () => { }); host.Children.Add(resumed); await resumed.Start();
                Check(((DockPanel)resumed.Children[0]).Children.OfType<ListBox>().Single().Items.Count == 1, "reader restores cached list after reopening");
                resumed.Close(); host.Children.Remove(resumed);
                Console.WriteLine("Checking whole-mailbox scan with synthetic folders…");
                var batchResult = await new ReaderSource(a).CacheMailbox(_ => { }, _ => { }, CancellationToken.None);
                Check(batchResult.Folders == 7 && batchResult.Saved == 2 && batchResult.Failed == 0 && batchResult.UncertainFolders == 0, "batch scans default and discovered folders to the end and persists messages");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                bool stopped = false;
                try { await new ReaderSource(a).CacheMailbox(_ => { }, _ => { }, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped && reopenedCache.List().Count == 2, "cancelling batch preserves saved messages");
                await a.ClearData();
                Check(reopenedCache.List().Count == 0, "clearing profile also clears encrypted reader cache");                host.Children.Remove(pane);
                store.Settings.Profiles.Add(new AccountProfile { Name = "Рабочий ящик" });
                var shell = new MainWindow(store) { Width = 1080, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
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
                    Check(readerHost.Visibility == Visibility.Visible && readerHost.Children.Count == 1 && toggleReader.Content.ToString() == "Оригинал", "reader mode survives switching account tab");
                    var activeList = ((DockPanel)((ReaderPane)readerHost.Children[0]).Children[0]).Children.OfType<ListBox>().Single();
                    Check(activeList.Items.Cast<ReaderLetter>().Single().Sender == store.Settings.Profiles[index].Name, "switched reader shows only selected account cache");
                }
                toggleReader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));                // Capture the native WPF shell with the webview hidden, showing only synthetic test UI.
                browserHost.Visibility = Visibility.Hidden;
                shell.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)shell.ActualWidth, (int)shell.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(shell);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(root, "shell-preview.png"))) encoder.Save(file);
                shell.Close();
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

