using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.Smoke;

internal static class NativeCloudChecks
{
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        using var first = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        using var second = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        host.Children.Add(first.Surface); host.Children.Add(second.Surface);
        CloudPane? pane = null, other = null;
        var originals = 0; var logins = 0;
        var responses = new List<Task>();
        try
        {
            await first.Initialize(Guid.NewGuid(), background: true);
            await second.Initialize(Guid.NewGuid(), background: true);
            await first.EnsureCloud(surface => host.Children.Add(surface));
            await second.EnsureCloud(surface => host.Children.Add(surface));
            var firstSource = first.CloudView!;
            Fixture(firstSource, "Первый", responses); var secondFixture = Fixture(second.CloudView!, "Второй", responses);
            first.SetActive(true); first.SetCloudOpen(true);
            pane = new CloudPane(first, () => originals++, () => logins++); host.Children.Add(pane); pane.SetActive(true);
            await pane.Start("https://cloud.mail.ru/home/");
            check(pane.Listing is { Ready: true } && pane.Items.Count == 2
                && pane.Items.Any(x => x.IsFolder && x.Name == "Документы")
                && pane.Items.Any(x => !x.IsFolder && x.Name == "Первый отчёт.pdf"),
                "native Cloud renders validated files and folders from its hidden source");
            check(first.CloudSurface!.RequestedVisibility != Visibility.Visible && !first.CloudView!.IsVisible
                && !Descendants(pane).OfType<WebView2>().Any(),
                "native Cloud has no visible website or embedded browser in its own interface");
            check(!pane.Items.Any(x => x.Name.Contains("промо", StringComparison.OrdinalIgnoreCase)) && originals == 0 && logins == 0,
                "unknown advertisements and dialog rows never become native Cloud files or trigger an automatic fallback");
            check(!Descendants(pane).OfType<Image>().Any(x => x.Source is BitmapImage { UriSource: { Scheme: "https" or "http" } }),
                "native Cloud does not render remote website images or advertising artwork");
            await pane.Dispatcher.InvokeAsync(() =>
            {
                pane.UpdateLayout();
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(pane.ActualWidth)), Math.Max(1, (int)Math.Ceiling(pane.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(pane);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(store.Root, "native-cloud.png")); png.Save(output);
            }, System.Windows.Threading.DispatcherPriority.Render);
            var ownedCount = window.OwnedWindows.Count; var hiddenMailRequests = 0;
            first.MailRequested += () => hiddenMailRequests++;
            await firstSource.CoreWebView2.ExecuteScriptAsync("window.open('about:blank','_blank')");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(window.OwnedWindows.Count == ownedCount && originals == 0 && logins == 0,
                "a hidden Cloud source cannot create an unsolicited native popup");
            var stoppedMailRedirect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void MailRedirect(object? sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri == "https://e.mail.ru/inbox/source-auto") stoppedMailRedirect.TrySetResult(e.Cancel); }
            firstSource.CoreWebView2.NavigationStarting += MailRedirect;
            try
            {
                firstSource.CoreWebView2.Navigate("https://e.mail.ru/inbox/source-auto");
                var stopped = await stoppedMailRedirect.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                check(stopped && hiddenMailRequests == 0 && first.CloudSurface!.RequestedVisibility != Visibility.Visible,
                    "a hidden Cloud source cannot switch the user to the Mail website through an automatic redirect");
            }
            finally { firstSource.CoreWebView2.NavigationStarting -= MailRedirect; }

            var list = Descendants(pane).OfType<Control>().Single(x => x.Name == "CloudItems");
            var oldPanel = Application.Current.Resources["Panel"];
            var oldInk = Application.Current.Resources["Ink"];
            try
            {
                var panelColor = Color.FromRgb(22, 27, 38); var inkColor = Color.FromRgb(231, 239, 249);
                Application.Current.Resources["Panel"] = new SolidColorBrush(panelColor);
                Application.Current.Resources["Ink"] = new SolidColorBrush(inkColor);
                await pane.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                check(list.Background is SolidColorBrush background && background.Color == panelColor
                    && list.Foreground is SolidColorBrush foreground && foreground.Color == inkColor,
                    "native Cloud uses the shared application theme without importing website CSS");
            }
            finally { Application.Current.Resources["Panel"] = oldPanel; Application.Current.Resources["Ink"] = oldInk; }

