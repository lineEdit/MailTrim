using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MailTrim.Core;
using MailTrim.Core.Gmail;
using Microsoft.Win32;

namespace MailTrim.App;

/// <summary>Only API structures reach this view. No Gmail HTML, scripts or WebView session.</summary>
public sealed class GmailPane : Grid, IDisposable
{
    private readonly LocalStore store;
    private readonly AccountProfile profile;
    private readonly Action<Guid, MailboxStatus> status;
    private readonly Action<int> notify;
    private readonly GmailVault vault;
    private readonly GmailOAuth oauth;
    private readonly GmailRegistrationStore registration;
    private readonly GmailClient api;
    private readonly ReaderImageLoader images = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operation = new(1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly ListBox folders = new() { Name = "GmailFolders" };
    private readonly ListBox messages = new() { Name = "GmailMessages" };
    private readonly TextBox search = new() { Name = "GmailSearch", MinWidth = 60, Margin = new Thickness(0,0,4,0), Tag = "Поиск в Gmail", ToolTip = "Поиск Gmail, например is:unread или from:адрес" };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10,8,10,10), FontSize = 11 };
    private readonly StackPanel body = new() { Name = "GmailReadingBody", Margin = new Thickness(24,24,24,24), MaxWidth = GmailReadingView.MaxContentWidth, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly WrapPanel actions = new() { Margin = new Thickness(8,5,8,5) };
    private readonly DockPanel sidebar = new();
    private readonly DockPanel list = new();
    private readonly DockPanel content = new();
    private readonly GridSplitter splitter = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
    private readonly Button more = new() { Content = "Показать ещё", Margin = new Thickness(10,4,10,4) };
    private readonly TextBlock folderTitle = new() { Name = "GmailFolderTitle", Text = "Входящие", FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock accountCaption = new() { Name = "GmailAccountCaption", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,4,0,0) };
    private readonly ScrollViewer messageScroll = new() { Name = "GmailMessageScroll", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private bool customListWidth;
    public static readonly DependencyProperty FoldersCollapsedProperty = DependencyProperty.Register(nameof(FoldersCollapsed),typeof(bool),typeof(GmailPane),new PropertyMetadata(false,(owner,_) => ((GmailPane)owner).ApplyFolderLayout()));
    public bool FoldersCollapsed { get => (bool)GetValue(FoldersCollapsedProperty); private set => SetValue(FoldersCollapsedProperty,value); }
    public void ToggleFolders() => FoldersCollapsed = !FoldersCollapsed;
    private readonly Dictionary<string, GmailMessage> prepared = [];
    private readonly Dictionary<string, GmailCachedPage> pages = [];
    private string address = "";
    private string label = "INBOX";
    private string query = "";
    private string? next;
    private string? inboxHead;
    private GmailMessage? selected;
    private bool selecting;
    private bool disposed;
    private bool active;
    private bool showingAccount;
    private CancellationTokenSource? imageRender;
    private CancellationTokenSource? connecting;
    private CancellationTokenSource? preparing;
    private Task preparation = Task.CompletedTask;
    public bool HasDraft { get; private set; }
    public bool Connected => oauth.Connected;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public GmailPane(LocalStore store, AccountProfile profile, Action<Guid, MailboxStatus> status, Action<int> notify, System.Net.Http.HttpMessageHandler? testHandler = null, GmailRegistrationStore? registrationStore = null, GmailOAuth? testOAuth = null)
    {
        this.store = store; this.profile = profile; this.status = status; this.notify = notify;
        vault = new(store.Root, profile.Id); oauth = testOAuth ?? new(vault); api = new(oauth.AccessToken,testHandler);
        registration = registrationStore ?? new(store.Root);
        BuildMailboxLayout();
        folders.SelectionChanged += async (_,_) => { if (!selecting && folders.SelectedItem is GmailLabel folder) await Execute(() => ChangeFolder(folder.Id,"")); };
        messages.SelectionChanged += async (_,_) =>
        {
            if (selecting || messages.SelectedItem is not GmailMessage message) return;
            if (HasDraft)
            {
                note.Text = "Сначала отправьте или отмените открытый черновик."; selecting = true;
                try { messages.SelectedItem = ((GmailMessage[]?)messages.ItemsSource ?? []).FirstOrDefault(m => m.Id == selected?.Id); }
                finally { selecting = false; } return;
            }
            await Execute(() => Open(message));
        };
        search.KeyDown += async (_,e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Execute(() => ChangeFolder(label,search.Text.Trim())); } };
        var cache = vault.Load<GmailCache>("cache");
        if (cache is not null)
        {
            address = cache.Address;
            foreach (var page in cache.Pages.Take(40)) pages[page.Label] = page;
            foreach (var message in cache.Bodies.Take(100)) prepared[message.Id] = message;
        }
        timer.Tick += async (_,_) => await Execute(Poll, background: true); timer.Start();
        Ready = Execute(Initialize);
    }
    private Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, ToolTip = title };
        button.SetResourceReference(StyleProperty,"NativeToolbarButton");
        button.Click += async (_,_) => await Execute(action); return button;
    }
    private void BuildMailboxLayout()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MailTrim;component/Gmail/GmailStyles.xaml",UriKind.Relative) });
        SetResourceReference(BackgroundProperty,"Surface");
        ColumnDefinitions.Add(new() { Width = new GridLength(196) });
        ColumnDefinitions.Add(new() { Width = new GridLength(340), MinWidth = 240, MaxWidth = 480 });
        ColumnDefinitions.Add(new() { Width = new GridLength(5) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star), MinWidth = 260 });
        sidebar.SetResourceReference(BackgroundProperty,"Surface"); Children.Add(sidebar);
        var sidebarHeader = new Grid { Height = 48 };
        var toggle = Button("\uE700", () => { ToggleFolders(); return Task.CompletedTask; });
        toggle.Name = "FolderToggleButton"; toggle.FontFamily = new FontFamily("Segoe MDL2 Assets"); toggle.FontSize = 16;
        toggle.Width = toggle.Height = 34; toggle.Margin = new Thickness(7,7,0,7); toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.ToolTip = "Свернуть или развернуть папки"; AutomationProperties.SetName(toggle,"Свернуть или развернуть папки");
        sidebarHeader.Children.Add(toggle);
        var brand = new TextBlock { Text = "Gmail", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(52,0,8,0), VerticalAlignment = VerticalAlignment.Center };
        brand.SetResourceReference(TextBlock.ForegroundProperty,"Ink");
        var brandVisibility = new Style(typeof(TextBlock)); var hideBrand = new DataTrigger { Binding = new Binding(nameof(FoldersCollapsed)) { Source = this }, Value = true };
        hideBrand.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed)); brandVisibility.Triggers.Add(hideBrand); brand.Style = brandVisibility;
        sidebarHeader.Children.Add(brand); DockPanel.SetDock(sidebarHeader,Dock.Top); sidebar.Children.Add(sidebarHeader);
        var compose = SideButton("Написать","\uE70F", () => { Compose(null); return Task.CompletedTask; }); compose.Name = "GmailComposeButton";
        compose.Height = 40; compose.FontWeight = FontWeights.SemiBold; compose.SetResourceReference(BackgroundProperty,"ServiceSelected"); compose.SetResourceReference(Control.ForegroundProperty,"Accent");
        AutomationProperties.SetName(compose,"Написать письмо"); DockPanel.SetDock(compose,Dock.Top); sidebar.Children.Add(compose);
        var bottom = new StackPanel { Margin = new Thickness(0,8,0,8) }; DockPanel.SetDock(bottom,Dock.Bottom); sidebar.Children.Add(bottom);
        bottom.Children.Add(SideButton("Настройки","\uE713", () => { (Window.GetWindow(this) as MainWindow)?.ShowSettings(); return Task.CompletedTask; }));
        bottom.Children.Add(SideButton("Аккаунт Google","\uE77B", () => { if (!HasDraft) ShowSetup(); else note.Text = "Сначала отправьте или отмените открытый черновик."; return Task.CompletedTask; }));
        folders.SetResourceReference(StyleProperty,"NativeList"); folders.SetResourceReference(BackgroundProperty,"Surface");
        folders.ItemTemplate = (DataTemplate)Resources["GmailFolderTemplate"]; folders.ItemContainerStyle = (Style)Resources["GmailFolderItem"];
        folders.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)Resources["GmailFolderGroupTemplate"] });
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(folders,true); sidebar.Children.Add(folders);
        messages.SetResourceReference(StyleProperty,"NativeList"); messages.ItemTemplate = (DataTemplate)Resources["GmailMessageTemplate"]; messages.ItemContainerStyle = (Style)Resources["GmailMessageItem"];
        list.SetResourceReference(BackgroundProperty,"Panel"); SetColumn(list,1); Children.Add(list);
        var listHeader = new DockPanel { Margin = new Thickness(13,14,10,10) };
        var refresh = Button("\uE72C",Refresh); refresh.FontFamily = new FontFamily("Segoe MDL2 Assets"); refresh.Width = 32; refresh.ToolTip = "Обновить список"; AutomationProperties.SetName(refresh,"Обновить список");
        DockPanel.SetDock(refresh,Dock.Right); listHeader.Children.Add(refresh);
        var listHeading = new StackPanel(); folderTitle.SetResourceReference(TextBlock.ForegroundProperty,"Ink"); accountCaption.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        listHeading.Children.Add(folderTitle); listHeading.Children.Add(accountCaption); listHeader.Children.Add(listHeading);
        DockPanel.SetDock(listHeader,Dock.Top); list.Children.Add(listHeader);
        var tools = new DockPanel { Margin = new Thickness(10,0,9,9) };
        var find = Button("\uE721", () => ChangeFolder(label,search.Text.Trim())); find.FontFamily = new FontFamily("Segoe MDL2 Assets"); find.Width = 32; find.ToolTip = "Найти письма"; AutomationProperties.SetName(find,"Найти письма");
        DockPanel.SetDock(find,Dock.Right); tools.Children.Add(find); search.SetResourceReference(StyleProperty,"NativeSearchBox"); AutomationProperties.SetName(search,"Поиск в Gmail"); tools.Children.Add(search);
        DockPanel.SetDock(tools,Dock.Top); list.Children.Add(tools);
        var footer = new StackPanel(); more.SetResourceReference(StyleProperty,"NativeToolbarButton"); more.Click += async (_,_) => await Execute(LoadMore); more.Visibility = Visibility.Collapsed;
        footer.Children.Add(more); note.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); footer.Children.Add(note); DockPanel.SetDock(footer,Dock.Bottom); list.Children.Add(footer); list.Children.Add(messages);
        splitter.SetResourceReference(BackgroundProperty,"Line"); splitter.DragCompleted += (_,_) => customListWidth = true; SetColumn(splitter,2); Children.Add(splitter);
        content.SetResourceReference(BackgroundProperty,"Panel"); SetColumn(content,3); Children.Add(content);
        var toolbar = new Border { Child = actions, BorderThickness = new Thickness(0,0,0,1) }; toolbar.SetResourceReference(Border.BorderBrushProperty,"Line");
        DockPanel.SetDock(toolbar,Dock.Top); content.Children.Add(toolbar); messageScroll.Content = body; content.Children.Add(messageScroll);
        SizeChanged += (_,_) => { if (!customListWidth) ColumnDefinitions[1].Width = new GridLength(ActualWidth >= 2200 ? 420 : ActualWidth >= 1500 ? 380 : ActualWidth < 1050 ? 300 : 340); };
        UpdateHeading();
    }
    private Button SideButton(string caption, string icon, Func<Task> action)
    {
        var button = Button(caption,action); button.Tag = icon; button.SetResourceReference(StyleProperty,"GmailSideButton"); return button;
    }
    private void ApplyFolderLayout()
    {
        if (ColumnDefinitions.Count > 0) ColumnDefinitions[0].Width = new GridLength(FoldersCollapsed ? 48 : 196);
    }
    private void UpdateHeading()
    {
        var folder = (folders.ItemsSource as IEnumerable<GmailLabel>)?.FirstOrDefault(item => item.Id == label);
        folderTitle.Text = query.Length > 0 ? "Результаты поиска" : folder is null ? "Входящие" : GmailDisplayConverter.FolderName(folder);
        accountCaption.Text = address.Length == 0 ? profile.Name : address; accountCaption.ToolTip = accountCaption.Text;
    }
    private void ShowEmptyMessage(string heading = "Выберите письмо", string text = "Отправитель, текст и изображения появятся здесь.")
    {
        if (HasDraft || showingAccount) return;
        body.Children.Clear(); actions.Children.Clear();
        var title = new TextBlock { Text = heading, Margin = new Thickness(0,24,0,10) }; title.SetResourceReference(StyleProperty,"NativeTitle"); body.Children.Add(title);
        var hint = new TextBlock { Text = text }; hint.SetResourceReference(StyleProperty,"NativeMetadata"); body.Children.Add(hint);
    }
    private async Task Execute(Func<Task> action, bool background = false)
    {
        if (disposed) return;
        try { if (background) { if (!await operation.WaitAsync(0,lifetime.Token)) return; } else await operation.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (disposed) { operation.Release(); return; }
        try { await action(); }
        catch (OperationCanceledException) { if (!disposed) note.Text = "Операция отменена или истекло время ожидания. Повторите подключение/обновление."; }
        catch (Exception ex)
        {
            store.Log("gmail_operation_failed");
            note.Text = ex is GmailApiException ge ? ge.Message switch
            {
                "gmail_sign_in" => "Нужен повторный вход Google. Нажмите «Аккаунт Google → Войти через браузер».",
                "gmail_permissions" => "Google не разрешил доступ к почте. Для тестовой версии ваш адрес должен быть разрешён в настройках приложения Google.",
                "gmail_client_invalid" => "Google не принял регистрацию MailTrim. Нужна разовая настройка приложения, затем можно повторить вход.",
                "gmail_browser_unavailable" => "Не удалось открыть браузер. Проверьте, выбран ли в Windows браузер по умолчанию, и повторите вход.",
                "gmail_network" => "Не удалось связаться с Google. Проверьте подключение и повторите вход.",
                "gmail_quota" => "Лимит запросов Google. Подождите до следующей проверки.",
                "gmail_missing" => "Письмо больше недоступно. Обновите список.",
                "gmail_consent_cancelled" => "Вход отменён в Google.", _ => "Не удалось выполнить запрос Gmail. Проверьте сеть и повторите."
            } : "Не удалось выполнить операцию Gmail. Проверьте подключение и доступ к локальным файлам.";
            status(profile.Id,new("Gmail: " + note.Text,null,null));
        }
        finally { operation.Release(); }
    }
    private async Task Initialize()
    {
        SetFolders([]); ShowCached();
        if (!oauth.Connected) { ShowSetup(); return; }
        address = await api.Address(lifetime.Token); await LoadFolders(); await Refresh(); await CheckStatus();
    }
    public void SetActive(bool value)
    {
        active = value; Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value && showingAccount)
        {
            if (!oauth.Connected && connecting is null) ShowSetup();
            return;
        }
        if (value && selected is not null && !HasDraft) Render(selected);
        if (value) _ = Execute(async () => { if (oauth.Connected) { await LoadFolders(); await Refresh(); } else ShowSetup(); });
        else { imageRender?.Cancel(); }
    }
    private void SetFolders(GmailLabel[] custom)
    {
        selecting = true;
        try
        {
            folders.ItemsSource = new[] { new GmailLabel("INBOX","Входящие"), new GmailLabel("","Все письма"), new GmailLabel("STARRED","Помеченные"), new GmailLabel("SENT","Отправленные"), new GmailLabel("DRAFT","Черновики"), new GmailLabel("SPAM","Спам"), new GmailLabel("TRASH","Корзина") }.Concat(custom.Where(l => l.Id.StartsWith("Label_",StringComparison.Ordinal)).OrderBy(l => l.Name)).ToArray();
            var groups = CollectionViewSource.GetDefaultView(folders.ItemsSource).GroupDescriptions;
            groups?.Clear(); groups?.Add(new PropertyGroupDescription(nameof(GmailLabel.Id),new GmailDisplayConverter { Kind = "FolderGroup" }));
            folders.SelectedItem = ((IEnumerable<GmailLabel>)folders.ItemsSource).FirstOrDefault(l => l.Id == label);
            UpdateHeading();
        }
        finally { selecting = false; }
    }
    private async Task LoadFolders() => SetFolders(await api.Labels(lifetime.Token));
    private GmailClientRegistration? SignInRegistration => oauth.SavedRegistration ?? registration.Load();
    public bool BrowserSignInAvailable => SignInRegistration is not null;
    private void AccountLayout(bool value)
    {
        showingAccount = value;
        sidebar.Visibility = list.Visibility = splitter.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        SetColumn(content,value ? 0 : 3); SetColumnSpan(content,value ? 4 : 1);
        body.MaxWidth = value ? double.PositiveInfinity : GmailReadingView.MaxContentWidth;
        body.HorizontalAlignment = value ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
    private void ShowMailbox()
    {
        if (showingAccount) { body.Children.Clear(); actions.Children.Clear(); }
        AccountLayout(false);
    }
    private TextBlock ConnectionNote()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
        text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = note });
        text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        return text;
    }
    private void ShowSetup()
    {
        if (disposed) return;
        messageScroll.ScrollToTop();
        AccountLayout(true);
        body.Children.Clear(); actions.Children.Clear();
        var panel = new StackPanel { MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8,32,8,8) };
        body.Children.Add(panel);
        panel.Children.Add(new TextBlock { Text = oauth.Connected ? "Аккаунт Google" : "Подключите Gmail", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        panel.Children.Add(new TextBlock { Text = oauth.Connected && address.Length > 0 ? address : "Войдите в Google в своём браузере. После подтверждения почта появится здесь.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        var available = BrowserSignInAvailable;
        if (!available)
            panel.Children.Add(new TextBlock { Text = "В этой сборке вход Google ещё не настроен. Сначала нужно один раз зарегистрировать MailTrim в Google. Повторять настройку для каждого ящика не потребуется.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        var signIn = new Button { Content = "Войти через браузер", IsEnabled = available && connecting is null, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(22,11,22,11), Margin = new Thickness(0,4,0,12), Foreground = Brushes.White };
        signIn.SetResourceReference(BackgroundProperty,"Accent");
        signIn.Click += async (_,_) =>
        {
            signIn.IsEnabled = false;
            try { await Execute(ConnectBrowser); }
            finally { if (!disposed) signIn.IsEnabled = BrowserSignInAvailable && connecting is null; }
        };
        panel.Children.Add(signIn);
        var privacy = new TextBlock { Text = "Пароль и код подтверждения вводятся только на странице Google. MailTrim не получает пароль и не переносит cookies браузера.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,0,0,16) };
        privacy.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); panel.Children.Add(privacy);
        if (oauth.Connected)
        {
            panel.Children.Add(Button("Вернуться к письмам", () => { ShowMailbox(); if (selected is not null) Render(selected); return Task.CompletedTask; }));
            panel.Children.Add(Button("Отключить этот аккаунт", async () =>
            {
                if (MessageBox.Show(Window.GetWindow(this),"Отключить Gmail и удалить локальные данные этого аккаунта? Письма в Google останутся.","Отключение Gmail",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) == MessageBoxResult.Yes) await ClearDataInternal();
            }));
        }
        panel.Children.Add(Button("Открыть Gmail в браузере", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mail.google.com/") { UseShellExecute = true }); return Task.CompletedTask; }));
        panel.Children.Add(ConnectionNote());
        var advanced = new StackPanel();
        advanced.Children.Add(new TextBlock { Text = "Настройка приложения выполняется один раз на этом компьютере. Уже подключённые аккаунты сохраняют свои разрешения.", TextWrapping = TextWrapping.Wrap });
        advanced.Children.Add(Button("Настроить приложение Google…", () =>
        {
            if (new GmailConnectionSettingsWindow(registration) { Owner = Window.GetWindow(this) }.ShowDialog() == true) ShowSetup();
            return Task.CompletedTask;
        }));
        panel.Children.Add(new Expander { Header = "Дополнительно", Content = advanced, IsExpanded = false, Margin = new Thickness(0,16,0,0) });
        note.Text = oauth.Connected ? "Gmail подключён" : available ? "Готово к входу через браузер" : "Нужна разовая настройка MailTrim";
    }
    private async Task ConnectBrowser()
    {
        if (HasDraft) { note.Text = "Сначала отправьте или отмените открытый черновик."; return; }
        var client = SignInRegistration;
        if (client is null) { ShowSetup(); return; }
        if (connecting is not null) return;
        await StopPreparation();
        note.Text = "Завершите вход в системном браузере (до 3 минут)…";
        body.Children.Clear(); actions.Children.Clear();
        body.Children.Add(new TextBlock { Text = "Ожидаю вход в Google", FontSize = 26, Margin = new Thickness(0,20,0,12) });
        body.Children.Add(new TextBlock { Text = "Подтвердите доступ в открывшемся браузере. После завершения вернитесь в MailTrim.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(ConnectionNote());
        using var connectStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connecting = connectStop;
        var cancel = new Button { Content = "Отменить вход", HorizontalAlignment = HorizontalAlignment.Left };
        cancel.Click += (_,_) => connectStop.Cancel(); body.Children.Add(cancel);
        var completed = false;
        try { await oauth.Connect(client,connectStop.Token); completed = true; }
        finally { connecting = null; body.Children.Remove(cancel); if (!completed && !disposed) ShowSetup(); }
        prepared.Clear(); pages.Clear(); selected = null; inboxHead = null; label = "INBOX"; query = "";
        try { address = await api.Address(lifetime.Token); await LoadFolders(); await Refresh(); await CheckStatus(); }
        catch { if (!disposed) ShowSetup(); throw; }
        ShowEmptyMessage("Gmail подключён","Выберите письмо в списке слева.");
        if (active && Window.GetWindow(this) is MainWindow window) window.RestoreWindow();
    }
    private void ShowCached()
    {
        if (query.Length == 0 && pages.TryGetValue(label,out var cached))
        { SetMessages(cached.Page.Messages); next = cached.Page.NextPageToken; UpdatePagination(); note.Text = $"Локальная копия · проверено {cached.CheckedAt.LocalDateTime:g}. Обновляется…"; }
    }
    private void SetMessages(IEnumerable<GmailMessage> list)
    {
        selecting = true;
        try { messages.ItemsSource = list.OrderByDescending(m => m.ReceivedAt).ThenBy(m => m.Id,StringComparer.Ordinal).ToArray(); messages.SelectedItem = ((GmailMessage[])messages.ItemsSource).FirstOrDefault(m => m.Id == selected?.Id); }
        finally { selecting = false; }
    }
    private async Task ChangeFolder(string folder, string find)
    {
        if (HasDraft)
        {
            note.Text = "Сначала отправьте или отмените открытый черновик."; selecting = true;
            try { folders.SelectedItem = ((IEnumerable<GmailLabel>)folders.ItemsSource).FirstOrDefault(f => f.Id == label); }
            finally { selecting = false; } return;
        }
        label = folder; query = find; search.Text = find; selected = null; imageRender?.Cancel(); body.Children.Clear(); actions.Children.Clear(); messages.ItemsSource = null; next = null;
        UpdateHeading(); ShowEmptyMessage();
        ShowCached(); await Refresh();
    }
    private void UpdatePagination() { more.IsEnabled = next is not null; more.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible; }
    public Task Refresh() => RefreshPage(false);
    private Task LoadMore() => RefreshPage(true);
    private async Task RefreshPage(bool append)
    {
        if (!oauth.Connected) { ShowSetup(); return; }
        if (HasDraft) return;
        if (append && next is null) return;
        ShowMailbox();
        note.Text = append ? "Загрузка следующей страницы…" : "Обновление Gmail…";
        var page = await api.Page(label,query,append ? next : null,lifetime.Token);
        var list = append ? ((GmailMessage[]?)messages.ItemsSource ?? []).Concat(page.Messages).DistinctBy(m => m.Id).ToArray() : page.Messages;
        SetMessages(list); next = page.NextPageToken; UpdatePagination(); UpdateHeading();
        if (!append && query.Length == 0) pages[label] = new(label,page,DateTimeOffset.Now);
        foreach (var message in page.Messages) if (prepared.TryGetValue(message.Id,out var old)) prepared[message.Id] = old with { Labels = message.Labels };
        SaveCache(); note.Text = $"Писем: {list.Length} · новые сверху · проверено {DateTime.Now:t}";
        if (selected is null) ShowEmptyMessage(list.Length == 0 ? "Писем нет" : "Выберите письмо",list.Length == 0 ? "В этой папке или по этому запросу писем пока не найдено." : "Отправитель, текст и изображения появятся здесь.");
        // API reads do not mark unread mail as read. Keep the existing explicit preparation preference.
        if (store.Settings.AutomaticMailboxCache && preparation.IsCompleted)
        {
            preparing?.Dispose(); preparing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            preparation = PrepareRead(page.Messages,preparing.Token);
        }
    }
    private async Task PrepareRead(IEnumerable<GmailMessage> list, CancellationToken token)
    {
        try
        {
            foreach (var message in list.Where(m => !m.Unread && !prepared.ContainsKey(m.Id)).Take(8))
            {
                if (!store.Settings.AutomaticMailboxCache) return;
                var ready = await api.Message(message.Id,token); token.ThrowIfCancellationRequested();
                if (!ready.Unread) Remember(ready);
            }
        }
        catch (OperationCanceledException) { }
        catch { store.Log("gmail_preparation_failed"); }
    }
    private async Task StopPreparation() { preparing?.Cancel(); await preparation; }
    private void Remember(GmailMessage message)
    {
        prepared.Remove(message.Id); prepared[message.Id] = message;
        while (prepared.Count > 100 || prepared.Values.Sum(m => m.Text.Length) > 4_000_000) prepared.Remove(prepared.Keys.First());
        SaveCache();
    }
    private void SaveCache() => vault.Save("cache",new GmailCache(pages.Values.TakeLast(40).ToArray(),prepared.Values.ToArray(),address));
    private async Task Open(GmailMessage message)
    {
        if (selected?.Id != message.Id) messageScroll.ScrollToTop();
        selected = message;
        if (prepared.TryGetValue(message.Id,out var cached)) { selected = cached with { Labels = message.Labels }; Render(selected); }
        else
        {
            body.Children.Clear(); actions.Children.Clear();
            body.Children.Add(GmailReadingView.CreateHeader(message));
            body.Children.Add(GmailReadingView.CreateBodyText(message.Preview));
            var loading = new TextBlock { Text = "Загрузка полного письма…" }; loading.SetResourceReference(StyleProperty,"NativeMetadata"); body.Children.Add(loading);
            actions.Children.Add(Button("Открыть Gmail в браузере", () => { OpenBrowser(message); return Task.CompletedTask; }));
        }
        var ready = await api.Message(message.Id,lifetime.Token); selected = ready; Remember(ready);
        if (messages.ItemsSource is GmailMessage[] current) SetMessages(current.Select(item => item.Id == ready.Id ? ready : item));
        Render(ready);
        // Merely preparing/fetching a message is read-only; the user controls its read status.
    }
    private void Render(GmailMessage message)
    {
        imageRender?.Cancel(); imageRender = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = imageRender.Token;
        body.Children.Clear(); actions.Children.Clear();
        if (!message.Labels.Contains("DRAFT")) actions.Children.Add(Button("Ответить", () => { Compose(message); return Task.CompletedTask; }));
        actions.Children.Add(Button(message.Unread ? "Прочитано" : "Не прочитано", async () => { await api.Modify(message.Id,message.Unread ? [] : ["UNREAD"],message.Unread ? ["UNREAD"] : [],lifetime.Token); prepared.Remove(message.Id); await Open(message); await CheckStatus(); }));
        actions.Children.Add(Button("В архив", async () => { await api.Modify(message.Id,[],["INBOX"],lifetime.Token); await Refresh(); await CheckStatus(); }));
        actions.Children.Add(Button("В корзину", async () => { if (MessageBox.Show(Window.GetWindow(this),"Переместить письмо в корзину Gmail?","Gmail",MessageBoxButton.YesNo) == MessageBoxResult.Yes) { await api.Trash(message.Id,lifetime.Token); prepared.Remove(message.Id); selected = null; body.Children.Clear(); actions.Children.Clear(); await Refresh(); await CheckStatus(); } }));
        body.Children.Add(GmailReadingView.CreateHeader(message));
        body.Children.Add(GmailReadingView.CreateBodyText(message.Text.Length == 0 ? "Текст отсутствует. Проверьте вложения или откройте письмо в Gmail." : message.Text));
        if (message.Images.Length > 0)
        {
            var imageHeading = new TextBlock { Text = "Изображения из письма", Margin = new Thickness(0,8,0,8) }; imageHeading.SetResourceReference(StyleProperty,"NativeMetadata"); body.Children.Add(imageHeading);
        }
        int auto = 0;
        foreach (var url in message.Images)
        {
            var target = new StackPanel(); body.Children.Add(target);
            var imageButton = new Button { Content = "Показать изображение", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,4,0,4) }; target.Children.Add(imageButton);
            async Task LoadImage()
            {
                imageButton.IsEnabled = false;
                try { var bitmap = await images.Load(new Uri(url),token); if (!token.IsCancellationRequested) { target.Children.Clear(); target.Children.Add(GmailReadingView.CreateImage(bitmap)); } }
                catch { if (!token.IsCancellationRequested) { imageButton.IsEnabled = true; imageButton.Content = "Повторить загрузку изображения"; } }
            }
            imageButton.Click += async (_,_) => await LoadImage();
            if (store.Settings.AutomaticallyLoadReaderImages && auto++ < 12) _ = LoadImage();
        }
        foreach (var attachment in message.Attachments)
            body.Children.Add(Button($"Сохранить: {attachment.Name}", async () =>
            {
                var safeName = Path.GetFileName(attachment.Name.Replace('\\','/')); if (safeName.Length == 0) safeName = "attachment";
                var dialog = new SaveFileDialog { FileName = safeName, Title = "Сохранить вложение Gmail" };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) { var data = await api.Attachment(message.Id,attachment.Id,lifetime.Token); await File.WriteAllBytesAsync(dialog.FileName,data,lifetime.Token); }
            }));
        body.Children.Add(Button("Открыть Gmail в браузере", () => { OpenBrowser(message); return Task.CompletedTask; }));
    }
    private void OpenBrowser(GmailMessage message) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mail.google.com/mail/?authuser=" + Uri.EscapeDataString(address) + "#all/" + Uri.EscapeDataString(message.ThreadId)) { UseShellExecute = true });
    private void Compose(GmailMessage? reply)
    {
        if (!oauth.Connected || HasDraft) return;
        messageScroll.ScrollToTop();
        imageRender?.Cancel(); body.Children.Clear(); actions.Children.Clear(); HasDraft = true;
        var to = new TextBox { Text = reply is null ? "" : reply.ReplyTo.Length > 0 ? reply.ReplyTo : reply.From, Margin = new Thickness(0,4,0,10) };
        var subject = new TextBox { Text = reply is null ? "" : reply.Subject.StartsWith("Re:",StringComparison.OrdinalIgnoreCase) ? reply.Subject : "Re: " + reply.Subject, Margin = new Thickness(0,4,0,10), MaxLength = 500 };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 250, MaxLength = 1_000_000 };
        body.Children.Add(new TextBlock { Text = "От: " + address, Margin = new Thickness(0,0,0,12) });
        body.Children.Add(new TextBlock { Text = "Кому" }); body.Children.Add(to); body.Children.Add(new TextBlock { Text = "Тема" }); body.Children.Add(subject); body.Children.Add(text);
        body.Children.Add(new TextBlock { Text = "Черновик хранится только в открытом окне; отправка — после подтверждения. Вложения к новым письмам пока добавляются в веб-интерфейсе Gmail.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
        var send = Button("Отправить", async () =>
        {
            _ = GmailMime.RawMessage(to.Text.Trim(),subject.Text,text.Text,reply);
            if (MessageBox.Show(Window.GetWindow(this),$"Отправить письмо на {to.Text.Trim()}?","Gmail",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { await api.Send(to.Text.Trim(),subject.Text,text.Text,reply,lifetime.Token); HasDraft = false; note.Text = "Письмо отправлено"; body.Children.Clear(); actions.Children.Clear(); await Refresh(); }
            catch { MessageBox.Show(Window.GetWindow(this),"Отправка не подтверждена. Прежде чем повторять, проверьте «Отправленные» в Gmail: письмо могло уйти, даже если ответ сервера не пришёл.","Отправка Gmail"); throw; }
        }); actions.Children.Add(send);
        actions.Children.Add(Button("Отменить", () =>
        {
            if (text.Text.Length == 0 || MessageBox.Show(Window.GetWindow(this),"Удалить текст открытого черновика?","Gmail",MessageBoxButton.YesNo) == MessageBoxResult.Yes) { HasDraft = false; if (selected is not null) Render(selected); else { body.Children.Clear(); actions.Children.Clear(); } }
            return Task.CompletedTask;
        }));
        text.Focus();
    }
    private async Task CheckStatus()
    {
        if (!oauth.Connected) return;
        var current = await api.InboxStatus(lifetime.Token);
        var added = inboxHead is null ? 0 : Array.IndexOf(current.Ids,inboxHead);
        inboxHead = current.Ids.FirstOrDefault();
        status(profile.Id,new("Проверено",current.Unread,DateTimeOffset.Now));
        if (store.Settings.NotifyNewMail && added > 0) notify(added);
        
    }
    private async Task Poll() { await CheckStatus(); if (active && !HasDraft && !showingAccount) await Refresh(); }
    public void RequestCheck() => _ = Execute(Poll, background: true);
    public void RequestRefresh() => _ = Execute(Refresh);
    public void Inbox() => _ = Execute(() => ChangeFolder("INBOX",""));
    public void FocusSearch() => search.Focus();
    public async Task ClearData()
    {
        await operation.WaitAsync();
        try { await ClearDataInternal(); } finally { operation.Release(); }
    }
    private async Task ClearDataInternal()
    {
        await StopPreparation(); await oauth.Disconnect(); prepared.Clear(); pages.Clear(); address = ""; selected = null; inboxHead = null; HasDraft = false; imageRender?.Cancel(); messages.ItemsSource = null; ShowSetup(); status(profile.Id,new("Gmail не подключён",null,null));
    }
    public void Dispose()
    { if (disposed) return; disposed = true; timer.Stop(); lifetime.Cancel(); connecting?.Cancel(); preparing?.Cancel(); imageRender?.Cancel(); images.Dispose(); api.Dispose(); oauth.Dispose(); }
}
