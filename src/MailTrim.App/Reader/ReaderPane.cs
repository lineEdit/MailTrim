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
    private readonly StackPanel body = new() { Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly List<ReaderLetter> letters = [];
    private readonly HashSet<string> knownLetters = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool restoring;
    private bool loading;
    private bool working;
    private bool readingMessage;
    private long readGeneration;
    private CancellationTokenSource? messageRead;
    private CancellationTokenSource? listRefresh;
    private bool entryRefreshNeeded = true;
    private bool openingOriginal;
    public Task BackgroundRefresh { get; private set; } = Task.CompletedTask;
    public Task BackgroundMessageRefresh { get; private set; } = Task.CompletedTask;
    private bool liveList;
    private readonly ListBox folders = new() { Name = "ReaderFolders", BorderThickness = new Thickness(0), Margin = new Thickness(4) };
    private readonly DockPanel folderPanel = new();
    private readonly TextBlock folderTitle = new() { Text = "Входящие", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button toggleFolders = new() { Name = "ToggleReaderFolders", Content = "☰", Padding = new Thickness(8, 4, 8, 4) };
    private CancellationTokenSource? folderLoad;
    public Task BackgroundFolderChange { get; private set; } = Task.CompletedTask;
    private string folderUrl = "https://e.mail.ru/inbox/";
    private DateTimeOffset nextReload = DateTimeOffset.UtcNow;
    private bool synchronizing;
    private readonly TextBox searchBox = new() { MaxLength = 256, ToolTip = "Поиск в кэше: отправитель, тема и текст. Enter — найти.", MinWidth = 160 };
    private int scrollOffset;
    private CancellationTokenSource? preparation;
    private bool preparingMessage;
    private bool preparationPaused;
    private readonly Dictionary<string, DateTimeOffset> preparationRetry = [];
    private readonly TextBlock preparationNotice = new() { FontSize = 11, Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };
    public Task BackgroundPreparation { get; private set; } = Task.CompletedTask;
    private ReaderLetter? selectedLetter;
    private readonly Stack<ReaderLetter> history = new();
    private readonly TextBlock cacheNotice = new() { FontSize = 11, Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar cacheProgress = new() { Height = 3, Margin = new Thickness(8, 0, 8, 6), Visibility = Visibility.Collapsed };
    private readonly Button cacheButton = new() { Content = "Кэш…", ToolTip = "Настройки автоматического кэширования", Padding = new Thickness(8,4,8,4) };
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
        Children[0].IsEnabled = false; Children[1].Visibility = Visibility.Collapsed; folderPanel.IsEnabled = false;
        SetColumn(panel, 3); Children.Add(panel); replyPanel = panel; session.ReaderReplyOpen = true;
    }
    private void DetachReply()
    {
        if (replyPanel is null) return;
        replyPanel.Children.Remove(session.View);
        browserParent!.Children.Insert(Math.Min(browserIndex, browserParent.Children.Count), session.View);
        session.View.Visibility = browserVisibility;
        Children.Remove(replyPanel); replyPanel = null; browserParent = null;
        Children[0].IsEnabled = true; Children[1].Visibility = Visibility.Visible; folderPanel.IsEnabled = true;
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
        entryRefreshNeeded = false;
        CancelPreparation();
        listRefresh?.Cancel();
        working = loading = true; list.IsEnabled = false;
        notice.Text = "Открываю ответ рядом со списком писем…";
        try
        {
            await session.ReaderData.PerformAction(letter, ReaderAction.Reply, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            AttachReply();
        }
        catch (OperationCanceledException) { }
        catch { notice.Text = "Не удалось открыть ответ. Проверьте вход в Mail.ru и повторите попытку."; }
        finally { working = loading = false; list.IsEnabled = true; }
    }
    public async Task OpenOriginal()
    {
        if (openingOriginal || lifetime.IsCancellationRequested) return;
        openingOriginal = true; entryRefreshNeeded = false;
        CancelPreparation(); listRefresh?.Cancel(); folderLoad?.Cancel(); messageRead?.Cancel();
        try
        {
            // Cancelled navigation must stop before the visible original starts navigating.
            await session.ReaderData.WaitForIdle(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!session.ReaderReplyOpen && selectedLetter is { } letter && session.View.CoreWebView2.Source != letter.Url)
                session.View.CoreWebView2.Navigate(letter.Url);
            original();
        }
        catch (OperationCanceledException) { }
        catch { if (!lifetime.IsCancellationRequested) notice.Text = "Не удалось открыть оригинал. Повторите попытку."; }
        finally { openingOriginal = false; }
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
          <TextBlock FontWeight="SemiBold" TextTrimming="CharacterEllipsis"><TextBlock.Text><MultiBinding StringFormat="{}{0} · {1}"><Binding Path="Sender"/><Binding Path="DateLabel"/></MultiBinding></TextBlock.Text></TextBlock>
          <TextBlock Text="{Binding Subject}" TextWrapping="Wrap" MaxHeight="40" Margin="0,3,0,3"/>
          <TextBlock Text="{Binding Preview}" TextTrimming="CharacterEllipsis" Opacity="0.65"/>
         </StackPanel>
        </DataTemplate>
        """);
        list.ItemContainerStyle = new Style(typeof(ListBoxItem)) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(164) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(session.ReaderListWidth), MinWidth = 260, MaxWidth = 520 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 300 });
        var left = new DockPanel(); SetColumn(left, 1); Children.Add(left);
        var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools);
        foreach (var (name, path) in new[] { ("Входящие", "inbox"), ("Отправленные", "sent"), ("Черновики", "drafts"), ("Архив", "archive"), ("Спам", "spam"), ("Корзина", "trash") })
            folders.Items.Add(new ReaderFolder(name, "https://e.mail.ru/" + path + "/"));
        folders.SelectedIndex = 0;
        folders.SetResourceReference(Control.BackgroundProperty, "Surface"); folders.SetResourceReference(Control.ForegroundProperty, "Ink");
        ScrollViewer.SetHorizontalScrollBarVisibility(folders, ScrollBarVisibility.Disabled);
        folders.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <DockPanel ToolTip="{Binding Name}" Margin="4,8">
            <TextBlock Text="{Binding Glyph}" FontFamily="Segoe MDL2 Assets" Width="24" VerticalAlignment="Center"/>
            <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
          </DockPanel>
        </DataTemplate>
        """);
        folders.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
          <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
          <Setter Property="Margin" Value="0,2"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
            <Border x:Name="Row" CornerRadius="6" Background="Transparent" BorderThickness="3,0,0,0" BorderBrush="Transparent">
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Panel}"/></Trigger>
              <Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Panel}"/><Setter TargetName="Row" Property="BorderBrush" Value="#3366EB"/><Setter Property="FontWeight" Value="SemiBold"/></Trigger>
              <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="#3366EB"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
        folderPanel.SetResourceReference(BackgroundProperty, "Surface");
        var folderHeading = new DockPanel();
        DockPanel.SetDock(toggleFolders, Dock.Left); folderHeading.Children.Add(toggleFolders);
        var folderLabel = new TextBlock { Text = "ПАПКИ", FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        folderLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); folderHeading.Children.Add(folderLabel);
        DockPanel.SetDock(folderHeading, Dock.Top); folderPanel.Children.Add(folderHeading); folderPanel.Children.Add(folders);
        var heading = new DockPanel(); tools.Children.Add(heading); heading.Children.Add(folderTitle);
        toggleFolders.Click += (_, _) => SetFoldersVisible(folders.Visibility != Visibility.Visible, true);
        System.Windows.Automation.AutomationProperties.SetName(folders, "Папки почты");
        folders.SelectionChanged += async (_, _) => {
            if (restoring || folders.SelectedItem is not ReaderFolder folder || folder.Url.TrimEnd('/') == folderUrl.TrimEnd('/')) return;
            if (IsReplyVisible || working && folderLoad is null) { SelectCurrentFolder(); notice.Text = "Дождитесь завершения операции перед сменой папки."; return; }
            BackgroundFolderChange = ChangeFolder(folder.Url);
            await BackgroundFolderChange;
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(actions);
        var refresh = new Button { Content = "↻", ToolTip = "Обновить список", Padding = new Thickness(8,4,8,4) }; actions.Children.Add(refresh);
        refresh.Click += async (_, _) => await RefreshList();
        var more = new Button { Content = "+", ToolTip = "Ещё письма", Padding = new Thickness(8,4,8,4) }; actions.Children.Add(more);
        more.Click += async (_, _) => await LoadList(false, true);
        actions.Children.Add(cacheButton); cacheButton.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.ShowSettings(true);
        var searchRow = new DockPanel { Margin = new Thickness(4) };
        var find = new Button { Content = "Найти", Padding = new Thickness(6,4,6,4) };
        var resetSearch = new Button { Content = "×", ToolTip = "Сбросить поиск", Padding = new Thickness(6,4,6,4) };
        DockPanel.SetDock(resetSearch, Dock.Right); searchRow.Children.Add(resetSearch);
        DockPanel.SetDock(find, Dock.Right); searchRow.Children.Add(find); searchRow.Children.Add(searchBox);
        tools.Children.Add(searchRow);
        find.Click += async (_, _) => await SearchSaved(searchBox.Text);
        searchBox.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await SearchSaved(searchBox.Text); } };
        resetSearch.Click += async (_, _) => await SearchSaved("");
        cacheNotice.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        tools.Children.Add(cacheNotice); tools.Children.Add(cacheProgress);
        session.Store.CacheProgressChanged += OnCacheProgress;
        if (session.Store.CacheProgress.TryGetValue(session.ProfileId, out var progress)) OnCacheProgress(session.ProfileId, progress);
        notice.FontSize = 11; notice.MaxHeight = 72;
        preparationNotice.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); tools.Children.Add(preparationNotice);
        tools.Children.Add(notice); left.Children.Add(list);        list.SelectionChanged += async (_, _) => { if (!restoring && list.SelectedItem is ReaderLetter letter) await Read(letter); };
        var right = new DockPanel(); SetColumn(right, 3); Children.Add(right);
        var messageActions = new WrapPanel();
        DockPanel.SetDock(messageActions, Dock.Top); right.Children.Add(messageActions);
        foreach (var (label, action) in new[] { ("Ответить", ReaderAction.Reply), ("Прочитано", ReaderAction.MarkRead), ("В архив", ReaderAction.Archive), ("Удалить…", ReaderAction.Delete) })
        {
            var button = new Button { Content = label, Padding = new Thickness(8,4,8,4), ToolTip = "Выполнить через официальный интерфейс Mail.ru" };
            messageActions.Children.Add(button);
            button.Click += async (_, _) => await PerformAction(action);
        }
        var open = new Button { Content = "Оригинал / вложения ↗", Padding = new Thickness(8,4,8,4) };
        messageActions.Children.Add(open); open.Click += async (_, _) => { if (!loading) await OpenOriginal(); };
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddText("Выберите письмо слева. Ответы, ссылки и вложения доступны в оригинале.");
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = true,
            ToolTip = "Изменить ширину списка. Двойной щелчок — стандартная ширина.", Focusable = true };
        splitter.SetResourceReference(BackgroundProperty, "Line"); SetColumn(splitter, 2); Children.Add(splitter);
        Children.Add(folderPanel); SetFoldersVisible(session.ReaderFoldersVisible);
        System.Windows.Automation.AutomationProperties.SetName(splitter, "Ширина списка писем");
        splitter.DragCompleted += (_, _) => session.SaveReaderListWidth(ColumnDefinitions[1].ActualWidth);
        splitter.KeyUp += (_, _) => session.SaveReaderListWidth(ColumnDefinitions[1].ActualWidth);
        splitter.MouseDoubleClick += (_, _) => { ColumnDefinitions[1].Width = new GridLength(310); session.SaveReaderListWidth(310); };
    }
    private void SetFoldersVisible(bool visible, bool save = false)
    {
        // The 40px rail keeps the toggle at a fixed location in both states.
        folders.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ((DockPanel)folderPanel.Children[0]).Children[1].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ColumnDefinitions[0].Width = new GridLength(visible ? 164 : 40);
        toggleFolders.ToolTip = visible ? "Свернуть папки" : "Показать папки";
        System.Windows.Automation.AutomationProperties.SetName(toggleFolders, toggleFolders.ToolTip.ToString());
        if (save) session.SaveReaderFoldersVisible(visible);
    }
    private void SelectCurrentFolder()
    {
        var previous = restoring; restoring = true;
        folders.SelectedItem = folders.Items.Cast<ReaderFolder>().First(x => x.Url.TrimEnd('/') == folderUrl.TrimEnd('/'));
        folderTitle.Text = ((ReaderFolder)folders.SelectedItem).Name; folderTitle.ToolTip = folderTitle.Text;
        restoring = previous;
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
        entryRefreshNeeded = false;
        CancelPreparation();
        listRefresh?.Cancel();
        working = loading = true; list.IsEnabled = false; notice.Text = "Открываю выбранное письмо в Mail.ru…";
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
        finally { working = loading = false; list.IsEnabled = true; }
    }
    public void FocusSearch() { if (!IsReplyVisible && searchBox.IsEnabled) { searchBox.Focus(); searchBox.SelectAll(); } }
    public Task RefreshList() { preparationPaused = false; session.ReaderData.Prepared.Invalidate(letters); return LoadList(true); }
    public async Task OpenInbox()
    {
        if (loading || IsReplyVisible || lifetime.IsCancellationRequested) return;
        restoring = true; folders.SelectedIndex = 0; restoring = false;
        if (folderUrl == "https://e.mail.ru/inbox/") await LoadList(true);
        else await ChangeFolder("https://e.mail.ru/inbox/");
    }
    private async Task ChangeFolder(string url)
    {
        CancelPreparation();
        folderLoad?.Cancel(); working = loading = false;
        preparationRetry.Clear();
        folderUrl = url; liveList = false; entryRefreshNeeded = false;
        SelectCurrentFolder(); searchBox.Text = ""; scrollOffset = 0;
        listRefresh?.Cancel(); readGeneration++; messageRead?.Cancel(); messageRead = null; readingMessage = false; selectedLetter = null; history.Clear();
        letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); AddText("Выберите письмо.");
        await LoadList(true);
    }
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
        CancelPreparation();
        listRefresh?.Cancel();
        if (query.Length == 0)
        {
            restoring = true; list.Items.Clear(); foreach (var letter in letters) list.Items.Add(letter); restoring = false;
            notice.Text = $"Писем: {letters.Count}. Поиск сброшен."; SchedulePreparation();
            return;
        }
        working = loading = true; list.IsEnabled = false; searchBox.IsEnabled = false;
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
        finally { working = loading = false; list.IsEnabled = true; searchBox.IsEnabled = true; }
    }
    public async Task Start()
    {
        _ = LiveLoop();
        if (session.ReaderState is { } position)
        {
            folderUrl = ReaderSource.IsFolder(position.Folder) ? position.Folder : folderUrl;
            if (!folders.Items.Cast<ReaderFolder>().Any(x => x.Url == folderUrl)) folders.Items.Add(new ReaderFolder("Папка Mail.ru", folderUrl));
            SelectCurrentFolder();
            foreach (var item in position.Letters) AddLetter(item);
            SchedulePreparation();
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
            BackgroundRefresh = RefreshOnEntry();
            return;
        }
        List<ReaderLetter> saved;
        try { saved = await Task.Run(session.Cache.List); }
        catch { notice.Text = "Не удалось открыть кэш. Проверьте доступ к локальной папке."; return; }
        if (lifetime.IsCancellationRequested) return;
        foreach (var letter in saved) AddLetter(letter);
        SchedulePreparation();
        if (saved.Count > 0) { notice.Text = $"В кэше: {saved.Count}. Проверяю новые входящие…"; BackgroundRefresh = RefreshOnEntry(); return; }
        await LoadList(true);
    }
    private async Task LiveLoop()
    {
        try { while (!lifetime.IsCancellationRequested) { await Task.Delay(2000, lifetime.Token); await SynchronizeLive(); if (BackgroundPreparation.IsCompleted) SchedulePreparation(); } }
        catch (OperationCanceledException) { }
    }
    public async Task SynchronizeLive(bool reload = false)
    {
        if (synchronizing || preparingMessage || loading || messageRead is not null || IsReplyVisible || openingOriginal || !liveList
            || listRefresh is not null || searchBox.Text.Length > 0 || lifetime.IsCancellationRequested) return;
        synchronizing = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        listRefresh = request;
        try
        {
            List<ReaderLetter>? fresh = null;
            if (reload || DateTimeOffset.UtcNow >= nextReload)
            {
                CancelPreparation();
                fresh = await session.ReaderData.LoadFolder(true, false, 0, folderUrl, request.Token);
                nextReload = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            else if (await session.ReaderData.Snapshot(folderUrl, request.Token) is { Ready: true } snapshot && snapshot.Letters.Count > 0)
                fresh = snapshot.Letters;
            request.Token.ThrowIfCancellationRequested();
            if (fresh is not null)
            {
                // CapturedAt changes on every parse; it alone is not a mail change.
                var changed = fresh.Count == 0 && letters.Count > 0 || fresh.Any(x => !letters.Any(old =>
                    old.Url == x.Url && old.Sender == x.Sender && old.Subject == x.Subject && old.Preview == x.Preview && old.Date == x.Date && old.Unread == x.Unread));
                if (changed) { MergeList(fresh, fresh.Count == 0); session.Cache.SaveHeaders(fresh, request.Token); }
                notice.Text = $"С сайта · {DateTime.Now:HH:mm:ss} · писем: {letters.Count}";
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!lifetime.IsCancellationRequested) { notice.Text = "Связь с Mail.ru недоступна. Показаны последние полученные данные."; nextReload = DateTimeOffset.UtcNow.AddSeconds(30); } }
        finally { if (ReferenceEquals(listRefresh, request)) listRefresh = null; synchronizing = false; }
    }
    private async Task DiscoverFolders(CancellationToken token)
    {
        var found = await session.ReaderData.FolderDetails(token); token.ThrowIfCancellationRequested();
        var previous = restoring; restoring = true;
        try
        {
            foreach (var folder in found)
            {
                var existing = folders.Items.Cast<ReaderFolder>().FirstOrDefault(x => x.Url.TrimEnd('/') == folder.Url.TrimEnd('/'));
                if (existing is null) folders.Items.Add(folder with { Name = folder.Name.Length > 0 ? folder.Name : "Папка " + new Uri(folder.Url).AbsolutePath.Trim('/') });
                else if (folder.Name.Length > 0 && existing.Name != folder.Name && new Uri(folder.Url).AbsolutePath.Trim('/') is not ("inbox" or "sent" or "drafts" or "archive" or "spam" or "trash"))
                    folders.Items[folders.Items.IndexOf(existing)] = folder;
            }
            SelectCurrentFolder();
        }
        finally { restoring = previous; }
    }
    private async Task RefreshOnEntry()
    {
        try
        {
            // Return the cache immediately. Foreground reading has priority over the shared source.
            await Task.Delay(150, lifetime.Token);
            while (!lifetime.IsCancellationRequested && !session.ReaderReplyOpen && entryRefreshNeeded)
            {
                if (loading || messageRead is not null || searchBox.Text.Length > 0) { await Task.Delay(250, lifetime.Token); continue; }
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                listRefresh = request;
                try
                {
                    var found = await session.ReaderData.LoadFolder(true, false, 0, folderUrl, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    MergeList(found, true); liveList = true; entryRefreshNeeded = false; nextReload = DateTimeOffset.UtcNow.AddSeconds(60);
                    notice.Text = $"С сайта · {DateTime.Now:HH:mm:ss} · писем: {letters.Count}";
                    await DiscoverFolders(request.Token);
                    return;
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && request.IsCancellationRequested) { /* Retry after foreground reading, never during a reply. */ }
                finally { if (ReferenceEquals(listRefresh, request)) listRefresh = null; }
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!lifetime.IsCancellationRequested) notice.Text = "Не удалось обновить входящие. Сохранённые письма доступны; ↻ — повторить."; }
    }
    private void MergeList(IEnumerable<ReaderLetter> found, bool replace = false)
    {
        var selectedUrl = (list.SelectedItem as ReaderLetter)?.Url;
        var listScroll = FindScroll(list);
        var offset = listScroll?.VerticalOffset ?? 0;
        var anchorIndex = Math.Clamp((int)offset, 0, Math.Max(0, list.Items.Count - 1));
        var anchorUrl = list.Items.Count > 0 ? (list.Items[anchorIndex] as ReaderLetter)?.Url : null;
        var merged = replace ? new Dictionary<string, ReaderLetter>(StringComparer.Ordinal) : letters.ToDictionary(x => x.Url, StringComparer.Ordinal);
        foreach (var raw in found)
            if (ReaderData.Clean(raw) is { } letter)
                merged[letter.Url] = merged.TryGetValue(letter.Url, out var old) ? MessageCache.MergeMetadata(old, letter) : letter;
        restoring = true;
        try
        {
            letters.Clear(); letters.AddRange(ReaderChronology.NewestFirst(merged.Values));
            knownLetters.Clear(); list.Items.Clear();
            foreach (var letter in letters) { knownLetters.Add(letter.Url); list.Items.Add(letter); }
            list.SelectedItem = letters.FirstOrDefault(x => x.Url == selectedUrl);
            if (selectedLetter is not null && merged.TryGetValue(selectedLetter.Url, out var updated)) selectedLetter = updated;
            UpdateLayout();
            var newAnchor = letters.FindIndex(x => x.Url == anchorUrl);
            listScroll?.ScrollToVerticalOffset(newAnchor >= 0 ? newAnchor + offset - anchorIndex : offset);
        }
        finally { restoring = false; }
        // Do not rebuild the body: retain scroll, manually loaded images and the reading position.
        SchedulePreparation();
    }
    private void CancelPreparation() { preparation?.Cancel(); }
    private void UpdatePreparationNotice()
    {
        var count = letters.Take(80).Count(letter => session.ReaderData.Prepared.Get(letter) is not null);
        preparationNotice.Text = $"Готово к чтению: {count}/{Math.Min(80, letters.Count)}" + (preparingMessage ? " · подготовка…" : "");
        preparationNotice.ToolTip = "Готовые данные хранятся в памяти этого ящика. Через сайт заранее открываются только подтверждённо прочитанные письма. Непрочитанные и письма с неизвестным статусом не открываются.";
    }
    private void SchedulePreparation()
    {
        if (preparationPaused || lifetime.IsCancellationRequested || openingOriginal || IsReplyVisible || searchBox.Text.Length > 0) return;
        CancelPreparation();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); preparation = request;
        preparingMessage = false;
        BackgroundPreparation = PrepareMessages(letters.Take(80).ToArray(), folderUrl, request);
    }
    private async Task PrepareMessages(ReaderLetter[] rows, string folder, CancellationTokenSource request)
    {
        try
        {
            await session.ReaderData.HydratePrepared(rows, request.Token);
            request.Token.ThrowIfCancellationRequested(); UpdatePreparationNotice();
            // Give foreground selection priority. Never submit an entire batch to the source queue.
            await Task.Delay(500, request.Token);
            foreach (var letter in rows.Where(x => x.Unread == false).Take(20))
            {
                request.Token.ThrowIfCancellationRequested();
                if (folder != folderUrl || !liveList || IsReplyVisible || openingOriginal || searchBox.Text.Length > 0) return;
                while (working || messageRead is not null || listRefresh is not null || synchronizing)
                    await Task.Delay(200, request.Token);
                if (DateTimeOffset.UtcNow >= nextReload) return;
                if (session.ReaderData.Prepared.Get(letter)?.IsFresh(DateTimeOffset.UtcNow) == true
                    || preparationRetry.TryGetValue(letter.Url, out var retry) && retry > DateTimeOffset.UtcNow) continue;
                preparingMessage = true; UpdatePreparationNotice();
                using var readRequest = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                readRequest.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var document = await session.ReaderData.ReadFresh(letter, readRequest.Token);
                    request.Token.ThrowIfCancellationRequested();
                    session.ReaderData.Prepared.Put(document, DateTimeOffset.UtcNow);
                    if (document.Cached) preparationRetry[letter.Url] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                catch (OperationCanceledException) when (!request.IsCancellationRequested) { preparationRetry[letter.Url] = DateTimeOffset.UtcNow.AddMinutes(1); }
                catch (OperationCanceledException) { throw; }
                catch { preparationRetry[letter.Url] = DateTimeOffset.UtcNow.AddMinutes(1); }
                finally { if (ReferenceEquals(preparation, request)) preparingMessage = false; }
                request.Token.ThrowIfCancellationRequested(); UpdatePreparationNotice();
                await Task.Delay(350, request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* Preparation is optional: foreground reading and the last good data remain usable. */ }
        finally
        {
            if (ReferenceEquals(preparation, request)) { preparation = null; preparingMessage = false; if (!lifetime.IsCancellationRequested) UpdatePreparationNotice(); }
            request.Dispose();
        }
    }
    private void AddLetter(ReaderLetter letter)
    {
        if (lifetime.IsCancellationRequested || ReaderData.Clean(letter) is not { } clean || !knownLetters.Add(clean.Url)) return;
        letter = clean;
        var index = letters.BinarySearch(letter, Comparer<ReaderLetter>.Create(ReaderChronology.Compare));
        if (index < 0) index = ~index;
        letters.Insert(index, letter);
        list.Items.Insert(index, letter);
    }
    private void OnCacheProgress(Guid profile, MailboxCacheProgress progress)
    {
        if (profile != session.ProfileId || lifetime.IsCancellationRequested) return;
        cacheNotice.Text = progress.Description;
        cacheProgress.Visibility = progress.Running ? Visibility.Visible : Visibility.Collapsed;
        cacheProgress.IsIndeterminate = progress.Indeterminate; cacheProgress.Value = progress.Percent;
    }
    public void Close()
    {
        session.Store.CacheProgressChanged -= OnCacheProgress;
        CancelPreparation(); listRefresh?.Cancel(); folderLoad?.Cancel();
        DetachReply();
        session.ReaderState = new ReaderPosition(letters.ToArray(), selectedLetter?.Url, FindScroll(list)?.VerticalOffset ?? 0,
            ((ScrollViewer)((DockPanel)Children[1]).Children[1]).VerticalOffset, folderUrl);
        readGeneration++; messageRead?.Cancel(); messageRead = null;
        lifetime.Cancel(); history.Clear(); preparationRetry.Clear(); selectedLetter = null; letters.Clear(); knownLetters.Clear(); list.Items.Clear(); body.Children.Clear(); }
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
        CancelPreparation();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        folderLoad = request;
        var requestedFolder = folderUrl;
        working = loading = true; list.IsEnabled = false;
        try
        {
            notice.Text = $"{folderTitle.Text} · загрузка…";
            entryRefreshNeeded = false;
            listRefresh?.Cancel();
            if (refresh) scrollOffset = 0;
            if (more) scrollOffset += 600;
            var found = await session.ReaderData.LoadFolder(refresh, more, scrollOffset, requestedFolder, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (refresh) MergeList(found, true);
            else foreach (var letter in found) AddLetter(letter);
            liveList = true; nextReload = DateTimeOffset.UtcNow.AddSeconds(60);
            notice.Text = $"С сайта · {DateTime.Now:HH:mm:ss} · писем: {letters.Count}";
            await DiscoverFolders(request.Token);
        }
        catch (OperationCanceledException) { }
        catch { if (!request.IsCancellationRequested) notice.Text = "Не удалось прочитать список. Откройте оригинал Mail.ru."; }
        finally { if (ReferenceEquals(folderLoad, request)) { folderLoad = null; working = loading = false; list.IsEnabled = true; SchedulePreparation(); } }
    }
    private Task Read(ReaderLetter letter, bool remember = true)
    {
        if (working || loading && !readingMessage || IsReplyVisible || lifetime.IsCancellationRequested || !NavigationPolicy.IsMail(letter.Url)) return Task.CompletedTask;
        var generation = ++readGeneration;
        CancelPreparation();
        listRefresh?.Cancel();
        messageRead?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        messageRead = request;
        if (remember && selectedLetter is { } previous && previous.Url != letter.Url) history.Push(previous);
        selectedLetter = letter;
        var prepared = session.ReaderData.Prepared.Get(letter);
        List<ReaderBlock>? preview = prepared?.Blocks.ToList();
        if (preview is null) { try { preview = session.Cache.Get(letter.Url); } catch { } }
        if (prepared?.IsFresh(DateTimeOffset.UtcNow) == true)
        {
            Render(letter, prepared.Blocks.ToList()); notice.Text = "Готовое письмо · проверено " + prepared.VerifiedAt!.Value.LocalDateTime.ToString("HH:mm:ss");
            request.Dispose(); messageRead = null; readingMessage = false; loading = false;
            BackgroundMessageRefresh = Task.CompletedTask; SchedulePreparation(); return Task.CompletedTask;
        }
        readingMessage = true; loading = preview is null;
        if (preview is not null) { Render(letter, preview); notice.Text = "Сохранённая копия · проверяю письмо на сайте…"; }
        else { body.Children.Clear(); AddText("Получаю письмо с Mail.ru…"); }
        if (preview is not null && searchBox.Text.Length > 0)
        { notice.Text = "Сохранённое письмо · результат поиска в кэше."; request.Dispose(); messageRead = null; readingMessage = false; return Task.CompletedTask; }
        BackgroundMessageRefresh = RefreshMessage(letter, generation, request, preview);
        return preview is not null ? Task.CompletedTask : BackgroundMessageRefresh;
    }
    private async Task RefreshMessage(ReaderLetter letter, long generation, CancellationTokenSource request, List<ReaderBlock>? preview)
    {
        var token = request.Token;
        try
        {
            var document = await session.ReaderData.ReadFresh(letter, token);
            token.ThrowIfCancellationRequested();
            if (generation != readGeneration || selectedLetter?.Url != letter.Url) return;
            var blocks = document.Blocks.ToList();
            notice.Text = document.Cached ? "Сайт недоступен · показана сохранённая копия письма."
                : document.Saved ? "Письмо получено с сайта · " + DateTime.Now.ToString("HH:mm:ss") : "Письмо получено с сайта; сохранить копию не удалось.";
            session.ReaderData.Prepared.Put(document, DateTimeOffset.UtcNow); UpdatePreparationNotice();
            // Equal data leaves manually loaded images and reading position untouched.
            if (preview is null || !preview.SequenceEqual(blocks)) Render(selectedLetter, blocks);
        }
        catch (OperationCanceledException) { }
        catch { if (generation == readGeneration && !lifetime.IsCancellationRequested) { if (preview is null) { body.Children.Clear(); AddText("Не удалось прочитать письмо. Откройте оригинал."); } else notice.Text = "Не удалось проверить письмо · показана сохранённая копия."; } }
        finally { request.Dispose(); if (generation == readGeneration) { loading = working; readingMessage = false; messageRead = null; SchedulePreparation(); } }
    }
    private void Render(ReaderLetter letter, List<ReaderBlock> blocks)
    {
            body.Children.Clear(); AddText(letter.Subject, 24); AddText(letter.Sender + " · " + letter.DateLabel, 13);
            if (blocks.Count == 0) AddText("Не удалось извлечь содержимое. Откройте письмо в Mail.ru.");
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
 public sealed record ReaderPosition(ReaderLetter[] Letters, string? SelectedUrl, double ListOffset, double BodyOffset, string Folder = "https://e.mail.ru/inbox/");
 public sealed record ReaderFolder(string Name, string Url)
 {
     public string Glyph => new Uri(Url).AbsolutePath.Trim('/') switch
     {
         "inbox" => "\uE715", "sent" => "\uE724", "drafts" => "\uE70F", "archive" => "\uE7B8",
         "spam" => "\uE7BA", "trash" => "\uE74D", _ => "\uE8B7"
     };
 }
