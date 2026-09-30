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
    private bool paused;
    private bool busy;
    private BrowserSession? Current => Profiles.SelectedItem is AccountProfile p ? sessions.GetValueOrDefault(p.Id) : null;
    public MainWindow(LocalStore store)
    {
        this.store = store;
        InitializeComponent(); ApplyTheme();
        Loaded += async (_, _) =>
        {
            RefreshProfiles(store.Settings.ActiveProfile);
            if (store.RulesRecovered) MessageBox.Show(this, "Локальные правила повреждены. Временно используются встроенные. Исправьте или сбросьте их в настройках.", "Правила");
            if (store.Settings.CheckUpdatesOnStartup) await UpdateChecker.Check(this, store, true);
        };
        Closed += (_, _) => { foreach (var session in sessions.Values) session.Dispose(); store.Log("app_closed"); };
        Closing += (_, e) => { if (busy) { e.Cancel = true; Status.Text = "Дождитесь завершения текущей операции."; } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { Current?.View.Reload(); e.Handled = true; }
            if (e.SystemKey == Key.Left && Current?.View.CanGoBack == true) { Current.View.GoBack(); e.Handled = true; }
        };
        store.Log("app_started");
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { await action(); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Не найден Microsoft Edge WebView2 Runtime. Установите Evergreen Runtime с сайта Microsoft и перезапустите приложение. Ссылка есть в README.", "Нужен WebView2");
        }
        catch (Exception ex)
        {
            store.Log("operation_failed");
            MessageBox.Show(this, "Операция не выполнена. Проверьте сеть, права на локальные файлы и настройки. Код: " + ex.GetType().Name, "MailTrim");
        }
        finally { busy = false; IsEnabled = true; }
    }
    private void RefreshProfiles(Guid? selected = null)
    {
        Profiles.ItemsSource = null;
        Profiles.ItemsSource = store.Settings.Profiles;
        Profiles.SelectedItem = store.Settings.Profiles.FirstOrDefault(p => p.Id == selected) ?? store.Settings.Profiles.FirstOrDefault();
    }
    private async Task ShowProfile()
    {
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
        session.SetActive(true);
    }
    private async void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => await Run(ShowProfile);
    private void Back_Click(object sender, RoutedEventArgs e) { if (Current?.View.CanGoBack == true) Current.View.GoBack(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (Current is { } s) s.View.Reload(); else _ = Run(ShowProfile); }
    private void Home_Click(object sender, RoutedEventArgs e) => Current?.View.CoreWebView2.Navigate("https://e.mail.ru/inbox/");
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Фильтры будут переключены, а открытые страницы перезагружены. Сохраните незавершённые письма перед продолжением.", "Переключение фильтров", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Run(async () =>
        {
            paused = !paused; PauseButton.Content = paused ? "Включить фильтры" : "Приостановить фильтры";
            foreach (var s in sessions.Values) await s.ApplySettings();
        });
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
        await Run(async () => { await current.ClearData(); current.View.CoreWebView2.Navigate("https://e.mail.ru/inbox/"); store.Log("session_cleared"); });
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not AccountProfile p || Current is not { } current) return;
        if (MessageBox.Show(this, "Удалить профиль и очистить его локальную сессию? Серверный ящик и письма не удаляются.", "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            await current.ClearData(); current.Dispose(); BrowserHost.Children.Remove(current.View); sessions.Remove(p.Id);
            store.Settings.Profiles.Remove(p); store.Settings.ActiveProfile = store.Settings.Profiles.FirstOrDefault()?.Id; store.Save();
            RefreshProfiles(store.Settings.ActiveProfile); await ShowProfile();
        });
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(store) { Owner = this }.ShowDialog() != true) return;
        await Run(async () => { ApplyTheme(); foreach (var s in sessions.Values) await s.ApplySettings(); });
    }
    private void ApplyTheme()
    {
        var dark = store.Settings.Theme == "Dark" || (store.Settings.Theme == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int n && n == 0);
        var colors = dark ? new[] { "#151C29", "#1F293A", "#EDF2FC", "#A7B4CC", "#344158" } : new[] { "#F5F7FB", "#FFFFFF", "#17233B", "#5B6980", "#DCE3EF" };
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Line" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
