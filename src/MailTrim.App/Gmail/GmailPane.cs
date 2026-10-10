using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly GmailClient api;
    private readonly ReaderImageLoader images = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operation = new(1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly ListBox folders = new() { DisplayMemberPath = "Name", BorderThickness = new Thickness(0) };
    private readonly ListBox messages = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox search = new() { MinWidth = 100, Margin = new Thickness(4), ToolTip = "Поиск Gmail, например is:unread или from:адрес" };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), FontSize = 12 };
    private readonly StackPanel body = new() { Margin = new Thickness(20,12,20,20) };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly Button more;
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
    private CancellationTokenSource? imageRender;
    private CancellationTokenSource? connecting;
    private CancellationTokenSource? preparing;
    private Task preparation = Task.CompletedTask;
    public bool HasDraft { get; private set; }
    public bool Connected => oauth.Connected;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public GmailPane(LocalStore store, AccountProfile profile, Action<Guid, MailboxStatus> status, Action<int> notify, System.Net.Http.HttpMessageHandler? testHandler = null)
    {
        this.store = store; this.profile = profile; this.status = status; this.notify = notify;
        vault = new(store.Root, profile.Id); oauth = new(vault); api = new(oauth.AccessToken,testHandler);
        SetResourceReference(BackgroundProperty,"Panel");
        ColumnDefinitions.Add(new() { Width = new GridLength(170) });
        ColumnDefinitions.Add(new() { Width = new GridLength(320) });
        ColumnDefinitions.Add(new() { Width = new GridLength(5) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
        var sidebar = new DockPanel(); Children.Add(sidebar);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); sidebar.Children.Add(bottom);
        bottom.Children.Add(Button("⚙", () => { (Window.GetWindow(this) as MainWindow)?.ShowSettings(); return Task.CompletedTask; }));
        bottom.Children.Add(Button("Подключение Gmail", Configure));
        bottom.Children.Add(Button("⇔", () => { ColumnDefinitions[0].Width = new GridLength(ColumnDefinitions[0].Width.Value == 170 ? 40 : 170); return Task.CompletedTask; }));
        var compose = Button("Написать", () => { Compose(null); return Task.CompletedTask; }); DockPanel.SetDock(compose,Dock.Top); sidebar.Children.Add(compose);
        sidebar.Children.Add(folders);
        var list = new DockPanel(); SetColumn(list,1); Children.Add(list);
        var tools = new DockPanel(); DockPanel.SetDock(tools,Dock.Top); list.Children.Add(tools);
        var find = Button("Найти", () => ChangeFolder(label,search.Text.Trim())); DockPanel.SetDock(find,Dock.Right); tools.Children.Add(find); tools.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons,Dock.Top); list.Children.Add(buttons);
        buttons.Children.Add(Button("Обновить", Refresh)); more = Button("Ещё", LoadMore); buttons.Children.Add(more);
        DockPanel.SetDock(note,Dock.Top); list.Children.Add(note); list.Children.Add(messages);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext }; SetColumn(splitter,2); Children.Add(splitter);
        var content = new DockPanel(); SetColumn(content,3); Children.Add(content); DockPanel.SetDock(actions,Dock.Top); content.Children.Add(actions);
        content.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var template = new DataTemplate(typeof(GmailMessage));
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); text.SetValue(FrameworkElement.MarginProperty,new Thickness(6)); text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding());
        template.VisualTree = text; messages.ItemTemplate = template;
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
        var button = new Button { Content = title, Margin = new Thickness(3), Padding = new Thickness(7,5,7,5) };
        button.Click += async (_,_) => await Execute(action); return button;
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
                "gmail_sign_in" => "Нужен повторный вход Google. Откройте «Подключение Gmail».",
                "gmail_permissions" => "Google отказал в доступе. Проверьте Gmail API, тестовых пользователей и разрешения OAuth.",
                "gmail_quota" => "Лимит запросов Google. Подождите до следующей проверки.",
                "gmail_missing" => "Письмо больше недоступно. Обновите список.",
                "gmail_consent_cancelled" => "Вход отменён в Google.", _ => "Не удалось выполнить запрос Gmail. Проверьте сеть и повторите."
            } : "Не удалось выполнить операцию Gmail. Проверьте сеть, OAuth-клиент и локальные файлы.";
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
        if (value) _ = Execute(async () => { if (oauth.Connected) { await LoadFolders(); await Refresh(); } else ShowSetup(); });
        else { imageRender?.Cancel(); }
    }
    private void SetFolders(GmailLabel[] custom)
    {
        selecting = true;
        try
        {
            folders.ItemsSource = new[] { new GmailLabel("INBOX","Входящие"), new GmailLabel("","Все письма"), new GmailLabel("STARRED","Помеченные"), new GmailLabel("SENT","Отправленные"), new GmailLabel("DRAFT","Черновики"), new GmailLabel("SPAM","Спам"), new GmailLabel("TRASH","Корзина") }.Concat(custom.Where(l => l.Id.StartsWith("Label_",StringComparison.Ordinal)).OrderBy(l => l.Name)).ToArray();
            folders.SelectedItem = ((IEnumerable<GmailLabel>)folders.ItemsSource).FirstOrDefault(l => l.Id == label);
        }
        finally { selecting = false; }
    }
    private async Task LoadFolders() => SetFolders(await api.Labels(lifetime.Token));
    private void ShowSetup()
    {
        body.Children.Clear(); actions.Children.Clear();
        body.Children.Add(new TextBlock { Text = "Gmail через официальный API", FontSize = 24, Margin = new Thickness(0,0,0,12) });
        body.Children.Add(new TextBlock { Text = "Создайте OAuth-клиент Google типа Desktop app, включите Gmail API и добавьте свой адрес в тестовые пользователи. Импортируйте JSON клиента в «Подключение Gmail». Вход откроется в системном браузере. Пароль Google приложение не получает.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Button("Подключить Gmail", Configure));
        body.Children.Add(Button("Инструкция подключения", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/lineEdit/MailTrim/blob/main/docs/GMAIL.md") { UseShellExecute = true }); return Task.CompletedTask; }));
        note.Text = "Gmail не подключён";
    }
    private async Task Configure()
    {
        if (HasDraft) { note.Text = "Сначала отправьте или отмените открытый черновик."; return; }
        var owner = Window.GetWindow(this);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "JSON OAuth-клиента Google типа Desktop app. Пароль Google здесь не нужен.", TextWrapping = TextWrapping.Wrap });
        var id = new TextBox { Margin = new Thickness(0,8,0,8), MaxLength = 500 };
        var secret = new PasswordBox { Margin = new Thickness(0,8,0,8), MaxLength = 500 };
        panel.Children.Add(new TextBlock { Text = "Client ID" }); panel.Children.Add(id); panel.Children.Add(new TextBlock { Text = "Client secret (из JSON клиента)" }); panel.Children.Add(secret);
        var import = new Button { Content = "Импортировать JSON…", Margin = new Thickness(0,8,0,8) }; panel.Children.Add(import);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var connect = new Button { Content = "Войти через браузер", IsDefault = true }; panel.Children.Add(connect);
        var disconnect = new Button { Content = "Отключить и очистить локальные данные", IsEnabled = oauth.Connected }; panel.Children.Add(disconnect);
        var dialog = new Window { Title = "Подключение Gmail", Owner = owner, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = panel;
        import.Click += (_,_) =>
        {
            var file = new OpenFileDialog { Filter = "Google OAuth client (*.json)|*.json" };
            if (file.ShowDialog(dialog) != true) return;
            try { if (new FileInfo(file.FileName).Length > 65536) throw new FormatException(); var config = GmailOAuth.ParseDesktopConfig(File.ReadAllText(file.FileName)); id.Text = config.Id; secret.Password = config.Secret; error.Text = "Desktop client загружен"; }
            catch { error.Text = "Не удалось прочитать Desktop OAuth client. Проверьте JSON."; }
        };
        bool clear = false;
        connect.Click += (_,_) => { if (id.Text.Trim().Length > 0 && secret.Password.Length > 0) dialog.DialogResult = true; };
        disconnect.Click += (_,_) => { if (MessageBox.Show(dialog,"Удалить локальные токены и кэш этого Gmail? Серверные письма останутся. Разрешение Google можно отозвать в настройках аккаунта Google.","Отключение",MessageBoxButton.YesNo) == MessageBoxResult.Yes) { clear = true; dialog.DialogResult = true; } };
        if (dialog.ShowDialog() != true) return;
        if (clear) { await ClearDataInternal(); return; }
        await StopPreparation();
        note.Text = "Завершите вход в системном браузере (до 3 минут)…";
        using var connectStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connecting = connectStop;
        var cancel = new Button { Content = "Отменить вход", HorizontalAlignment = HorizontalAlignment.Left };
        cancel.Click += (_,_) => connectStop.Cancel(); body.Children.Add(cancel);
        try { await oauth.Connect(id.Text.Trim(),secret.Password,connectStop.Token); }
        finally { connecting = null; body.Children.Remove(cancel); }
        prepared.Clear(); pages.Clear(); selected = null; inboxHead = null; label = "INBOX"; query = "";
        address = await api.Address(lifetime.Token); await LoadFolders(); await Refresh(); await CheckStatus();
    }
    private void ShowCached()
    {
        if (query.Length == 0 && pages.TryGetValue(label,out var cached))
        { SetMessages(cached.Page.Messages); next = cached.Page.NextPageToken; more.IsEnabled = next is not null; note.Text = $"Локальная копия · проверено {cached.CheckedAt.LocalDateTime:g}. Обновляется…"; }
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
        ShowCached(); await Refresh();
    }
    public Task Refresh() => RefreshPage(false);
    private Task LoadMore() => RefreshPage(true);
    private async Task RefreshPage(bool append)
    {
        if (!oauth.Connected) { ShowSetup(); return; }
        if (HasDraft) return;
        if (append && next is null) return;
        note.Text = append ? "Загрузка следующей страницы…" : "Обновление Gmail…";
        var page = await api.Page(label,query,append ? next : null,lifetime.Token);
        var list = append ? ((GmailMessage[]?)messages.ItemsSource ?? []).Concat(page.Messages).DistinctBy(m => m.Id).ToArray() : page.Messages;
        SetMessages(list); next = page.NextPageToken; more.IsEnabled = next is not null;
        if (!append && query.Length == 0) pages[label] = new(label,page,DateTimeOffset.Now);
        foreach (var message in page.Messages) if (prepared.TryGetValue(message.Id,out var old)) prepared[message.Id] = old with { Labels = message.Labels };
        SaveCache(); note.Text = $"Писем: {list.Length} · новые сверху · проверено {DateTime.Now:t}";
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
        selected = message;
        if (prepared.TryGetValue(message.Id,out var cached)) { selected = cached with { Labels = message.Labels }; Render(selected); }
        else
        {
            body.Children.Clear(); actions.Children.Clear();
            body.Children.Add(new TextBlock { Text = message.Subject, FontSize = 24, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = message.Preview, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = "Загрузка полного письма…", Margin = new Thickness(0,12,0,0) });
            actions.Children.Add(Button("Открыть Gmail в браузере", () => { OpenBrowser(message); return Task.CompletedTask; }));
        }
        var ready = await api.Message(message.Id,lifetime.Token); selected = ready; Remember(ready); Render(ready);
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
        body.Children.Add(new TextBlock { Text = message.Subject, FontSize = 24, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) });
        body.Children.Add(new TextBlock { Text = message.From + " · " + message.ReceivedAt.LocalDateTime.ToString("g"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        body.Children.Add(new TextBox { Text = message.Text.Length == 0 ? "Текст отсутствует. Проверьте вложения или откройте письмо в Gmail." : message.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0,0,0,12) });
        int auto = 0;
        foreach (var url in message.Images)
        {
            var target = new StackPanel(); body.Children.Add(target);
            var imageButton = new Button { Content = "Показать изображение", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,4,0,4) }; target.Children.Add(imageButton);
            async Task LoadImage()
            {
                imageButton.IsEnabled = false;
                try { var bitmap = await images.Load(new Uri(url),token); if (!token.IsCancellationRequested) { target.Children.Clear(); target.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 2000, HorizontalAlignment = HorizontalAlignment.Left }); } }
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
    private async Task Poll() { await CheckStatus(); if (active && !HasDraft) await Refresh(); }
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
