using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailTrim.Core;
using Microsoft.Win32;

namespace MailTrim.App;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly Dictionary<Guid, BrowserSession> sessions = [];
    private ReaderPane? reader;
    private bool readerMode;
    private DesktopIntegration? desktop;
    private MailMonitor? monitor;
    private MailboxCacheMonitor? mailboxCache;
    private readonly System.Windows.Threading.DispatcherTimer visibleStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<Guid, DateTimeOffset> visibleStatusTimes = [];
    private bool readingVisibleStatus;
    public async Task RefreshVisibleMailboxStatus()
    {
        if (busy || readingVisibleStatus || Current is not { } session || session.View.CoreWebView2 is null || Profiles.SelectedItem is not AccountProfile profile) return;
        readingVisibleStatus = true;
        try
        {
            var json = await session.View.CoreWebView2.ExecuteScriptAsync(MailboxStatusScript.Snapshot);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(json, FilterRules.Json);
            if (snapshot is { Ready: true, Unread: >= 0 } && ReferenceEquals(Current, session))
            {
                var now = DateTimeOffset.Now;
                visibleStatusTimes[profile.Id] = now;
                profile.SetStatus(new MailboxStatus("Проверено", snapshot.Unread, now));
                ConnectionStatus.Text = profile.StatusText;
                desktop?.UpdateCounts(store.Settings.Profiles);
            }
        }
        catch (Exception) { /* A navigating or disposed view has no reliable snapshot. */ }
        finally { readingVisibleStatus = false; }
    }
    public void RestoreWindow() { if (desktop is not null) desktop.Restore(); else { Show(); Activate(); } }
    private bool paused;
    private bool busy;
    private bool confirmedShutdown;
    private readonly Func<string, bool> confirmReplyExit;
    internal void AllowConfirmedShutdown() => confirmedShutdown = true;
    private bool ConfirmReplyExit(bool allProfiles = false)
    {
        var count = allProfiles ? sessions.Values.Count(s => s.ReaderReplyOpen) : Current?.ReaderReplyOpen == true ? 1 : 0;
        return count == 0 || confirmReplyExit($"Открыта панель ответа (ящиков: {count}). Приложение не проверяет, сохранён ли черновик в Mail.ru.\n\nСначала отправьте письмо или сохраните черновик. Продолжить и покинуть страницу?");
    }
    private void NavigateFromReader(Action<BrowserSession> navigate, bool refreshReader = false)
    {
        if (busy || Current is not { } current) return;
        if (reader is { IsReplyVisible: false } native)
        {
            _ = Run(() => refreshReader ? native.RefreshList() : native.PreviousLetter());
            return;
        }
        if (!ConfirmReplyExit()) return;
        CloseReader(); current.ReaderReplyOpen = false;
        navigate(current);
    }
    private BrowserSession? Current => Profiles.SelectedItem is AccountProfile p ? sessions.GetValueOrDefault(p.Id) : null;
    public MainWindow(LocalStore store, bool desktopFeatures = false, Func<string, bool>? confirmReplyExit = null)
    {
        this.store = store;
        this.confirmReplyExit = confirmReplyExit ?? (message => MessageBox.Show(this, message, "Открыт ответ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        InitializeComponent(); ApplyTheme();
        VersionLabel.Text = "MailTrim " + typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        Loaded += async (_, _) =>
        {
            RefreshProfiles(store.Settings.ActiveProfile);
            if (store.RulesRecovered) MessageBox.Show(this, "Локальные правила повреждены. Временно используются встроенные. Исправьте или сбросьте их в настройках.", "Правила");
            if (store.RulesUpgradeAvailable) MessageBox.Show(this, "Доступны новые фильтры рекламы. Ваш изменённый набор сохранён. Чтобы использовать новый набор: Настройки → Правила → Встроенные правила → Сохранить и перезагрузить. Это заменит ваши изменения правил.", "Обновление фильтров");
            if (store.Settings.CheckUpdatesOnStartup) await UpdateChecker.Check(this, store, true);
        };
        Closed += (_, _) => { visibleStatusTimer.Stop(); monitor?.Dispose(); mailboxCache?.Dispose(); desktop?.Dispose(); desktop = null; CloseReader(); foreach (var session in sessions.Values) session.Dispose(); store.Log("app_closed"); };
        Closing += (_, e) =>
        {
            if (confirmedShutdown || desktop?.SystemEnding == true) return;
            if (busy) { e.Cancel = true; Status.Text = "Дождитесь завершения текущей операции."; return; }
            var goingToTray = desktop is not null && !desktop.Exiting && store.Settings.CloseToTray;
            if (!goingToTray && !ConfirmReplyExit(allProfiles: true)) e.Cancel = true;
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (busy) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Tab && Profiles.Items.Count > 1)
            {
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                Profiles.SelectedIndex = (Profiles.SelectedIndex + step + Profiles.Items.Count) % Profiles.Items.Count;
                e.Handled = true; return;
            }
            if (reader is not null && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            { reader.FocusSearch(); e.Handled = true; return; }
            if (reader is not null && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
            { e.Handled = true; await reader.ResetSearch(); return; }
            if (e.Key == Key.F5) { e.Handled = true; NavigateFromReader(s => s.View.Reload(), true); }
            if (e.SystemKey == Key.Left && (reader is not null || Current?.View.CanGoBack == true)) { e.Handled = true; NavigateFromReader(s => s.View.GoBack()); }
        };
        if (desktopFeatures)
        {
            _ = new WindowPositionManager(this, store);
            desktop = new DesktopIntegration(this, store);
            var backgroundHost = new Grid { Visibility = Visibility.Hidden, IsHitTestVisible = false };
            ((Grid)Content).Children.Add(backgroundHost);
            Grid.SetRow(backgroundHost, 1);
            monitor = new MailMonitor(store, backgroundHost, count => desktop.Notify(count), () => !busy, UpdateMailboxStatus); monitor.Start();
            mailboxCache = new MailboxCacheMonitor(store, this, backgroundHost, () => !busy);
            mailboxCache.Start();
            desktop.CheckRequested += monitor.RequestCheck;
            desktop.UpdateCounts(store.Settings.Profiles);
            visibleStatusTimer.Tick += async (_, _) => await RefreshVisibleMailboxStatus();
            visibleStatusTimer.Start();
        }
        store.Log("app_started");
    }

    private void UpdateMailboxStatus(Guid id, MailboxStatus status)
    {
        if (Current is not null && Profiles.SelectedItem is AccountProfile active && active.Id == id
            && visibleStatusTimes.TryGetValue(id, out var fresh) && DateTimeOffset.Now - fresh < TimeSpan.FromSeconds(15)) return;
        store.Settings.Profiles.FirstOrDefault(p => p.Id == id)?.SetStatus(status);
        if (Profiles.SelectedItem is AccountProfile selected) ConnectionStatus.Text = selected.StatusText;
        desktop?.UpdateCounts(store.Settings.Profiles);
    }
    private void CloseReader() => ResetReader(false);
    private void ResetReader(bool keepReading)
    {
        readerMode = keepReading;
        reader?.Close(); reader = null; ReaderHost.Children.Clear();
        ReaderHost.Visibility = keepReading ? Visibility.Visible : Visibility.Collapsed;
        BrowserHost.Visibility = keepReading ? Visibility.Hidden : Visibility.Visible;
        Current?.SetReadingOnly(keepReading);
        ReaderButton.Content = "Только важное";
    }
    private async void Reader_Click(object sender, RoutedEventArgs e)
    {
        if (reader is not null) { await Run(reader.OpenOriginal); return; }
        await OpenReader();
    }
    private async Task OpenReader()
    {
        if (Current?.View.CoreWebView2 is null) return;
        readerMode = true;
        reader = new ReaderPane(Current, CloseReader);
        ReaderHost.Children.Add(reader); BrowserHost.Visibility = Visibility.Hidden;
        ReaderHost.Visibility = Visibility.Visible; ReaderButton.Content = "Оригинал";
        await reader.Start();
    }
    private async Task Run(Func<Task> action, bool stopCache = false)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { if (monitor is not null) await monitor.StopAsync(); if (stopCache && mailboxCache is not null) await mailboxCache.StopAsync(); await action(); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Не найден Microsoft Edge WebView2 Runtime. Установите Evergreen Runtime с сайта Microsoft и перезапустите приложение. Ссылка есть в README.", "Нужен WebView2");
        }
        catch (Exception ex)
        {
            store.Log("operation_failed");
            MessageBox.Show(this, "Операция не выполнена. Проверьте сеть, права на локальные файлы и настройки. Код: " + ex.GetType().Name, "MailTrim");
        }
        finally { busy = false; IsEnabled = true; mailboxCache?.RequestCheck(); }
    }
    private void RefreshProfiles(Guid? selected = null)
    {
        Profiles.ItemsSource = null;
        Profiles.ItemsSource = store.Settings.Profiles;
        Profiles.SelectedItem = store.Settings.Profiles.FirstOrDefault(p => p.Id == selected) ?? store.Settings.Profiles.FirstOrDefault();
    }
    private async Task ShowProfile()
    {
        var keepReaderMode = readerMode;
        ResetReader(keepReaderMode);
        foreach (var s in sessions.Values) s.SetActive(false);
        EmptyLabel.Visibility = Profiles.SelectedItem is AccountProfile ? Visibility.Collapsed : Visibility.Visible;
        if (Profiles.SelectedItem is not AccountProfile profile) return;
        store.Settings.ActiveProfile = profile.Id; store.Save();
        if (!sessions.TryGetValue(profile.Id, out var session))
        {
            session = new BrowserSession(store, this, text => { if (Profiles.SelectedItem is AccountProfile p && p.Id == profile.Id) Status.Text = text; }, () => paused);
            sessions.Add(profile.Id, session); BrowserHost.Children.Add(session.View);
            try { await session.Initialize(profile.Id); }
            catch { sessions.Remove(profile.Id); BrowserHost.Children.Remove(session.View); session.Dispose(); throw; }
        }
        session.SetReadingOnly(readerMode);
        session.SetActive(true);
        ConnectionStatus.Text = profile.StatusText;
        if (readerMode) await OpenReader();
    }
    private async void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => await Run(ShowProfile);
    private void ProfileActions_Click(object sender, RoutedEventArgs e)
    {
        RenameMenu.IsEnabled = Profiles.SelectedItem is AccountProfile;
        ClearMenu.IsEnabled = RemoveMenu.IsEnabled = Current is not null;
        ProfileActionsButton.ContextMenu.PlacementTarget = ProfileActionsButton;
        ProfileActionsButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ProfileActionsButton.ContextMenu.IsOpen = true;
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if ((reader is not null || Current?.View.CanGoBack == true)) NavigateFromReader(s => s.View.GoBack()); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (Current is not null) NavigateFromReader(s => s.View.Reload(), true); else _ = Run(ShowProfile); }
    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (reader is { IsReplyVisible: false } native) { _ = Run(native.OpenInbox); return; }
        NavigateFromReader(s => s.View.CoreWebView2.Navigate("https://e.mail.ru/inbox/"), true);
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Фильтры будут переключены, а открытые страницы перезагружены. Сохраните незавершённые письма перед продолжением.", "Переключение фильтров", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Run(async () =>
        {
            CloseReader(); paused = !paused; PauseButton.Content = paused ? "Фильтры: выкл" : "Фильтры: вкл";
            PauseButton.ToolTip = paused ? "Включить фильтры" : "Приостановить фильтры";
            foreach (var s in sessions.Values) await s.ApplySettings();
        }, stopCache: true);
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (store.Settings.Profiles.Count >= 30) { MessageBox.Show(this, "Достигнут лимит: 30 профилей."); return; }
        var name = AskName("Новый ящик", "Новый ящик"); if (name is null) return;
        await Run(async () => { var p = new AccountProfile { Name = name }; store.Settings.Profiles.Add(p); store.Save(); RefreshProfiles(p.Id); await ShowProfile(); });
    }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not AccountProfile p) return;
        var name = AskName("Имя профиля", p.Name); if (name is null) return;
        await Run(async () => { p.Name = name; store.Save(); RefreshProfiles(p.Id); await ShowProfile(); });
    }
    private string? AskName(string title, string value)
    {
        var input = new TextBox { Text = value, MaxLength = 60, Margin = new Thickness(0,12,0,12) };
        var button = new Button { Content = "Сохранить", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Название только на этом компьютере (не пароль):" }); panel.Children.Add(input); panel.Children.Add(button);
        var dialog = new Window { Owner = this, Title = title, Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        button.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current) return;
        if (MessageBox.Show(this, "Выйти из этого ящика и удалить его локальные cookies, кэш и данные сайтов? Несохранённый текст будет потерян. Письма на сервере останутся.", "Очистка сессии", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(async () => { CloseReader(); await current.ClearData(); current.View.CoreWebView2.Navigate("https://e.mail.ru/inbox/"); store.Log("session_cleared"); }, stopCache: true);
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not AccountProfile p || Current is not { } current) return;
        if (MessageBox.Show(this, "Удалить профиль и очистить его локальную сессию? Серверный ящик и письма не удаляются.", "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            CloseReader(); await current.ClearData(); current.Dispose(); BrowserHost.Children.Remove(current.View); sessions.Remove(p.Id);
            store.Settings.Profiles.Remove(p); store.Settings.ActiveProfile = store.Settings.Profiles.FirstOrDefault()?.Id; store.Save();
            RefreshProfiles(store.Settings.ActiveProfile); await ShowProfile();
        }, stopCache: true);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    public async void ShowSettings(bool cacheTab = false)
    {
        var cacheWasEnabled = store.Settings.AutomaticMailboxCache;
        var dialog = new SettingsWindow(store, mailboxCache, cacheTab, async id =>
        {
            if (sessions.TryGetValue(id, out var session))
            {
                if (ReferenceEquals(Current, session) && reader is { IsReplyVisible: false }) CloseReader();
                await session.ReaderData.WaitForIdle(CancellationToken.None);
                session.ReaderData.Prepared.Clear(); session.ReaderState = null;
            }
        }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (mailboxCache is not null && !store.Settings.AutomaticMailboxCache) await mailboxCache.StopAsync();
        if (dialog.RequiresReload)
            await Run(async () => { CloseReader(); ApplyTheme(); foreach (var s in sessions.Values) await s.ApplySettings(); }, stopCache: true);
        if (!cacheWasEnabled && store.Settings.AutomaticMailboxCache) mailboxCache?.Resume();
        else mailboxCache?.RequestCheck(true);
    }
    private void ApplyTheme()
    {
        var dark = store.Settings.Theme == "Dark" || (store.Settings.Theme == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int n && n == 0);
        var colors = dark ? new[] { "#151C29", "#1F293A", "#EDF2FC", "#A7B4CC", "#344158" } : new[] { "#F5F7FB", "#FFFFFF", "#17233B", "#5B6980", "#DCE3EF" };
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Line" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}









