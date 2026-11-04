using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailTrim.App;
using MailTrim.Core;
using MailTrim.Core.Gmail;

namespace MailTrim.Smoke;

internal static class GmailSignInViewChecks
{
    public static async Task Run(LocalStore store, Grid host, Action<bool, string> check)
    {
        var registrationRoot = Path.Combine(store.Root, "gmail-sign-in-view");
        var absent = new GmailRegistrationStore(Path.Combine(registrationRoot, "absent"), () => null);
        var bundled = GmailClientRegistration.Create("view-fixture.apps.googleusercontent.com", "GOCSPX-view-fixture");
        var configured = new GmailRegistrationStore(Path.Combine(registrationRoot, "configured"), () => bundled);
        var window = Window.GetWindow(host)!;
        var oldWidth = window.Width; var oldHeight = window.Height;
        try
        {
            window.Width = 1000; window.Height = 700;
            using (var pane = new GmailPane(store, Profile(), (_, _) => { }, _ => { }, registrationStore: absent))
            {
                host.Children.Add(pane);
                try
                {
                    await pane.Ready; await Layout(pane);
                    var signIn = SignIn(pane);
                    check(!pane.BrowserSignInAvailable && signIn.IsVisible && !signIn.IsEnabled
                        && VisibleText(pane).Any(text => text.Contains("В этой сборке вход Google ещё не настроен", StringComparison.Ordinal)),
                        "Gmail without an app registration shows an honest unavailable state and a disabled browser sign-in button");
                    CheckSimpleView(pane, check);
                    Capture(pane, Path.Combine(store.Root, "gmail-sign-in-unconfigured.png"));
                }
                finally { host.Children.Remove(pane); }
            }

            var fresh = Profile();
            using (var pane = new GmailPane(store, fresh, (_, _) => { }, _ => { }, registrationStore: configured))
            {
                host.Children.Add(pane);
                try
                {
                    await pane.Ready; await Layout(pane);
                    check(pane.BrowserSignInAvailable && SignIn(pane).IsEnabled
                        && new GmailVault(store.Root, fresh.Id).Load<GmailCredentials>("credentials") is null,
                        "a new Gmail account offers browser sign-in from shared app registration without importing account files");
                    check(!VisibleText(pane).Any(text => text.Contains("В этой сборке вход Google ещё не настроен", StringComparison.Ordinal)),
                        "configured Gmail sign-in does not show the missing-registration warning");
                    CheckSimpleView(pane, check);
                    Capture(pane, Path.Combine(store.Root, "gmail-sign-in-configured.png"));
                    window.Width = 1500; window.Height = 800; await Layout(pane);
                    Capture(pane, Path.Combine(store.Root, "gmail-sign-in-configured-wide.png"));
                }
                finally { host.Children.Remove(pane); window.Width = 1000; window.Height = 700; }
            }

            var reconnect = Profile();
            var credentials = new GmailCredentials("saved-view-fixture.apps.googleusercontent.com", "saved-secret", "saved-refresh", "saved-access", DateTimeOffset.UtcNow.AddHours(1));
            var saved = new GmailVault(store.Root, reconnect.Id); saved.Save("credentials", credentials);
            using (var pane = new GmailPane(store, reconnect, (_, _) => { }, _ => { }, new EmptyMailbox(), absent))
            {
                host.Children.Add(pane);
                try
                {
                    await pane.Ready; await Layout(pane);
                    Descendants<Button>(pane).Single(button => button.Content?.ToString() == "Аккаунт Google")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Layout(pane);
                    check(pane.Connected && pane.BrowserSignInAvailable && SignIn(pane).IsEnabled && absent.Load() is null,
                        "an existing Gmail account can reconnect in the browser using its saved registration without a shared client or another import");
                    check(saved.Load<GmailCredentials>("credentials") == credentials,
                        "opening Gmail account settings preserves that account's existing grant");
                    CheckSimpleView(pane, check);
                    Capture(pane, Path.Combine(store.Root, "gmail-sign-in-reconnect.png"));
                }
                finally { host.Children.Remove(pane); }
            }

            await FailureViews(store, host, configured, check);
        }
        finally { window.Width = oldWidth; window.Height = oldHeight; host.UpdateLayout(); }
    }

