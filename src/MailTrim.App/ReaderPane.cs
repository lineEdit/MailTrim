using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>Native reading view with a profile-scoped encrypted cache. Mail never becomes executable markup.</summary>
public sealed class ReaderPane : Grid
{
    private readonly BrowserSession session;
    private readonly Action original;
    private readonly ListBox list = new() { BorderThickness = new Thickness(0) };
    private readonly StackPanel body = new() { Margin = new Thickness(26), MaxWidth = 850 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly List<ReaderLetter> letters = [];
    private readonly HashSet<string> knownLetters = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool restoring;
    private bool loading;
    private readonly TextBox searchBox = new() { MaxLength = 256, ToolTip = "Поиск в кэше: отправитель, тема и текст. Enter — найти.", MinWidth = 160 };
    private int scrollOffset;
    private readonly Dictionary<string, List<ReaderBlock>> cache = [];
    private ReaderLetter? selectedLetter;
    private CancellationTokenSource? batch;
    private readonly Button cacheButton = new() { Content = "Кэш всего ящика", Padding = new Thickness(8,4,8,4) };
    public void OpenOriginal()
    {
        batch?.Cancel();
        if (selectedLetter is { } letter && session.View.CoreWebView2.Source != letter.Url)
            session.View.CoreWebView2.Navigate(letter.Url);
        original();
    }
    public ReaderPane(BrowserSession session, Action original)
    {
        this.session = session; this.original = original;
        System.Windows.Automation.AutomationProperties.SetName(searchBox, "Поиск в сохранённых письмах");
        SetResourceReference(BackgroundProperty, "Panel");
        list.SetResourceReference(Control.BackgroundProperty, "Panel"); list.SetResourceReference(Control.ForegroundProperty, "Ink");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
         <StackPanel Margin="6" Width="250">
          <TextBlock FontWeight="SemiBold" TextTrimming="CharacterEllipsis"><TextBlock.Text><MultiBinding StringFormat="{}{0} · {1}"><Binding Path="Sender"/><Binding Path="Date"/></MultiBinding></TextBlock.Text></TextBlock>
          <TextBlock Text="{Binding Subject}" TextWrapping="Wrap" MaxHeight="40" Margin="0,3,0,3"/>
          <TextBlock Text="{Binding Preview}" TextTrimming="CharacterEllipsis" Opacity="0.65"/>
         </StackPanel>
        </DataTemplate>
        """);        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); Children.Add(left);
        var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(actions);
        var refresh = new Button { Content = "↻", ToolTip = "Обновить список", Padding = new Thickness(8,4,8,4) }; actions.Children.Add(refresh);
        refresh.Click += async (_, _) => await LoadList(true);
        var more = new Button { Content = "+", ToolTip = "Ещё письма", Padding = new Thickness(8,4,8,4) }; actions.Children.Add(more);
        more.Click += async (_, _) => await LoadList(false, true);
        actions.Children.Add(cacheButton); cacheButton.Click += async (_, _) => await RequestCache();
        var searchRow = new DockPanel { Margin = new Thickness(4) };
        var find = new Button { Content = "Найти", Padding = new Thickness(6,4,6,4) };
        var resetSearch = new Button { Content = "×", ToolTip = "Сбросить поиск", Padding = new Thickness(6,4,6,4) };
        DockPanel.SetDock(resetSearch, Dock.Right); searchRow.Children.Add(resetSearch);
        DockPanel.SetDock(find, Dock.Right); searchRow.Children.Add(find); searchRow.Children.Add(searchBox);
        tools.Children.Add(searchRow);
        find.Click += async (_, _) => await SearchSaved(searchBox.Text);
        searchBox.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await SearchSaved(searchBox.Text); } };
        resetSearch.Click += async (_, _) => await SearchSaved("");
        var clear = new MenuItem { Header = "Удалить зашифрованный кэш этого ящика" };
        cacheButton.ContextMenu = new ContextMenu(); cacheButton.ContextMenu.Items.Add(clear);
        clear.Click += (_, _) => {
            if (loading) return;
            if (MessageBox.Show(Window.GetWindow(this), "Удалить локальные копии? Письма на сервере останутся.", "Очистка кэша", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { session.Cache.Clear(); cache.Clear(); notice.Text = "Кэш удалён."; } catch { notice.Text = "Не удалось очистить кэш."; }
        };
        notice.FontSize = 11; notice.MaxHeight = 32;
        tools.Children.Add(notice); left.Children.Add(list);        list.SelectionChanged += async (_, _) => { if (!restoring && list.SelectedItem is ReaderLetter letter) await Read(letter); };
        var right = new DockPanel(); SetColumn(right, 1); Children.Add(right);
        var open = new Button { Content = "Ответить / вложения ↗", Padding = new Thickness(8,4,8,4), HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(open, Dock.Top); right.Children.Add(open); open.Click += (_, _) => OpenOriginal();
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddText("Выберите письмо слева. Ответы, ссылки и вложения доступны в оригинале.");
    }
    public async Task SearchSaved(string query)
    {
        if (loading || lifetime.IsCancellationRequested) return;
        query = query.Trim(); searchBox.Text = query;
        if (query.Length == 0)
        {
            restoring = true; list.Items.Clear(); foreach (var letter in letters) list.Items.Add(letter); restoring = false;
            notice.Text = $"Писем: {letters.Count}. Поиск сброшен.";
            return;
        }
        loading = true; list.IsEnabled = false; searchBox.IsEnabled = false;
        notice.Text = "Поиск по сохранённым письмам…";
        try
        {
            var found = await Task.Run(() => session.Cache.Search(query, lifetime.Token), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            foreach (var letter in found) AddLetter(letter);
            var urls = found.Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
            var matches = letters.Where(x => urls.Contains(x.Url) || MessageCache.Matches(x, query)).ToArray();
            restoring = true; list.Items.Clear(); foreach (var letter in matches) list.Items.Add(letter); restoring = false;
            notice.Text = $"Найдено: {matches.Length}. Текст — только из кэша этого ящика.";
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось выполнить поиск по кэшу."; }
        finally { loading = false; list.IsEnabled = true; searchBox.IsEnabled = true; }
    }
    public async Task Start()
    {
        if (session.ReaderState is { } position)
        {
            foreach (var item in position.Letters) AddLetter(item);
            restoring = true;
            list.SelectedItem = letters.FirstOrDefault(x => x.Url == position.SelectedUrl);
            restoring = false;
            if (list.SelectedItem is ReaderLetter selected) await Read(selected);
            if (lifetime.IsCancellationRequested) return;
            UpdateLayout();
            FindScroll(list)?.ScrollToVerticalOffset(position.ListOffset);
            ((ScrollViewer)((DockPanel)Children[1]).Children[1]).ScrollToVerticalOffset(position.BodyOffset);
            notice.Text = $"Писем: {letters.Count}. Позиция восстановлена.";
            return;
        }
        List<ReaderLetter> saved;
        try { saved = await Task.Run(session.Cache.List); }
        catch { notice.Text = "Не удалось открыть кэш. Проверьте доступ к локальной папке."; return; }
        if (lifetime.IsCancellationRequested) return;
        foreach (var letter in saved) AddLetter(letter);
        if (saved.Count > 0) { notice.Text = $"В кэше: {saved.Count}. ↻ — проверить новые письма."; return; }
        await LoadList(false);
    }
    private void AddLetter(ReaderLetter letter)
    {
        if (!NavigationPolicy.IsMail(letter.Url) || !knownLetters.Add(letter.Url)) return;
        letters.Add(letter); list.Items.Add(letter);
    }    private async Task RequestCache()
    {
        if (batch is not null) { batch.Cancel(); return; }
        if (loading) return;
        if (searchBox.Text.Length > 0) await SearchSaved("");
        if (MessageBox.Show(Window.GetWindow(this), "Заранее загрузить весь ящик через Mail.ru?\n\nПриложение обойдёт стандартные и доступные пользовательские папки, включая спам и корзину. Открытие писем может пометить их прочитанными.\n\nТекст и ссылки на картинки сохранятся на этом компьютере в кэше, зашифрованном для вашей учётной записи Windows. Картинки и вложения автоматически не скачиваются. Лимит — 512 МБ на профиль. Существующий кэш будет дополнен.\n\nЗагрузка может занять долгое время. Её можно остановить; сохранённое останется. Начать?", "Кэш всего ящика", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loading = true; list.IsEnabled = false; cacheButton.Content = "Остановить";
        try
        {
            var result = await new ReaderSource(session).CacheMailbox(text => notice.Text = text, AddLetter, batch.Token);
            notice.Text = $"Сохранено: {result.Saved}, ошибок: {result.Failed}.";
            if (!lifetime.IsCancellationRequested)
                MessageBox.Show(Window.GetWindow(this), $"В кэше: {result.Saved} писем. Ошибок чтения: {result.Failed}.\nОбработано папок: {result.Folders}.\nПапок без подтверждённого конца списка: {result.UncertainFolders}.\n\nСайт может не показывать скрытые папки или часть списка. Это кэш доступных сообщений, а не подтверждённая резервная копия всего ящика.", "Загрузка завершена");
        }
        catch (OperationCanceledException) { notice.Text = "Остановлено. Сохранённые письма доступны из кэша."; }
        catch { notice.Text = "Загрузка прервана. Проверьте вход, сеть и место для кэша. Сохранённые письма остались."; }
        finally { batch.Dispose(); batch = null; loading = false; list.IsEnabled = true; cacheButton.Content = "Кэш всего ящика"; }
    }
    public void Close()
    {
        session.ReaderState = new ReaderPosition(letters.ToArray(), selectedLetter?.Url, FindScroll(list)?.VerticalOffset ?? 0,
            ((ScrollViewer)((DockPanel)Children[1]).Children[1]).VerticalOffset);
        lifetime.Cancel(); cache.Clear(); selectedLetter = null; letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); }
    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }    private async Task<T?> Extract<T>(string script) => JsonSerializer.Deserialize<T>(await session.View.CoreWebView2.ExecuteScriptAsync(script), FilterRules.Json);
    private async Task LoadList(bool refresh, bool more = false)
    {
        if (loading || lifetime.IsCancellationRequested) return;
        if (searchBox.Text.Length > 0) await SearchSaved("");
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
            if (refresh) { cache.Clear(); selectedLetter = null; letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); }
            foreach (var letter in found ?? []) AddLetter(letter);
            notice.Text = letters.Count == 0 ? "Откройте список писем в Mail.ru и включите режим снова. Возможно, нужен вход в аккаунт." : $"Загружено: {letters.Count}. Показаны письма с открытой страницы.";
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось прочитать список. Откройте оригинал Mail.ru."; }
        finally { loading = false; list.IsEnabled = true; }
    }
    private async Task Read(ReaderLetter letter)
    {
        if (loading || lifetime.IsCancellationRequested || !NavigationPolicy.IsMail(letter.Url)) return;
        selectedLetter = letter;
        loading = true; list.IsEnabled = false; body.Children.Clear(); AddText("Загрузка письма…");
        try
        {
            List<ReaderBlock>? blocks;
            if (!cache.TryGetValue(letter.Url, out blocks))
            {
                blocks = session.Cache.Get(letter.Url);
                if (blocks is null)
                {
                    blocks = await new ReaderSource(session).Read(letter, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    try { session.Cache.Save(letter, blocks); }
                    catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { notice.Text = "Письмо прочитано, но не сохранено в кэш."; }
                }                if (blocks?.Count > 0 && blocks.Sum(x => x.Text.Length + x.Image.Length) <= 500_000)
                {
                    while (cache.Count >= 12 || cache.Values.Sum(v => v.Sum(x => x.Text.Length + x.Image.Length)) > 500_000)
                        cache.Remove(cache.Keys.First());
                    cache[letter.Url] = blocks;
                }
            }            lifetime.Token.ThrowIfCancellationRequested();
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
 public sealed record ReaderPosition(ReaderLetter[] Letters, string? SelectedUrl, double ListOffset, double BodyOffset);
