using System.Diagnostics;
using System.Windows;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.App;

public sealed class BrowserSession : IDisposable
{
    public WebView2 View { get; } = new();
    private readonly LocalStore store;
    private readonly Window owner;
    private readonly Action<string> status;
    private readonly Func<bool> paused;
    private readonly Action<string>? externalHandler;
    private readonly List<(Window Window, WebView2 View)> popups = [];
    private readonly Dictionary<WebView2, string> scripts = [];
    private CoreWebView2Environment? environment;
    private bool disposed;
    private bool clearing;
    private int blocked;

    public void SetActive(bool active)
    {
        View.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in popups.ToArray()) { if (active) p.Window.Show(); else p.Window.Hide(); }
    }

    public BrowserSession(LocalStore store, Window owner, Action<string> status, Func<bool> paused, Action<string>? externalHandler = null)
        => (this.store, this.owner, this.status, this.paused, this.externalHandler) = (store, owner, status, paused, externalHandler);

    public async Task Initialize(Guid profile)
    {
        environment = await CoreWebView2Environment.CreateAsync(null, store.ProfilePath(profile));
        if (disposed) return;
        await View.EnsureCoreWebView2Async(environment);
        if (disposed) return;
        await Configure(View, false);
        View.CoreWebView2.Navigate("https://e.mail.ru/inbox/");
    }

    private async Task Configure(WebView2 view, bool popup)
    {
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (NavigationPolicy.IsInternal(e.Uri)) { status("Загрузка официальной страницы…"); return; }
            if ((popup || clearing) && e.Uri == "about:blank") return;
            e.Cancel = true;
            // Do not leak an OAuth redirect URL into another browser automatically.
            if (NavigationPolicy.IsExternal(e.Uri) && e.IsUserInitiated) QueueExternal(e.Uri);
            else status("Переход за пределы почты остановлен. Откройте ссылку вручную в браузере.");
        };
        core.NavigationCompleted += (_, e) =>
        {
            status(e.IsSuccess ? $"Готово · заблокировано запросов: {blocked}" : "Страница не загрузилась. Проверьте сеть или временно отключите фильтры.");
            if (!e.IsSuccess) store.Log("navigation_failed");
        };
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.PermissionRequested += async (_, e) =>
        {
            e.SavesInProfile = false;
            e.State = CoreWebView2PermissionState.Deny;
            if (!e.IsUserInitiated || !NavigationPolicy.IsInternal(e.Uri)) return;
            using var deferral = e.GetDeferral();
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (!disposed && MessageBox.Show(owner, "Разрешить официальной странице доступ: " + e.PermissionKind + "?", "Разрешение сайта", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    e.State = CoreWebView2PermissionState.Allow;
            });
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!store.Rules.ShouldBlock(e.Request.Uri, store.Settings.BlockRequests && !paused())) return;
            e.Response = environment!.CreateWebResourceResponse(null, 403, "Blocked by MailTrim", "Cache-Control: no-store");
            blocked++;
            if (blocked == 1 || blocked % 10 == 0) status($"Заблокировано запросов: {blocked}");
        };
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            if (!NavigationPolicy.IsInternal(e.Uri) && e.Uri != "about:blank")
            {
                if (e.IsUserInitiated && NavigationPolicy.IsExternal(e.Uri)) QueueExternal(e.Uri);
                return;
            }
            using var deferral = e.GetDeferral();
            if (popups.Count >= 4 || disposed) return;
            var child = new WebView2();
            var window = new Window { Owner = owner, Title = "MailTrim • окно сайта", Width = 720, Height = 780, Content = child, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            popups.Add((window, child));
            window.Closed += (_, _) => { popups.RemoveAll(p => p.Window == window); scripts.Remove(child); child.Dispose(); };
            try
            {
                window.Show();
                await child.EnsureCoreWebView2Async(environment);
                await Configure(child, true);
                child.CoreWebView2.SourceChanged += (_, _) =>
                {
                    if (Uri.TryCreate(child.CoreWebView2.Source, UriKind.Absolute, out var origin))
                        window.Title = "MailTrim • " + origin.IdnHost;
                };
                child.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
                e.NewWindow = child.CoreWebView2; // Preserve opener + official authentication flow in this profile.
            }
            catch { store.Log("popup_failed"); window.Close(); status("Не удалось открыть окно авторизации."); }
        };
        core.DownloadStarting += async (_, e) =>
        {
            // Use WebView2's download UI; always ask for a path, never auto-open attachments.
            using var deferral = e.GetDeferral();
            e.Cancel = true;
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                var dialog = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileName(e.ResultFilePath), Title = "Сохранить вложение" };
                if (dialog.ShowDialog(owner) == true) { e.ResultFilePath = dialog.FileName; e.Cancel = false; }
            });
        };
        core.ProcessFailed += (_, _) => { store.Log("webview_process_failed"); status("Процесс WebView2 остановился. Перезапустите MailTrim."); };
        await SetScript(view);
        SetTheme(view);
    }

    private async Task SetScript(WebView2 view)
    {
        if (scripts.Remove(view, out var old)) view.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(old);
        scripts[view] = await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript.Create(store.Rules, store.Settings.CosmeticFilters && !paused(), store.Settings.Aggressive));
    }
    private void SetTheme(WebView2 view) => view.CoreWebView2.Profile.PreferredColorScheme = store.Settings.Theme switch
    {
        "Dark" => CoreWebView2PreferredColorScheme.Dark,
        "Light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto
    };
    public async Task ApplySettings()
    {
        foreach (var view in new[] { View }.Concat(popups.Select(p => p.View)).ToArray())
        {
            if (view.CoreWebView2 is null) continue;
            await SetScript(view); SetTheme(view);
            // Reload is explicit in the settings UI: needed to undo both CSS and previously blocked requests.
            view.Reload();
        }
    }
    public async Task ClearData()
    {
        foreach (var p in popups.ToArray()) p.Window.Close();
        View.CoreWebView2.Stop();
        // Navigate away before wiping so the loaded mail page cannot repopulate its session.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        View.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            clearing = true;
            View.CoreWebView2.NavigateToString("<html><body>Clearing session</body></html>");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await View.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        finally { clearing = false; View.CoreWebView2.NavigationCompleted -= Completed; }
    }
    private void QueueExternal(string url) => owner.Dispatcher.BeginInvoke(() => { if (!disposed) OpenExternal(url); });
    private void OpenExternal(string url)
    {
        if (!NavigationPolicy.IsExternal(url)) return;
        if (externalHandler is not null) { externalHandler(url); return; }
        var host = new Uri(url).IdnHost;
        if (MessageBox.Show(owner, $"Открыть внешний сайт {host} в системном браузере?", "Внешняя ссылка", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { store.Log("external_open_failed"); status("Не удалось открыть системный браузер."); }
    }
    public void Dispose()
    {
        disposed = true;
        foreach (var p in popups.ToArray()) p.Window.Close();
        View.Dispose(); scripts.Clear();
    }
}
