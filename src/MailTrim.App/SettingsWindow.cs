using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailTrim.Core;
using Microsoft.Win32;

namespace MailTrim.App;

public sealed class SettingsWindow : Window
{
    private readonly LocalStore store;
    private bool cacheOperation;
    public bool RequiresReload { get; private set; }
    private readonly CheckBox automaticCache = new() { Content = "Автоматически кэшировать прочитанные письма во всех папках" };
    private readonly CheckBox network = new() { Content = "Блокировать рекламные и аналитические запросы" };
    private readonly CheckBox cosmetic = new() { Content = "Скрывать рекламные блоки CSS-фильтрами" };
    private readonly CheckBox aggressive = new() { Content = "Дополнительно скрывать промо, новости и рекомендации" };
    private readonly CheckBox startup = new() { Content = "Запускать вместе с Windows, свёрнутым в трей" };
    private readonly CheckBox tray = new() { Content = "При закрытии окна продолжать работу в трее" };
    private readonly CheckBox notifications = new() { Content = "Уведомлять о новых письмах" };
    private readonly CheckBox siteNotifications = new() { Content = "Использовать уведомления сайта Mail.ru вместо опроса" };
    private readonly CheckBox updates = new() { Content = "Проверять новые версии при запуске (запрос к GitHub)" };
    private readonly CheckBox previewUpdates = new() { Content = "Включать предварительные версии (канал разработки)" };
    private readonly ComboBox theme = new() { ItemsSource = new[] { "System", "Light", "Dark" }, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox repository = new() { MaxLength = 120 };
    private readonly TextBox rules = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160 };
    public SettingsWindow(LocalStore store, MailboxCacheMonitor? cacheMonitor = null, bool openCacheTab = false, Func<Guid, Task>? beforeClear = null)
    {
        this.store = store;
        Closing += (_, e) => { if (cacheOperation) e.Cancel = true; };
        Title = "Настройки MailTrim"; Width = 820; Height = 780; MinWidth = 640; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        automaticCache.IsChecked = store.Settings.AutomaticMailboxCache;
        network.IsChecked = store.Settings.BlockRequests; cosmetic.IsChecked = store.Settings.CosmeticFilters; aggressive.IsChecked = store.Settings.Aggressive;
        startup.IsChecked = store.Settings.StartWithWindows; tray.IsChecked = store.Settings.CloseToTray; notifications.IsChecked = store.Settings.NotifyNewMail;
        siteNotifications.IsChecked = store.Settings.UseSiteNotifications;
        previewUpdates.IsChecked = store.Settings.IncludePrereleaseUpdates;
        updates.IsChecked = store.Settings.CheckUpdatesOnStartup; theme.SelectedItem = store.Settings.Theme; repository.Text = store.Settings.UpdateRepository;
        rules.Text = System.Text.Json.JsonSerializer.Serialize(store.Rules, FilterRules.Json);
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; var save = new Button { Content = "Сохранить" };
        footer.Children.Add(cancel); footer.Children.Add(save); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var main = new StackPanel { Margin = new Thickness(16) };
        main.Children.Add(new TextBlock { Text = "Фильтрация", FontSize = 22, FontWeight = FontWeights.SemiBold });
        main.Children.Add(network); main.Children.Add(cosmetic); main.Children.Add(aggressive);
        main.Children.Add(Note("Дополнительные правила могут скрыть полезные элементы. Если вход или почта не работают, приостановите фильтры кнопкой в главном окне."));
        main.Children.Add(Label("Тема оболочки и предпочтение для сайта")); main.Children.Add(theme);
        main.Children.Add(Note("System — системная, Light — светлая, Dark — тёмная. Веб-почта может использовать свою настройку темы."));
        main.Children.Add(Label("Локальные данные"));
        main.Children.Add(Note("Сессии хранятся в %LOCALAPPDATA%\\MailTrim\\Profiles. Журнал содержит только время и коды событий, без адресов, URL, заголовков и содержимого писем."));
        tabs.Items.Add(new TabItem { Header = "Вид и фильтры", Content = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var desktopPanel = new StackPanel { Margin = new Thickness(16) };
        desktopPanel.Children.Add(Label("Запуск и фоновая работа"));
        desktopPanel.Children.Add(startup); desktopPanel.Children.Add(tray); desktopPanel.Children.Add(notifications);
        desktopPanel.Children.Add(siteNotifications);
        desktopPanel.Children.Add(Note("По умолчанию новые письма проверяются раз в 2 минуты. В режиме сайта разрешены браузерные уведомления только от e.mail.ru; оповещения от опроса отключены, чтобы не дублировать их. В уведомлении сайта могут быть отправитель и тема письма."));
        desktopPanel.Children.Add(Note("Для режима сайта откройте вкладку каждого нужного ящика и включите уведомления в настройках самой почты, если они отключены. После смены режима обновите страницу. Приложение должно оставаться запущенным, в том числе в трее. Если сайт не присылает уведомления, вернитесь к обычной проверке."));
        desktopPanel.Children.Add(Note("Значок рядом с часами: двойной щелчок открывает окно, «Выйти» полностью закрывает приложение. Автозапуск привязан к текущему расположению EXE; после переноса приложения включите его заново."));
        desktopPanel.Children.Add(Note("Проверяются входящие через отдельную веб-сессию того же профиля, без открытия писем. Первый успешный опрос задаёт исходный список и не уведомляет о старой почте. Нужны интернет и действующий вход Mail.ru/VK ID. Оповещения от опроса не содержат текстов писем; Windows может скрывать их в режиме «Не беспокоить»."));
        tabs.Items.Add(new TabItem { Header = "Windows и уведомления", Content = new ScrollViewer { Content = desktopPanel } });
        var updatePanel = new StackPanel { Margin = new Thickness(16) };
        updatePanel.Children.Add(Label("Репозиторий обновлений: owner/repository")); updatePanel.Children.Add(repository); updatePanel.Children.Add(updates); updatePanel.Children.Add(previewUpdates);
        var check = new Button { Content = "Проверить версию сейчас", HorizontalAlignment = HorizontalAlignment.Left };
        check.Click += async (_, _) => { check.IsEnabled = false; try { await UpdateChecker.Check(this, store, false, repository.Text.Trim(), previewUpdates.IsChecked == true); } finally { check.IsEnabled = true; } };
        updatePanel.Children.Add(check); updatePanel.Children.Add(Note("Обновления lineEdit/MailTrim можно скачать и установить здесь. Перед перезапуском потребуется подтверждение. Для других репозиториев открывается страница релиза."));
        tabs.Items.Add(new TabItem { Header = "Обновления", Content = new ScrollViewer { Content = updatePanel } });
        var cachePanel = new StackPanel { Margin = new Thickness(16) };
        cachePanel.Children.Add(new TextBlock { Text = "Письма готовы к чтению", FontSize = 22, FontWeight = FontWeights.SemiBold });
        cachePanel.Children.Add(automaticCache);
        cachePanel.Children.Add(Note("Включите один раз и сохраните. Через 15 секунд после запуска приложение обходит доступные папки каждого ящика через отдельную скрытую страницу. Следующий обход — через 15 минут после завершения. Открытое письмо и редактор ответа остаются на месте. Нужен действующий вход Mail.ru/VK ID."));
        cachePanel.Children.Add(Note("Автоматически открываются только письма с явным признаком «прочитано». Непрочитанные, письма с неизвестным статусом и тела черновиков пропускаются; их заголовки сохраняются для поиска. Новые непрочитанные письма сохраняются после вашего открытия. Картинки и вложения автоматически не сохраняются в кэш; скрытая страница сайта может загружать картинки."));
        cachePanel.Children.Add(Label("Загрузка по ящикам"));
        var cacheProfile = new ComboBox { ItemsSource = store.Settings.Profiles, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = store.Settings.ActiveProfile, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        if (cacheProfile.SelectedIndex < 0 && cacheProfile.Items.Count > 0) cacheProfile.SelectedIndex = 0;
        cachePanel.Children.Add(cacheProfile);
        var cacheState = Note(""); cacheState.Name = "MailboxCacheStatus"; cachePanel.Children.Add(cacheState);
        var cacheBar = new ProgressBar { Name = "MailboxCacheProgress", Height = 6, Margin = new Thickness(0, 4, 0, 12) }; cachePanel.Children.Add(cacheBar);
        void RefreshCacheState()
        {
            if (cacheProfile.SelectedItem is AccountProfile profile && store.CacheProgress.TryGetValue(profile.Id, out var state))
            {
                cacheState.Text = state.Description;
                cacheBar.IsIndeterminate = state.Indeterminate; cacheBar.Value = state.Percent;
                cacheBar.Visibility = state.Running ? Visibility.Visible : Visibility.Collapsed;
            }
            else { cacheState.Text = store.Settings.AutomaticMailboxCache ? "Ожидание фоновой загрузки…" : "Автоматическая загрузка выключена. Открытые письма по-прежнему сохраняются."; cacheBar.Visibility = Visibility.Collapsed; }
            if (cacheMonitor?.Paused == true) cacheState.Text += "\nФоновая загрузка приостановлена до возобновления или следующего запуска приложения.";
        }
        void CacheChanged(Guid id, MailboxCacheProgress state) => RefreshCacheState();
        store.CacheProgressChanged += CacheChanged;
        Closed += (_, _) => store.CacheProgressChanged -= CacheChanged;
        cacheProfile.SelectionChanged += (_, _) => RefreshCacheState(); RefreshCacheState();
        var cacheActions = new WrapPanel(); cachePanel.Children.Add(cacheActions);
        var resumeCache = new Button { Content = "Проверить и продолжить", IsEnabled = cacheMonitor is not null };
        var stopCache = new Button { Content = "Приостановить", IsEnabled = cacheMonitor is not null };
        cacheActions.Children.Add(resumeCache); cacheActions.Children.Add(stopCache);
        resumeCache.Click += (_, _) =>
        {
            if (!store.Settings.AutomaticMailboxCache) { cacheState.Text = "Сначала включите автоматическое кэширование и сохраните настройки."; return; }
            cacheMonitor?.Resume(); RefreshCacheState();
        };
        stopCache.Click += async (_, _) => { stopCache.IsEnabled = false; try { if (cacheMonitor is not null) await cacheMonitor.PauseAsync(); RefreshCacheState(); } finally { stopCache.IsEnabled = true; } };
        cachePanel.Children.Add(Note("Во время поиска общий объём ещё неизвестен. После обхода папок показываются обработанные и оставшиеся найденные прочитанные письма, число сохранённых и ошибок. Пропущенные письма не входят в остаток. Уже сохранённые повторно не скачиваются; ошибки будут проверены при следующем обходе."));
        cachePanel.Children.Add(Label("Хранение и очистка"));
        cachePanel.Children.Add(Note("Кэш сохраняется между запусками в %LOCALAPPDATA%\\MailTrim\\ReaderCache, отдельно для каждого профиля и зашифрован средствами Windows. Лимит — 512 МБ на ящик. Обход зависит от того, какие папки и строки показывает сайт; это не подтверждённая резервная копия всего ящика."));
        var clearCache = new Button { Content = "Очистить кэш выбранного ящика…", HorizontalAlignment = HorizontalAlignment.Left }; cachePanel.Children.Add(clearCache);
        clearCache.Click += async (_, _) =>
        {
            if (cacheProfile.SelectedItem is not AccountProfile profile) return;
            if (MessageBox.Show(this, "Удалить локальные копии выбранного ящика? Письма на сервере останутся. Фоновая загрузка будет приостановлена до возобновления или следующего запуска.", "Очистка кэша", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            cacheOperation = true; IsEnabled = false;
            try
            {
                if (cacheMonitor is not null) await cacheMonitor.PauseAsync();
                if (beforeClear is not null) await beforeClear(profile.Id);
                new MessageCache(store.Root, profile.Id).Clear(); store.CacheProgress.Remove(profile.Id); RefreshCacheState();
            }
            catch { cacheState.Text = "Не удалось очистить кэш. Проверьте доступ к локальным файлам."; }
            finally { cacheOperation = false; IsEnabled = true; }
        };
        var cacheTab = new TabItem { Header = "Письма и кэш", Content = new ScrollViewer { Content = cachePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        tabs.Items.Insert(1, cacheTab); if (openCacheTab) tabs.SelectedItem = cacheTab;
        var rulePanel = new DockPanel { Margin = new Thickness(16) };
        var intro = Note("JSON-правила применяются без пересборки. allowedDomains имеет приоритет над blockedDomains. Домены включают поддомены. Для скрытия допустимы только CSS-селекторы. Произвольный загружаемый JavaScript не выполняется.");
        DockPanel.SetDock(intro, Dock.Top); rulePanel.Children.Add(intro);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,10,0,10) }; DockPanel.SetDock(buttons, Dock.Top); rulePanel.Children.Add(buttons);
        var import = new Button { Content = "Импорт JSON…" }; var reload = new Button { Content = "Прочитать локальный файл" }; var reset = new Button { Content = "Встроенные правила" };
        buttons.Children.Add(import); buttons.Children.Add(reload); buttons.Children.Add(reset);
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Правила JSON|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) LoadRules(dialog.FileName);
        };
        reload.Click += (_, _) => LoadRules(store.RulesPath);
        reset.Click += (_, _) => rules.Text = store.DefaultRules;
        rulePanel.Children.Add(rules); tabs.Items.Add(new TabItem { Header = "Правила", Content = rulePanel });
        save.Click += (_, _) =>
        {
            try
            {
                FilterRules.Parse(rules.Text);
                var repo = repository.Text.Trim();
                if (repo.Length > 0 && !UpdateChecker.ValidRepository(repo)) throw new FormatException("Репозиторий: owner/repository, без URL.");
                if (updates.IsChecked == true && repo.Length == 0) throw new FormatException("Укажите репозиторий для проверки обновлений.");
                RequiresReload = store.Settings.BlockRequests != (network.IsChecked == true)
                    || store.Settings.CosmeticFilters != (cosmetic.IsChecked == true)
                    || store.Settings.Aggressive != (aggressive.IsChecked == true)
                    || store.Settings.Theme != (string)theme.SelectedItem
                    || rules.Text != System.Text.Json.JsonSerializer.Serialize(store.Rules, FilterRules.Json);
                if (RequiresReload && MessageBox.Show(this, "Открытые страницы будут перезагружены. Сохраните незавершённые письма перед продолжением.", "Применить настройки", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                store.SaveRules(rules.Text);
                store.Settings.BlockRequests = network.IsChecked == true; store.Settings.CosmeticFilters = cosmetic.IsChecked == true; store.Settings.Aggressive = aggressive.IsChecked == true;
                store.Settings.Theme = (string)theme.SelectedItem; store.Settings.UpdateRepository = repo; store.Settings.CheckUpdatesOnStartup = updates.IsChecked == true; store.Settings.IncludePrereleaseUpdates = previewUpdates.IsChecked == true;
                WindowsStartup.SetEnabled(startup.IsChecked == true);
                store.Settings.StartWithWindows = startup.IsChecked == true; store.Settings.CloseToTray = tray.IsChecked == true; store.Settings.NotifyNewMail = notifications.IsChecked == true;
                store.Settings.AutomaticMailboxCache = automaticCache.IsChecked == true;
                store.Settings.UseSiteNotifications = siteNotifications.IsChecked == true;
                store.Save(); DialogResult = true;
            }
            catch (FormatException ex) { MessageBox.Show(this, ex.Message, "Проверьте настройки"); }
            catch (System.Text.Json.JsonException) { MessageBox.Show(this, "Некорректный JSON. Изменения не применены.", "Правила"); }
            catch (Exception) { MessageBox.Show(this, "Не удалось сохранить настройки. Проверьте доступ к локальным файлам."); }
        };
    }
    private void LoadRules(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 256_000) throw new FormatException();
            var text = File.ReadAllText(path); FilterRules.Parse(text); rules.Text = text;
        }
        catch { MessageBox.Show(this, "Не удалось прочитать правила. Проверьте JSON, размер и права доступа."); }
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,22,0,8) };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0,8,0,8) };
}