    private static AccountProfile Profile() => new() { Name = "Synthetic Gmail sign-in", Provider = MailProvider.Gmail };
    private static async Task FailureViews(LocalStore store, Grid host, GmailRegistrationStore configured, Action<bool, string> check)
    {
        var unavailable = Profile();
        var vault = new GmailVault(store.Root, unavailable.Id);
        using (var oauth = new GmailOAuth(vault, new NoNetwork(), _ => throw new Win32Exception("synthetic browser start failure")))
        using (var pane = new GmailPane(store, unavailable, (_, _) => { }, _ => { }, registrationStore: configured, testOAuth: oauth))
        {
            host.Children.Add(pane);
            try
            {
                await pane.Ready; await Layout(pane);
                SignIn(pane).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(pane, () => CanRetry(pane)
                    && VisibleText(pane).Any(text => text.Contains("Не удалось открыть браузер", StringComparison.Ordinal)));
                check(CanRetry(pane) && VisibleText(pane).Any(text => text.Contains("Не удалось открыть браузер", StringComparison.Ordinal))
                    && !pane.Connected && vault.Load<GmailCredentials>("credentials") is null,
                    "a browser launch failure remains visible in the account view and re-enables Gmail sign-in without saving a grant");
                CheckSimpleView(pane, check);
                Capture(pane, Path.Combine(store.Root, "gmail-sign-in-browser-failed.png"));
            }
            finally { host.Children.Remove(pane); }
        }

        var cancelled = Profile();
        var cancelledVault = new GmailVault(store.Root, cancelled.Id);
        Uri? opened = null;
        using (var oauth = new GmailOAuth(cancelledVault, new NoNetwork(), uri => opened = uri))
        using (var pane = new GmailPane(store, cancelled, (_, _) => { }, _ => { }, registrationStore: configured, testOAuth: oauth))
        {
            host.Children.Add(pane);
            try
            {
                await pane.Ready; await Layout(pane);
                // Match reconnecting after reading a message: an old selection remains
                // available, but it must not replace the pending sign-in screen.
                var selected = new GmailMessage("switch-fixture", "thread-fixture", "Synthetic sender", "recipient@example.test",
                    "Previously selected message", "", DateTimeOffset.UtcNow, ["INBOX"], "Prepared message text", [], [], "", "");
                typeof(GmailPane).GetField("selected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(pane, selected);
                SignIn(pane).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(pane, () => opened is not null
                    && Descendants<Button>(pane).Any(button => button.IsVisible && button.Content?.ToString() == "Отменить вход"));
                check(opened?.Host == "accounts.google.com" && !CanRetry(pane),
                    "Gmail waiting view offers cancellation while its browser sign-in is in progress");
                pane.SetActive(false); pane.SetActive(true); await Layout(pane);
                check(Descendants<Button>(pane).Any(button => button.IsVisible && button.Content?.ToString() == "Отменить вход")
                    && VisibleText(pane).Any(text => text == "Ожидаю вход в Google")
                    && !VisibleText(pane).Any(text => text == selected.Subject),
                    "returning to a Gmail tab preserves pending sign-in and cancellation instead of rendering the previously selected message");
                Descendants<Button>(pane).Single(button => button.IsVisible && button.Content?.ToString() == "Отменить вход")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(pane, () => CanRetry(pane)
                    && VisibleText(pane).Any(text => text.Contains("отмен", StringComparison.OrdinalIgnoreCase)));
                check(CanRetry(pane) && VisibleText(pane).Any(text => text.Contains("отмен", StringComparison.OrdinalIgnoreCase))
                    && !pane.Connected && cancelledVault.Load<GmailCredentials>("credentials") is null,
                    "cancelling Gmail browser sign-in restores the sign-in button and keeps cancellation feedback visible without saving a grant");
                CheckSimpleView(pane, check);
                Capture(pane, Path.Combine(store.Root, "gmail-sign-in-cancelled.png"));
            }
            finally { host.Children.Remove(pane); }
        }

        var granted = Profile();
        var grantedVault = new GmailVault(store.Root, granted.Id);
        var authorization = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var oauth = new GmailOAuth(grantedVault, new TokenReply(), uri => authorization.TrySetResult(uri)))
        using (var pane = new GmailPane(store, granted, (_, _) => { }, _ => { }, new DeniedMailbox(), configured, oauth))
        {
            host.Children.Add(pane);
            try
            {
                await pane.Ready; await Layout(pane);
                SignIn(pane).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var uri = await authorization.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var fields = uri.Query.TrimStart('?').Split('&').Select(entry => entry.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
                using var browser = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var completion = await browser.GetAsync(fields["redirect_uri"] + "?state=" + Uri.EscapeDataString(fields["state"]) + "&code=ui-fixture");
                await WaitFor(pane, () => CanRetry(pane)
                    && VisibleText(pane).Any(text => text.Contains("Google не разрешил доступ к почте", StringComparison.Ordinal)));
                check(completion.IsSuccessStatusCode && pane.Connected && CanRetry(pane)
                    && grantedVault.Load<GmailCredentials>("credentials")?.RefreshToken == "ui-grant-refresh"
                    && !VisibleText(pane).Any(text => text == "Ожидаю вход в Google"),
                    "a Gmail API failure after successful browser authorization restores account settings with visible feedback and preserves the valid grant");
                CheckSimpleView(pane, check);
                Capture(pane, Path.Combine(store.Root, "gmail-sign-in-after-grant-api-failed.png"));
            }
            finally { host.Children.Remove(pane); }
        }
    }
    private static bool CanRetry(GmailPane pane) => Descendants<Button>(pane).Any(button => button.IsVisible && button.IsEnabled && button.Content?.ToString() == "Войти через браузер");
    private static async Task WaitFor(GmailPane pane, Func<bool> ready)
    {
        for (int attempt = 0; attempt < 150 && !ready(); attempt++) { await Task.Delay(20); await Layout(pane); }
        if (!ready()) throw new InvalidOperationException("Gmail sign-in view did not settle into the expected test state");
        await Layout(pane);
    }
    private static Button SignIn(GmailPane pane) => Descendants<Button>(pane).Single(button => button.Content?.ToString() == "Войти через браузер");
    private static IEnumerable<string> VisibleText(DependencyObject root) => Descendants<TextBlock>(root).Where(text => text.IsVisible).Select(text => text.Text);
    private static void CheckSimpleView(GmailPane pane, Action<bool, string> check)
    {
        var text = VisibleText(pane).ToArray();
        check(!text.Any(value => value.Contains("JSON", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Client ID", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Client secret", StringComparison.OrdinalIgnoreCase))
            && !Descendants<Button>(pane).Any(button => button.IsVisible && button.Content?.ToString() == "Инструкция подключения")
            && Descendants<Expander>(pane).Single().IsExpanded == false,
            "normal Gmail sign-in hides JSON, client fields and setup links behind collapsed advanced settings");
    }
    private static async Task Layout(FrameworkElement element)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }
    private static void Capture(FrameworkElement element, string path)
    {
        Window.GetWindow(element)?.UpdateLayout();
        element.UpdateLayout();
        var signIn = Descendants<Button>(element).Single(button => button.Content?.ToString() == "Войти через браузер");
        if (!signIn.IsVisible || signIn.ActualWidth <= 0 || signIn.ActualHeight <= 0)
            throw new InvalidOperationException("Gmail sign-in capture requires a visible, arranged sign-in button");
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T matching) yield return matching;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class EmptyMailbox : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "gmail.googleapis.com") throw new InvalidOperationException("unexpected_synthetic_gmail_request");
            var path = request.RequestUri.AbsolutePath;
            var json = path.EndsWith("/profile", StringComparison.Ordinal) ? "{\"emailAddress\":\"synthetic@example.test\"}"
                : path.EndsWith("/labels/INBOX", StringComparison.Ordinal) ? "{\"messagesUnread\":0}"
                : path.EndsWith("/labels", StringComparison.Ordinal) ? "{\"labels\":[]}"
                : path.EndsWith("/messages", StringComparison.Ordinal) ? "{\"messages\":[]}"
                : throw new InvalidOperationException("unexpected_synthetic_gmail_path");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("unexpected_network_request_during_gmail_ui_failure_check");
    }
    private sealed class TokenReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://oauth2.googleapis.com/token")
                throw new InvalidOperationException("unexpected_synthetic_token_request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"ui-grant-access\",\"refresh_token\":\"ui-grant-refresh\",\"expires_in\":3600,\"scope\":\"https://www.googleapis.com/auth/gmail.modify\"}")
            });
        }
    }
    private sealed class DeniedMailbox : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "gmail.googleapis.com")
                throw new InvalidOperationException("unexpected_synthetic_denied_mailbox_request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") });
        }
    }
}
