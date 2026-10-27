using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.Smoke;

internal static class CloudChecks
{
    public static async Task Shell(MainWindow shell, Action<bool, string> check)
    {
        var browserHost = (Grid)shell.FindName("BrowserHost");
        var readerHost = (Grid)shell.FindName("ReaderHost");
        var profiles = (ListBox)shell.FindName("Profiles");
        var button = (Button)shell.FindName("CloudButton");
        var sessions = (Dictionary<Guid, BrowserSession>)typeof(MainWindow).GetField("sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shell)!;
        var session = sessions[((AccountProfile)profiles.SelectedItem).Id];
        await session.EnsureCloud(surface => browserHost.Children.Add(surface));
        Fixture(session.CloudView!);
        var pane = readerHost.Children[0];
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        check(browserHost.Visibility == Visibility.Visible && readerHost.Visibility == Visibility.Collapsed && ReferenceEquals(pane, readerHost.Children[0]) && button.Content.ToString() == "Почта", "Cloud toolbar temporarily hides the same native reader without destroying it");
        await Loaded(session.CloudView!, "document.getElementById('mail-link') !== null");
        var popupCount = shell.OwnedWindows.Count;
        await session.CloudView!.CoreWebView2.ExecuteScriptAsync("document.getElementById('mail-link').click()");
        await Until(() => shell.IsEnabled && !session.CloudOpen && readerHost.Visibility == Visibility.Visible);
        check(ReferenceEquals(pane, readerHost.Children[0]) && shell.OwnedWindows.Count == popupCount,
            "Cloud site's Mail link with a new-window target returns to the same native reader without a separate window");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        profiles.SelectedIndex = 1; await Idle();
        check(readerHost.Visibility == Visibility.Visible && button.Content.ToString() == "Облако", "another account retains its Mail reading mode instead of inheriting Cloud");
        profiles.SelectedIndex = 0; await Idle();
        check(readerHost.Visibility == Visibility.Collapsed && button.Content.ToString() == "Почта", "returning to an account restores its Cloud workspace");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        check(readerHost.Visibility == Visibility.Visible && ((Button)shell.FindName("ReaderButton")).Content.ToString() == "Оригинал", "reading mode also returns after switching accounts while Cloud was open");
        async Task Idle()
        {
            for (var i = 0; i < 400; i++) { await Task.Delay(25); if (shell.IsEnabled) return; }
            throw new TimeoutException("cloud_shell_idle");
        }
    }
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        using var first = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        using var second = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        host.Children.Add(first.Surface); host.Children.Add(second.Surface);
        try
        {
            await first.Initialize(Guid.NewGuid()); first.View.CoreWebView2.Stop();
            await second.Initialize(Guid.NewGuid(), background: true);
            Fixture(first.View); await Navigate(first.View, "https://e.mail.ru/inbox/cloud-test");
            await first.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value='unsent synthetic reply'");
            var cloudLink = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.CloudRequested += url => cloudLink.TrySetResult(url);
            first.View.CoreWebView2.Navigate("https://cloud.mail.ru/home/Linked/");
            check(await cloudLink.Task.WaitAsync(TimeSpan.FromSeconds(10)) == "https://cloud.mail.ru/home/Linked/" && first.View.Source.Host == "e.mail.ru", "Mail Cloud links request the internal Cloud workspace without navigating away from the message");
            var cloudPopup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.CloudRequested += url => cloudPopup.TrySetResult(url);
            await first.View.CoreWebView2.ExecuteScriptAsync("window.open('https://cloud.mail.ru/home/NewWindow/', '_blank')");
            check(await cloudPopup.Task.WaitAsync(TimeSpan.FromSeconds(10)) == "https://cloud.mail.ru/home/NewWindow/", "Mail Cloud links with a new-window target use the internal workspace");
            first.ReaderReplyOpen = true; first.SetReadingOnly(true); first.SetActive(true);
            var cookie = first.View.CoreWebView2.CookieManager.CreateCookie("mailtrim_fake_cloud", "profile-one", ".mail.ru", "/");
            cookie.IsSecure = true; first.View.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            await first.EnsureCloud(surface => host.Children.Add(surface));
            await second.EnsureCloud(surface => host.Children.Add(surface));
            var cloud = first.CloudView!;
            Fixture(cloud); Fixture(second.CloudView!);
            check((await cloud.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.mail.ru/")).Any(c => c.Name == cookie.Name && c.Value == "profile-one"), "Cloud shares only the selected Mail account's session");
            check(!(await second.CloudView!.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.mail.ru/")).Any(c => c.Name == cookie.Name), "Cloud cookies cannot cross account profiles");
            await Navigate(cloud, "https://cloud.mail.ru/home/Test/");
            first.SetCloudOpen(true);
            check(first.Surface.Visibility == Visibility.Collapsed && first.CloudSurface!.Visibility == Visibility.Visible && first.ActiveView == cloud, "Cloud opens inside the application while Mail stays mounted");
            check(await cloud.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.js-banner-wrapper-container')).display !== 'none' && document.querySelector('input[type=file]') !== null") == "true", "Mail cosmetic rules do not change the Cloud file interface");
            check(!cloud.CoreWebView2.Settings.IsPasswordAutosaveEnabled && !cloud.CoreWebView2.Settings.AreHostObjectsAllowed && !cloud.CoreWebView2.Settings.IsWebMessageEnabled, "Cloud keeps authentication and host bridge restrictions");
            first.SetActive(false); second.SetCloudOpen(true); second.SetActive(true);
            check(first.CloudSurface!.Visibility == Visibility.Collapsed && second.CloudSurface!.Visibility == Visibility.Visible, "account switching changes the Cloud surface without mixing profiles");
            second.SetActive(false); first.SetActive(true);
            first.SetCloudOpen(false);
            check(first.Surface.Visibility == Visibility.Visible && first.ReaderReplyOpen && first.ReadingOnly && await first.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value === 'unsent synthetic reply'") == "true", "return from Cloud preserves the mounted reply and reading mode");
            first.SetCloudOpen(true);
            check(cloud.Source.AbsolutePath == "/home/Test/", "returning to Cloud preserves its open folder without reloading");
            var returnMail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.MailRequested += () => returnMail.TrySetResult();
            cloud.CoreWebView2.Navigate("https://e.mail.ru/inbox/");
            await returnMail.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(cloud.Source.Host == "cloud.mail.ru", "Cloud mail links return to the existing Mail view instead of replacing Cloud");
            var ownedCount = window.OwnedWindows.Count;
            var popupMail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.MailRequested += () => popupMail.TrySetResult();
            await cloud.CoreWebView2.ExecuteScriptAsync("document.getElementById('mail-link').click()");
            await popupMail.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(window.OwnedWindows.Count == ownedCount && cloud.Source.AbsolutePath == "/home/Test/"
                && await first.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value === 'unsent synthetic reply'") == "true",
                "Cloud Mail target=_blank returns to the mounted reply without creating a popup or replacing the Cloud folder");
            var blankMail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.MailRequested += () => blankMail.TrySetResult();
            await cloud.CoreWebView2.ExecuteScriptAsync("window.syntheticMailWindow=window.open('about:blank','_blank');window.syntheticMailWindow.location.href='https://e.mail.ru/inbox/'");
            await blankMail.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => window.OwnedWindows.Count == ownedCount);
            check(cloud.Source.AbsolutePath == "/home/Test/"
                && await first.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value === 'unsent synthetic reply'") == "true",
                "Cloud Mail popup created through about:blank is closed after returning to the existing mail workspace");
            var existingWindows = window.OwnedWindows.Cast<Window>().ToHashSet();
            var authRoutedToMail = false;
            first.MailRequested += () => authRoutedToMail = true;
            await cloud.CoreWebView2.ExecuteScriptAsync("window.syntheticAuthWindow=window.open('about:blank','_blank')");
            await Until(() => window.OwnedWindows.Cast<Window>().Any(w => !existingWindows.Contains(w) && w.Content is WebView2 { CoreWebView2: not null }));
            var authWindow = window.OwnedWindows.Cast<Window>().Single(w => !existingWindows.Contains(w));
            try
            {
                var authView = (WebView2)authWindow.Content;
                Fixture(authView);
                await Navigate(authView, "https://account.mail.ru/__mailtrim_cloud_auth");
                await Navigate(authView, "https://e.mail.ru/inbox/auth-complete");
                check(!authRoutedToMail && window.OwnedWindows.Cast<Window>().Contains(authWindow)
                    && await authView.CoreWebView2.ExecuteScriptAsync("window.opener !== null") == "true",
                    "about:blank popup that enters official authentication preserves its opener and subsequent Mail redirect");
                check((await authView.CoreWebView2.CookieManager.GetCookiesAsync("https://e.mail.ru/")).Any(c => c.Name == cookie.Name && c.Value == "profile-one"),
                    "Cloud authentication popup retains the selected account's shared session");
            }
            finally { authWindow.Close(); }
            var externalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cloud.CoreWebView2.NavigationStarting += (_, e) => { if (e.Uri == "https://example.test/") externalStarted.TrySetResult(e.Cancel); };
            cloud.CoreWebView2.Navigate("https://example.test/");
            check(await externalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Cloud cannot automatically navigate to an external origin");
            await first.ClearData();
            check(first.View.Source.AbsoluteUri == "about:blank" && cloud.Source.AbsoluteUri == "about:blank" && !first.CloudOpen, "session clearing leaves both live Mail and Cloud documents before wiping cookies");
            check(!(await cloud.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.mail.ru/")).Any(c => c.Name == cookie.Name), "session clearing removes shared Cloud cookies");
        }
        finally
        {
            foreach (var session in new[] { first, second })
            {
                host.Children.Remove(session.Surface);
                if (session.CloudSurface is { } surface) host.Children.Remove(surface);
            }
        }
    }
    private static void Fixture(WebView2 view)
    {
        view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        view.CoreWebView2.WebResourceRequested += (_, e) =>
        {
            var html = "<html><body><div class='js-banner-wrapper-container'>Cloud files toolbar</div><input id=draft><input type=file multiple><a id='mail-link' href='https://e.mail.ru/inbox/' target='_blank'>Почта</a></body></html>";
            e.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        };
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("cloud_route_condition");
    }
    private static async Task Loaded(WebView2 view, string expression)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await view.CoreWebView2.ExecuteScriptAsync(expression) == "true") return;
            await Task.Delay(25);
        }
        throw new TimeoutException("cloud_fixture_loaded");
    }
    private static async Task Navigate(WebView2 view, string url)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong navigation = 0;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri == url) navigation = e.NavigationId; }
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess && e.NavigationId == navigation) done.TrySetResult(); }
        view.CoreWebView2.NavigationStarting += Starting; view.CoreWebView2.NavigationCompleted += Completed;
        try { view.CoreWebView2.Navigate(url); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { view.CoreWebView2.NavigationStarting -= Starting; view.CoreWebView2.NavigationCompleted -= Completed; }
    }
}
