using System.IO;
using System.Net.Http;
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
    private bool readingMessage;
    private long readGeneration;
    private CancellationTokenSource? messageRead;
    private readonly TextBox searchBox = new() { MaxLength = 256, ToolTip = "Поиск в кэше: отправитель, тема и текст. Enter — найти.", MinWidth = 160 };
    private int scrollOffset;
    private readonly Dictionary<string, List<ReaderBlock>> cache = [];
    private ReaderLetter? selectedLetter;
    private readonly Stack<ReaderLetter> history = new();
    private CancellationTokenSource? batch;
    private readonly Button cacheButton = new() { Content = "Кэш всего ящика", Padding = new Thickness(8,4,8,4) };
    private Panel? browserParent;
    private int browserIndex;
    private Visibility browserVisibility;
    private DockPanel? replyPanel;
    public bool IsLoading => loading;
    public bool IsReplyVisible => replyPanel is not null;

    private void AttachReply()
    {
        if (replyPanel is not null) return;
        if (session.View.Parent is not Panel parent) throw new InvalidOperationException("browser_parent_required");
        browserParent = parent; browserIndex = parent.Children.IndexOf(session.View); browserVisibility = session.View.Visibility;
        var panel = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var back = new Button { Content = "← К чтению", HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += async (_, _) => await ReturnToReading(); header.Children.Add(back);
        header.Children.Add(new TextBlock { Text = "Ответ в Mail.ru. Проверьте получателя и отправьте штатной кнопкой сайта. Если редактор не открылся, нажмите «Ответить» ниже.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
        parent.Children.Remove(session.View);
        panel.Children.Add(session.View); session.View.Visibility = Visibility.Visible;
        Children[0].IsEnabled = false; Children[1].Visibility = Visibility.Collapsed;
        SetColumn(panel, 2); Children.Add(panel); replyPanel = panel; session.ReaderReplyOpen = true;
    }
    private void DetachReply()
    {
        if (replyPanel is null) return;
        replyPanel.Children.Remove(session.View);
        browserParent!.Children.Insert(Math.Min(browserIndex, browserParent.Children.Count), session.View);
        session.View.Visibility = browserVisibility;
        Children.Remove(replyPanel); replyPanel = null; browserParent = null;
        Children[0].IsEnabled = true; Children[1].Visibility = Visibility.Visible;
    }
    public async Task ReturnToReading(bool confirmed = false)
    {
        if (replyPanel is null) return;
        if (!confirmed && MessageBox.Show(Window.GetWindow(this), "Перед возвратом отправьте письмо или сохраните черновик в Mail.ru. Вернуться к чтению?", "Ответ в Mail.ru", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        DetachReply(); session.ReaderReplyOpen = false;
        if (selectedLetter is { } letter) await Read(letter);
        notice.Text = "Режим чтения восстановлен. Отправка и сохранение черновика выполняются Mail.ru.";
    }
    private async Task OpenReply(ReaderLetter letter)
    {
        loading = true; list.IsEnabled = false;
        notice.Text = "Открываю ответ рядом со списком писем…";
        try
        {
            await session.ReaderData.PerformAction(letter, ReaderAction.Reply, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            AttachReply();
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось открыть ответ. Проверьте вход в Mail.ru и повторите попытку."; }
        finally { loading = false; list.IsEnabled = true; }
    }
    public void OpenOriginal()
    {
        batch?.Cancel();
        if (!session.ReaderReplyOpen && selectedLetter is { } letter && session.View.CoreWebView2.Source != letter.Url)
            session.View.CoreWebView2.Navigate(letter.Url);
        original();
    }
    public ReaderPane(BrowserSession session, Action original)
    {
        this.session = session; this.original = original;
        session.SetReadingOnly(true);
        System.Windows.Automation.AutomationProperties.SetName(searchBox, "Поиск в сохранённых письмах");
        SetResourceReference(BackgroundProperty, "Panel");
        list.SetResourceReference(Control.BackgroundProperty, "Panel"); list.SetResourceReference(Control.ForegroundProperty, "Ink");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
         <StackPanel Margin="6">
          <TextBlock FontWeight="SemiBold" TextTrimming="CharacterEllipsis"><TextBlock.Text><MultiBinding StringFormat="{}{0} · {1}"><Binding Path="Sender"/><Binding Path="Date"/></MultiBinding></TextBlock.Text></TextBlock>
          <TextBlock Text="{Binding Subject}" TextWrapping="Wrap" MaxHeight="40" Margin="0,3,0,3"/>
          <TextBlock Text="{Binding Preview}" TextTrimming="CharacterEllipsis" Opacity="0.65"/>
         </StackPanel>
        </DataTemplate>
        """);
        list.ItemContainerStyle = new Style(typeof(ListBoxItem)) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(session.ReaderListWidth), MinWidth = 260, MaxWidth = 520 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 300 });
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
            if (loading || IsReplyVisible) return;
            if (MessageBox.Show(Window.GetWindow(this), "Удалить локальные копии? Письма на сервере останутся.", "Очистка кэша", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { session.Cache.Clear(); cache.Clear(); notice.Text = "Кэш удалён."; } catch { notice.Text = "Не удалось очистить кэш."; }
        };
        notice.FontSize = 11; notice.MaxHeight = 72;
        tools.Children.Add(notice); left.Children.Add(list);        list.SelectionChanged += async (_, _) => { if (!restoring && list.SelectedItem is ReaderLetter letter) await Read(letter); };
        var right = new DockPanel(); SetColumn(right, 2); Children.Add(right);
        var messageActions = new WrapPanel();
        DockPanel.SetDock(messageActions, Dock.Top); right.Children.Add(messageActions);
        foreach (var (label, action) in new[] { ("Ответить", ReaderAction.Reply), ("Прочитано", ReaderAction.MarkRead), ("В архив", ReaderAction.Archive), ("Удалить…", ReaderAction.Delete) })
        {
            var button = new Button { Content = label, Padding = new Thickness(8,4,8,4), ToolTip = "Выполнить через официальный интерфейс Mail.ru" };
            messageActions.Children.Add(button);
            button.Click += async (_, _) => await PerformAction(action);
        }
        var open = new Button { Content = "Оригинал / вложения ↗", Padding = new Thickness(8,4,8,4) };
        messageActions.Children.Add(open); open.Click += (_, _) => { if (!loading) OpenOriginal(); };
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddText("Выберите письмо слева. Ответы, ссылки и вложения доступны в оригинале.");
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = true,
            ToolTip = "Изменить ширину списка. Двойной щелчок — стандартная ширина.", Focusable = true };
        splitter.SetResourceReference(BackgroundProperty, "Line"); SetColumn(splitter, 1); Children.Add(splitter);
        System.Windows.Automation.AutomationProperties.SetName(splitter, "Ширина списка писем");
        splitter.DragCompleted += (_, _) => session.SaveReaderListWidth(ColumnDefinitions[0].ActualWidth);
        splitter.KeyUp += (_, _) => session.SaveReaderListWidth(ColumnDefinitions[0].ActualWidth);
        splitter.MouseDoubleClick += (_, _) => { ColumnDefinitions[0].Width = new GridLength(310); session.SaveReaderListWidth(310); };
    }
    public async Task PerformAction(ReaderAction action)
    {
        if (loading || IsReplyVisible || lifetime.IsCancellationRequested) return;
        if (selectedLetter is not { } letter) { notice.Text = "Сначала выберите письмо."; return; }
        if (action == ReaderAction.Reply)
        {
            await OpenReply(letter);
            return;
        }
        if (action == ReaderAction.Delete && MessageBox.Show(Window.GetWindow(this),
            $"Удалить выбранное письмо через Mail.ru?\n\n{letter.Subject}\n\nИз корзины и нестандартных папок удаление доступно только вручную в оригинале.",
            "Удаление письма", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        loading = true; list.IsEnabled = false; notice.Text = "Открываю выбранное письмо в Mail.ru…";
        try
        {
            // Cached reading need not have navigated the browser to the selected letter.
            var result = await session.ReaderData.PerformAction(letter, action, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            // Keep the native reader and its position, including when the site changed.
            notice.Text = result == "clicked"
                ? "Команда передана Mail.ru. Если требуется подтверждение, откройте «Оригинал / вложения ↗». Кэш сохранён."
                : "Кнопка Mail.ru не распознана — действие не подтверждено. Можно открыть «Оригинал / вложения ↗» вручную.";
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Действие не подтверждено. Откройте оригинал и проверьте письмо."; }
        finally { loading = false; list.IsEnabled = true; }
    }
    public void FocusSearch() { if (!IsReplyVisible && searchBox.IsEnabled) { searchBox.Focus(); searchBox.SelectAll(); } }
    public Task RefreshList() => LoadList(true);
    public async Task PreviousLetter()
    {
        if (loading || IsReplyVisible || lifetime.IsCancellationRequested) return;
        if (!history.TryPop(out var previous)) { notice.Text = "Нет предыдущего открытого письма в этом режиме."; return; }
        restoring = true; list.SelectedItem = letters.FirstOrDefault(l => l.Url == previous.Url); restoring = false;
        await Read(previous, false);
    }
    public Task ResetSearch() => SearchSaved("");
    public async Task SearchSaved(string query)
    {
        if (loading || IsReplyVisible || lifetime.IsCancellationRequested) return;
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
            if (!session.ReaderReplyOpen && list.SelectedItem is ReaderLetter selected) await Read(selected);
            if (session.ReaderReplyOpen) { selectedLetter = list.SelectedItem as ReaderLetter; AttachReply(); return; }
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
        if (lifetime.IsCancellationRequested || ReaderData.Clean(letter) is not { } clean || !knownLetters.Add(clean.Url)) return;
        letter = clean;
        letters.Add(letter); list.Items.Add(letter);
    }    private async Task RequestCache()
    {
        if (batch is not null) { batch.Cancel(); return; }
        if (loading || IsReplyVisible) return;
        if (searchBox.Text.Length > 0) await SearchSaved("");
        if (MessageBox.Show(Window.GetWindow(this), "Заранее загрузить весь ящик через Mail.ru?\n\nПриложение обойдёт стандартные и доступные пользовательские папки, включая спам и корзину. Открытие писем может пометить их прочитанными.\n\nТекст и ссылки на картинки сохранятся на этом компьютере в кэше, зашифрованном для вашей учётной записи Windows. Картинки и вложения автоматически не скачиваются. Лимит — 512 МБ на профиль. Существующий кэш будет дополнен.\n\nЗагрузка может занять долгое время. Её можно остановить; сохранённое останется. Начать?", "Кэш всего ящика", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loading = true; list.IsEnabled = false; cacheButton.Content = "Остановить";
        try
        {
            var result = await session.ReaderData.CacheMailbox(text => notice.Text = text, AddLetter, batch.Token);
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
        DetachReply();
        session.ReaderState = new ReaderPosition(letters.ToArray(), selectedLetter?.Url, FindScroll(list)?.VerticalOffset ?? 0,
            ((ScrollViewer)((DockPanel)Children[1]).Children[1]).VerticalOffset);
        readGeneration++; messageRead?.Cancel(); messageRead = null;
        lifetime.Cancel(); history.Clear(); cache.Clear(); selectedLetter = null; letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); }
    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    private async Task LoadList(bool refresh, bool more = false)
    {
        if (loading || IsReplyVisible || lifetime.IsCancellationRequested) return;
        if (searchBox.Text.Length > 0) await SearchSaved("");
        loading = true; list.IsEnabled = false;
        try
        {
            notice.Text = "Загрузка…";
            if (refresh) scrollOffset = 0;
            if (more) scrollOffset += 600;
            var found = await session.ReaderData.LoadList(refresh, more, scrollOffset, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (refresh) { cache.Clear(); selectedLetter = null; letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); }
            foreach (var letter in found ?? []) AddLetter(letter);
            notice.Text = letters.Count == 0 ? "Откройте список писем в Mail.ru и включите режим снова. Возможно, нужен вход в аккаунт." : $"Загружено: {letters.Count}. Показаны письма с открытой страницы.";
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось прочитать список. Откройте оригинал Mail.ru."; }
        finally { loading = false; list.IsEnabled = true; }
    }
    private async Task Read(ReaderLetter letter, bool remember = true)
    {
        if (loading && !readingMessage || IsReplyVisible || lifetime.IsCancellationRequested || !NavigationPolicy.IsMail(letter.Url)) return;
        var generation = ++readGeneration;
        messageRead?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        messageRead = request; var token = request.Token;
        if (remember && selectedLetter is { } previous && previous.Url != letter.Url) history.Push(previous);
        selectedLetter = letter;
        loading = true; readingMessage = true; body.Children.Clear(); AddText("Загрузка письма…");
        try
        {
            List<ReaderBlock>? blocks;
            if (!cache.TryGetValue(letter.Url, out blocks))
            {
                var document = await session.ReaderData.Read(letter, token);
                token.ThrowIfCancellationRequested();
                blocks = document.Blocks.ToList();
                if (!document.Saved) notice.Text = "Письмо прочитано, но не сохранено в кэш.";
                if (blocks?.Count > 0 && blocks.Sum(x => x.Text.Length + x.Image.Length) <= 500_000)
                {
                    while (cache.Count >= 12 || cache.Values.Sum(v => v.Sum(x => x.Text.Length + x.Image.Length)) > 500_000)
                        cache.Remove(cache.Keys.First());
                    cache[letter.Url] = blocks;
                }
            }            token.ThrowIfCancellationRequested();
            if (generation != readGeneration || selectedLetter?.Url != letter.Url) return;
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
        catch { if (generation == readGeneration && !lifetime.IsCancellationRequested) { body.Children.Clear(); AddText("Не удалось прочитать письмо. Откройте оригинал."); } }
        finally { if (generation == readGeneration) { loading = false; readingMessage = false; messageRead = null; } }
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
