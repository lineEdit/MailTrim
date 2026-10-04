using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.App;

// Used by release verification only. Never opens normal settings, mail profiles or the network.
internal static class PackageLaunchProbe
{
    public static async Task<int> Run(string directory)
    {
        string root;
        try
        {
            root = Path.GetFullPath(directory);
            if (Directory.Exists(root) || File.Exists(root)) return 2;
            Directory.CreateDirectory(root);
        }
        catch { return 2; }
        var report = Path.Combine(root, "package-launch.txt");
        Window? window = null;
        BrowserSession? session = null;
        try
        {
            var store = new LocalStore(root);
            var shell = new MainWindow(store);
            shell.AllowConfirmedShutdown(); shell.Close();
            window = new Window { Width = 640, Height = 480, Left = -10000, Top = -10000, ShowInTaskbar = false, Title = "MailTrim package check" };
            session = new BrowserSession(store, window, _ => { }, () => false);
            var view = session.View;
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) { loaded.TrySetException(e.InitializationException); return; }
                var core = view.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, request) =>
                {
                    var html = "<!doctype html><html><body><p id='package-check'>MailTrim</p></body></html>";
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                core.NavigationCompleted += (_, navigation) => loaded.TrySetResult(navigation.IsSuccess);
            };
            window.Content = view; window.Show();
            await session.Initialize(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(30));
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new IOException("Fixture navigation failed");
            var text = await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('package-check')?.textContent");
            if (JsonSerializer.Deserialize<string>(text) != "MailTrim") throw new IOException("Fixture not rendered");
            File.WriteAllText(report, "PASS " + typeof(App).Assembly.GetName().Version + " WPF WebView2");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, "FAIL " + ex.GetType().Name); return 1; }
        finally { session?.Dispose(); window?.Close(); }
    }
}
