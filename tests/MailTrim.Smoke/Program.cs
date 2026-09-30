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
                var store = new LocalStore(root);
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
                await a.ClearData();
                Check(!(await cm.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == "mailtrim_test"), "clear data removes session cookie");
                var sentinel = "private-email@example.org?token=secret";
                store.Log(sentinel); store.Log("test_event");
                Check(!File.ReadAllText(Path.Combine(root, "events.log")).Contains(sentinel), "log rejects sensitive strings");
                var shell = new MainWindow(store) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                shell.Show();
                var browserHost = (Grid)shell.FindName("BrowserHost");
                for (var i = 0; i < 100 && (!shell.IsEnabled || browserHost.Children.Count == 0); i++) await Task.Delay(100);
                Check(shell.IsEnabled && browserHost.Children.Count == 1, "main window initializes with production resources");
                // Capture the native WPF shell with the webview hidden, showing only synthetic test UI.
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
        session.View.CoreWebView2.WebResourceRequested += (_, e) =>
        {
            if (e.Request.Uri.EndsWith("/__mailtrim_fixture", StringComparison.Ordinal))
                e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><head><title>MailTrim test</title></head><body><div data-testid='advertising'>Advertisement</div><main id='message'>Test message</main></body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
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
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS " + name);
    }
}