            await pane.OpenFolder("https://cloud.mail.ru/home/Docs/");
            check(pane.Listing is { Ready: true } docs && docs.FolderUrl.EndsWith("/home/Docs/", StringComparison.Ordinal)
                && pane.Items.Single().Name == "Документ.txt", "native Cloud opens folders through official same-profile navigation");
            await pane.GoBack();
            check(pane.Listing is { Ready: true } back && back.FolderUrl == "https://cloud.mail.ru/home/" && pane.Items.Count == 2,
                "native Cloud back navigation restores the parent folder");
            await pane.OpenFolder("https://cloud.mail.ru/home/Actual/");
            var actualFolder = pane.Items.Single(x => x.IsFolder);
            check(pane.Listing is { Ready: true, Partial: true } && actualFolder.Name == "Настоящие атрибуты" && actualFolder.Url == "https://cloud.mail.ru/home/%2FDocs"
                && pane.Items.Any(x => !x.IsFolder && x.Name == "Настоящий файл.txt"),
                "native Cloud recognizes provider data-qa metadata and marks a virtualized listing as partial");
            await pane.OpenFolder(actualFolder.Url);
            check(pane.Listing is { Ready: true } actual && actual.FolderUrl == actualFolder.Url && pane.Items.Single().Name == "Документ.txt",
                "native Cloud opens the provider's encoded object-path folder URLs");
            await pane.GoBack(); await pane.OpenFolder("https://cloud.mail.ru/home/");

