using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailTrim.Core;
using Microsoft.Win32;

namespace MailTrim.App;

public sealed class SettingsWindow : Window
{
    private readonly LocalStore store;
    public bool RequiresReload { get; private set; }
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
    public SettingsWindow(LocalStore store)
    {
        this.store = store;
        Title = "Настройки MailTrim"; Width = 820; Height = 780; MinWidth = 640; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        main.Children.Add(Label("Репозиторий обновлений: owner/repository")); main.Children.Add(repository); main.Children.Add(updates); main.Children.Add(previewUpdates);
        var check = new Button { Content = "Проверить версию сейчас", HorizontalAlignment = HorizontalAlignment.Left };
        check.Click += async (_, _) => { check.IsEnabled = false; try { await UpdateChecker.Check(this, store, false, repository.Text.Trim(), previewUpdates.IsChecked == true); } finally { check.IsEnabled = true; } };
        main.Children.Add(check); main.Children.Add(Note("Новая версия открывается на GitHub после подтверждения. Автоматической установки и выполнения загруженных файлов нет."));
        main.Children.Add(Label("Локальные данные"));
        main.Children.Add(Note("Сессии хранятся в %LOCALAPPDATA%\\MailTrim\\Profiles. Журнал содержит только время и коды событий, без адресов, URL, заголовков и содержимого писем."));
        tabs.Items.Add(new TabItem { Header = "Общие", Content = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var desktopPanel = new StackPanel { Margin = new Thickness(16) };
        desktopPanel.Children.Add(Label("Запуск и фоновая работа"));
        desktopPanel.Children.Add(startup); desktopPanel.Children.Add(tray); desktopPanel.Children.Add(notifications);
        desktopPanel.Children.Add(siteNotifications);
        desktopPanel.Children.Add(Note("По умолчанию новые письма проверяются раз в 2 минуты. В режиме сайта разрешены браузерные уведомления только от e.mail.ru; оповещения от опроса отключены, чтобы не дублировать их. В уведомлении сайта могут быть отправитель и тема письма."));
        desktopPanel.Children.Add(Note("Для режима сайта откройте вкладку каждого нужного ящика и включите уведомления в настройках самой почты, если они отключены. После смены режима обновите страницу. Приложение должно оставаться запущенным, в том числе в трее. Если сайт не присылает уведомления, вернитесь к обычной проверке."));
        desktopPanel.Children.Add(Note("Значок рядом с часами: двойной щелчок открывает окно, «Выйти» полностью закрывает приложение. Автозапуск привязан к текущему расположению EXE; после переноса приложения включите его заново."));
        desktopPanel.Children.Add(Note("Проверяются входящие через отдельную веб-сессию того же профиля, без открытия писем. Первый успешный опрос задаёт исходный список и не уведомляет о старой почте. Нужны интернет и действующий вход Mail.ru/VK ID. Оповещения от опроса не содержат текстов писем; Windows может скрывать их в режиме «Не беспокоить»."));
        tabs.Items.Add(new TabItem { Header = "Windows и уведомления", Content = new ScrollViewer { Content = desktopPanel } });
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


