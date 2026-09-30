using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>Native, memory-only reading view. Untrusted mail never becomes executable markup.</summary>
public sealed class ReaderPane : Grid
{
    private readonly BrowserSession session;
    private readonly Action original;
    private readonly ListBox list = new() { BorderThickness = new Thickness(0) };
    private readonly StackPanel body = new() { Margin = new Thickness(26), MaxWidth = 850 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly List<ReaderLetter> letters = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool loading;
    private int scrollOffset;
    public ReaderPane(BrowserSession session, Action original)
    {
        this.session = session; this.original = original;
        SetResourceReference(BackgroundProperty, "Panel");
        list.SetResourceReference(Control.BackgroundProperty, "Panel"); list.SetResourceReference(Control.ForegroundProperty, "Ink");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); Children.Add(left);
        var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools);
        var refresh = new Button { Content = "Обновить список" }; tools.Children.Add(refresh);
        refresh.Click += async (_, _) => await LoadList(true);
        var more = new Button { Content = "Ещё письма" }; tools.Children.Add(more);
        more.Click += async (_, _) => await LoadList(false, true);
        tools.Children.Add(notice); left.Children.Add(list);
        list.SelectionChanged += async (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: ReaderLetter letter }) await Read(letter); };
        var right = new DockPanel(); SetColumn(right, 1); Children.Add(right);
        var open = new Button { Content = "Открыть в Mail.ru · ответить / вложения", HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(open, Dock.Top); right.Children.Add(open); open.Click += (_, _) => original();
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddText("Выберите письмо слева. Ответы, ссылки и вложения доступны в оригинале.");
    }
    public async Task Start() => await LoadList(false);
    public void Close() { lifetime.Cancel(); letters.Clear(); list.Items.Clear(); body.Children.Clear(); }
    private async Task<T?> Extract<T>(string script) => JsonSerializer.Deserialize<T>(await session.View.CoreWebView2.ExecuteScriptAsync(script), FilterRules.Json);
    private async Task LoadList(bool refresh, bool more = false)
    {
        if (loading || lifetime.IsCancellationRequested) return;
        loading = true; list.IsEnabled = false;
        try
        {
            notice.Text = "Загрузка…";
            if (refresh || more && (await Extract<List<ReaderLetter>>(ReaderScript.List))?.Count is not > 0)
            {
                session.View.CoreWebView2.Navigate("https://e.mail.ru/inbox/");
                await Task.Delay(1500, lifetime.Token);
            }
            if (refresh) scrollOffset = 0;
            if (more)
            {
                scrollOffset += 600;
                await session.View.CoreWebView2.ExecuteScriptAsync($"document.querySelector('.ReactVirtualized__List')?.scrollTo(0,{scrollOffset})");
            }
            List<ReaderLetter>? found = null;
            for (var n = 0; n < 20; n++)
            {
                await Task.Delay(400, lifetime.Token);
                found = await Extract<List<ReaderLetter>>(ReaderScript.List);
                if (found?.Count > 0) break;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (refresh) { letters.Clear(); list.Items.Clear(); body.Children.Clear(); }
            foreach (var letter in found ?? [])
            {
                if (!NavigationPolicy.IsMail(letter.Url) || letters.Any(x => x.Url == letter.Url) || letters.Count >= 500) continue;
                letters.Add(letter);
                var panel = new StackPanel { Margin = new Thickness(8), Width = 266 };
                panel.Children.Add(new TextBlock { Text = letter.Sender + " · " + letter.Date, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                panel.Children.Add(new TextBlock { Text = letter.Subject, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, Margin = new Thickness(0,4,0,4) });
                panel.Children.Add(new TextBlock { Text = letter.Preview, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .65 });
                list.Items.Add(new ListBoxItem { Content = panel, Tag = letter });
            }
            notice.Text = letters.Count == 0 ? "Откройте список писем в Mail.ru и включите режим снова. Возможно, нужен вход в аккаунт." : $"Загружено: {letters.Count}. Показаны письма с открытой страницы.";
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось прочитать список. Откройте оригинал Mail.ru."; }
        finally { loading = false; list.IsEnabled = true; }
    }
    private async Task Read(ReaderLetter letter)
    {
        if (loading || lifetime.IsCancellationRequested || !NavigationPolicy.IsMail(letter.Url)) return;
        loading = true; list.IsEnabled = false; body.Children.Clear(); AddText("Загрузка письма…");
        try
        {
            // Navigate via the official page. Reading can mark a letter as read, just as in Mail.ru.
            var navigated = new TaskCompletionSource<bool>();
            void Completed(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args) => navigated.TrySetResult(args.IsSuccess);
            session.View.CoreWebView2.NavigationCompleted += Completed;
            try
            {
                session.View.CoreWebView2.Navigate(letter.Url);
                if (!await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token)) throw new IOException();
            }
            finally { session.View.CoreWebView2.NavigationCompleted -= Completed; }
            List<ReaderBlock>? blocks = null;
            for (var n = 0; n < 40; n++)
            {
                await Task.Delay(400, lifetime.Token);
                if (session.View.CoreWebView2.Source != letter.Url) continue;
                blocks = await Extract<List<ReaderBlock>>(ReaderScript.Body);
                if (blocks?.Count > 0) break;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            body.Children.Clear(); AddText(letter.Subject, 24); AddText(letter.Sender + " · " + letter.Date, 13);
            if (blocks is null || blocks.Count == 0) AddText("Не удалось извлечь содержимое. Откройте письмо в Mail.ru.");
            foreach (var block in blocks ?? [])
            {
                if (string.IsNullOrEmpty(block.Image)) AddText(block.Text);
                else if (Uri.TryCreate(block.Image, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
                {
                    var button = new Button { Content = "Показать картинку: " + block.Text, Tag = uri, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 650, ToolTip = "Запрос к серверу картинки без cookies. Отправитель может узнать о просмотре." };
                    body.Children.Add(button);
                    button.Click += async (_, _) => await ShowImage(button, uri);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { body.Children.Clear(); AddText("Не удалось прочитать письмо. Откройте оригинал."); }
        finally { loading = false; list.IsEnabled = true; }
    }
    private async Task ShowImage(Button button, Uri uri)
    {
        button.IsEnabled = false;
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8_000_000) throw new IOException();
            await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var data = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, lifetime.Token)) > 0) { if (data.Length + count > 8_000_000) throw new IOException(); data.Write(buffer, 0, count); }
            lifetime.Token.ThrowIfCancellationRequested(); data.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1400; bitmap.StreamSource = data; bitmap.EndInit(); bitmap.Freeze();
            var index = body.Children.IndexOf(button);
            if (index >= 0) { body.Children.RemoveAt(index); body.Children.Insert(index, new Image { Source = bitmap, MaxHeight = 1000, Margin = new Thickness(0,8,0,12) }); }
        }
        catch (OperationCanceledException) { }
        catch { button.Content = "Картинка недоступна — откройте оригинал"; }
    }
    private void AddText(string text, double size = 16) => body.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
}