            await pane.OpenFolder("https://cloud.mail.ru/home/More/");
            check(pane.Listing is { Ready: true, Partial: true } && pane.Items.Count == 2 && pane.Items.Any(x => x.Name == "Первая.txt"),
                "native Cloud identifies the first viewport of a virtualized provider list as partial");
            await pane.LoadMore();
            check(pane.Items.Count == 4 && new[] { "Первая.txt", "Вторая.txt", "Третья.txt", "Четвёртая.txt" }.All(name => pane.Items.Any(x => x.Name == name))
                && await firstSource.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-qa-type=file]').length === 2 && window.moreReplaced") == "true"
                && originals == 0 && logins == 0 && first.CloudSurface!.RequestedVisibility != Visibility.Visible,
                "native Cloud More merges replaced virtualized rows with earlier models without revealing the source website");
            await pane.LoadMore();
            check(pane.Items.Count == 4 && pane.Listing is { Partial: true },
                "native Cloud More does not duplicate previously loaded files or claim that a partial list is complete");

            var slow = pane.OpenFolder("https://cloud.mail.ru/home/Slow/");
            await Task.Delay(80);
            check(!slow.IsCompleted, "native Cloud delayed-folder fixture is still pending before the next selection");
            var fast = pane.OpenFolder("https://cloud.mail.ru/home/Fast/");
            await fast;
            try { await slow; } catch (OperationCanceledException) { }
            check(pane.Listing is { Ready: true } latest && latest.FolderUrl.EndsWith("/home/Fast/", StringComparison.Ordinal)
                && pane.Items.Single().Name == "Актуальный.txt", "rapid native Cloud folder changes cannot publish an older delayed result");

            pane.SetActive(false); first.SetActive(false); second.SetActive(true); second.SetCloudOpen(true);
            other = new CloudPane(second, () => originals++, () => logins++); host.Children.Add(other); other.SetActive(true);
            await other.Start("https://cloud.mail.ru/home/");
            check(other.Items.Any(x => x.Name == "Второй отчёт.pdf") && !other.Items.Any(x => x.Name == "Первый отчёт.pdf")
                && pane.Items.Single().Name == "Актуальный.txt", "native Cloud account panes keep separate listings and folder selection");
            check(first.CloudSurface!.RequestedVisibility != Visibility.Visible && second.CloudSurface!.RequestedVisibility != Visibility.Visible,
                "switching native Cloud accounts never reveals either website surface");
            check(pane.Visibility == Visibility.Collapsed && other.Visibility == Visibility.Visible,
                "native Cloud switches account panes without showing an inactive account's file list");

            secondFixture.WelcomeRoot = true; await other.Refresh();
            check(other.Listing is { Ready: true, Empty: true } && other.Items.Count == 0,
                "native Cloud accepts the provider's certified loaded empty-root welcome structure without rendering its promotions");
            secondFixture.WelcomeRoot = false; secondFixture.TitleOnly = true; await other.Refresh();
            check(other.Listing is { Ready: false, Empty: false } && other.Items.Count == 0,
                "a welcome title alone never certifies that the user's Cloud is empty");
            secondFixture.TitleOnly = false;

            await other.OpenFolder("https://cloud.mail.ru/home/Empty/");
            check(other.Listing is { Ready: true, Empty: true } && other.Items.Count == 0,
                "native Cloud accepts an explicitly confirmed empty folder");
            await other.OpenFolder("https://cloud.mail.ru/home/Unknown/");
            check(other.Listing is { Ready: false, AuthRequired: false } && other.Items.Count == 0
                && second.CloudSurface!.RequestedVisibility != Visibility.Visible && originals == 0,
                "unknown Cloud markup is an explicit unavailable state rather than a fabricated empty folder or visible original");
            await other.OpenFolder("https://cloud.mail.ru/home/Auth/");
            check(other.Listing is { AuthRequired: true } && other.Items.Count == 0 && originals == 0 && logins == 0
                && second.CloudSurface!.RequestedVisibility != Visibility.Visible,
                "Cloud login requirement remains a native state until the user explicitly opens official authentication");
        }
        finally
        {
            if (pane is not null) { await pane.StopAsync(); pane.Dispose(); host.Children.Remove(pane); }
            if (other is not null) { await other.StopAsync(); other.Dispose(); host.Children.Remove(other); }
            await Task.WhenAll(responses);
            foreach (var session in new[] { first, second })
            {
                host.Children.Remove(session.Surface);
                if (session.CloudSurface is { } surface) host.Children.Remove(surface);
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); var visited = new HashSet<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
            if (current is not Visual && current is not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private sealed class FixtureState { public bool WelcomeRoot { get; set; } public bool TitleOnly { get; set; } }
    private static FixtureState Fixture(WebView2 view, string profile, List<Task> responses)
    {
        var state = new FixtureState();
        var core = view.CoreWebView2;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (_, e) =>
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            responses.Add(finished.Task);
            try
            {
                using var deferral = e.GetDeferral();
                var uri = new Uri(e.Request.Uri);
                if (uri.AbsolutePath == "/home/Slow/") await Task.Delay(1200);
                if (uri.AbsolutePath == "/home/Auth/")
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 302, "Found", "Location: https://account.mail.ru/login/__native_cloud\r\nCache-Control: no-store");
                    return;
                }
                var content = uri.Host == "account.mail.ru" ? "<h1>Официальный вход</h1><form><input type=password><button>Продолжить</button></form>"
                    : uri.AbsolutePath == "/home/" && state.WelcomeRoot ? "<div class='MainPage__root--native'><div data-qa-datalist=home></div><table><tbody><tr><td>Обычная таблица оформления</td><td>Служебный текст</td></tr></tbody></table><div class='HomePage__promo--native'><section class='WelcomeNewPromo__root--native'><h1>Добро пожаловать в Облако!</h1><p>Тарифы и предложения</p></section></div></div>"
                    : uri.AbsolutePath == "/home/" && state.TitleOnly ? "<h1>Добро пожаловать в Облако!</h1><p>Список ещё не распознан</p>"
                    : uri.AbsolutePath == "/home/Unknown/" ? "<h1>Все файлы</h1><div>Неизвестная структура сайта</div>"
                    : uri.AbsolutePath == "/home/Empty/" ? "<h1>Пустая папка</h1><div role=grid data-empty=true><div role=status>В папке пусто</div></div>"
                    : uri.AbsolutePath == "/home/More/" ? MorePage
                    : uri.AbsolutePath == "/home/Actual/" ? ActualPage
                    : uri.AbsolutePath.Equals("/home/%2FDocs", StringComparison.OrdinalIgnoreCase) ? Page("Документы", "/home/%2FDocs", Row("Документ.txt", "/home/%2FDocs%2FDocument.txt", false))
                    : uri.AbsolutePath == "/home/Docs/" ? Page("Документы", "/home/Docs/", Row("Документ.txt", "/home/Docs/Document.txt", false))
                    : uri.AbsolutePath == "/home/Slow/" ? Page("Медленная", "/home/Slow/", Row("Устаревший.txt", "/home/Slow/Old.txt", false))
                    : uri.AbsolutePath == "/home/Fast/" ? Page("Быстрая", "/home/Fast/", Row("Актуальный.txt", "/home/Fast/Latest.txt", false))
                    : Page("Все файлы", "/home/", Row("Документы", "/home/Docs/", true) + Row(profile + " отчёт.pdf", "/home/Report.pdf", false));
                var html = "<!doctype html><html><head><meta charset=utf-8></head><body>" + content + "</body></html>";
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            }
            catch (Exception ex) { finished.TrySetException(ex); }
            finally { finished.TrySetResult(); }
        };
        return state;
    }
    private const string ActualPage = """
        <h1>Все файлы</h1><div data-qa-datalist="home" data-qa-view="list" aria-rowcount="4">
        <a href="#" data-id="/Docs" data-qa-id="/Docs" data-qa-type="folder" data-qa-name="Настоящие атрибуты"><span>Настоящие атрибуты</span></a>
        <a href="#" data-id="/Actual.txt" data-qa-id="/Actual.txt" data-qa-type="file" data-qa-name="Настоящий файл.txt"><span>Настоящий файл.txt</span></a></div>
        """;
    private static string QaRow(string name, string path) => $"<a href='#' data-id='{path}' data-qa-id='{path}' data-qa-type='file' data-qa-name='{name}' style='display:block;height:30px'>{name}</a>";
    private static readonly string MorePage = $$"""
        <h1>Много файлов</h1><div id="more-list" data-qa-datalist="home" data-qa-view="list" aria-rowcount="4" style="height:80px;overflow:auto">
        <div id="more-spacer" style="height:300px">{{QaRow("Первая.txt", "/First.txt")}}{{QaRow("Вторая.txt", "/Second.txt")}}</div></div>
        <script>window.moreReplaced=false;document.getElementById('more-list').addEventListener('scroll',()=>{
          if(window.moreReplaced)return;window.moreReplaced=true;
          document.getElementById('more-spacer').innerHTML={{System.Text.Json.JsonSerializer.Serialize(QaRow("Третья.txt", "/Third.txt") + QaRow("Четвёртая.txt", "/Fourth.txt"))}};
        });</script>
        """;
    private static string Page(string name, string folder, string rows) => $"""
        <h1>{name}</h1><nav aria-label="Путь"><a href="/home/">Все файлы</a><a href="{folder}" aria-current=page>{name}</a></nav>
        <main><div role=grid aria-label="Файлы">{rows}<div class=unknown-promo><h2>Новая неожиданная акция</h2><a href="https://promos.example.test/offer">Подписаться</a></div></div></main>
        <a role=row data-type=folder data-path="/promo-outside/" data-name="Промо вне списка" href="/home/promo-outside/">Промо вне списка</a>
        <div role=dialog><div role=grid>{Row("Промо в окне", "/home/promo-modal/", true)}</div><button>Попробовать</button></div>
        """;
    private static string Row(string name, string url, bool folder) => $"""
        <a role=row data-type="{(folder ? "folder" : "file")}" data-name="{name}" data-path="{url[5..]}" href="{url}">
        <span data-field=name>{name}</span><span data-field=size>{(folder ? "" : "23 КБ")}</span><time data-field=modified datetime="2026-10-27T12:00:00Z">27.10.2026</time></a>
        """;
}
