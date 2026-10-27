using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailTrim.Core;

namespace MailTrim.App;

// Only validated models become controls here; the website remains in its own hidden surface.
public sealed class CloudPane : Grid, IDisposable
{
    private const string Home = "https://cloud.mail.ru/home/";
    private readonly CloudDataService data;
    private readonly Action showOriginal;
    private readonly Action showLogin;
    private readonly ListBox files = new() { Name = "CloudItems", BorderThickness = new Thickness(0) };
    private readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel folderLinks = new();
    private readonly TextBlock status = new() { Name = "CloudStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 5, 12, 8) };
    private readonly TextBlock title = new() { Name = "CloudFolderTitle", Text = "Облако", Margin = new Thickness(12, 8, 12, 0) };
    private readonly TextBox search = new() { Name = "CloudSearch", MaxLength = 200, MinWidth = 180, ToolTip = "Фильтр имён в загруженной папке", Margin = new Thickness(5) };
    private readonly TextBlock detail = new() { Margin = new Thickness(12, 6, 12, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock emptyNotice = new() { MaxWidth = 420, FontSize = 18, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), IsHitTestVisible = false };
    private readonly Button login = new() { Content = "Войти в Облако", Visibility = Visibility.Collapsed };
    private readonly Button more = new() { Content = "Ещё файлы", Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Stack<string> history = new();
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(45) };
    private CancellationTokenSource? request;
    private readonly CancellationTokenSource lifetime = new();
    private long generation;
    private bool active;
    private bool entryRefreshNeeded = true;
    private bool disposed;
    private string folderUrl = Home;
    private IReadOnlyList<CloudItem> items = [];
    public CloudListing? Listing { get; private set; }
    public IReadOnlyList<CloudItem> Items => items;
    public bool IsLoading { get; private set; }
    public Task BackgroundLoad { get; private set; } = Task.CompletedTask;
    public CloudItem? SelectedItem => files.SelectedItem as CloudItem;

    public CloudPane(BrowserSession session, Action showOriginal, Action showLogin)
    {
        data = new CloudDataService(session);
        this.showOriginal = showOriginal; this.showLogin = showLogin;
        SetResourceReference(BackgroundProperty, "Surface");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(14, 0, 14, 0), LastChildFill = false }; SetColumnSpan(header, 2); Children.Add(header);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var at = new TextBlock { Text = "@", FontSize = 32, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 5, 0) }; at.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        brand.Children.Add(at); var brandName = new TextBlock { Text = "облако", FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        brandName.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); brand.Children.Add(brandName); DockPanel.SetDock(brand, Dock.Left); header.Children.Add(brand);
        search.Width = 300; search.VerticalAlignment = VerticalAlignment.Center; search.ToolTip = "Поиск по именам в текущем списке";
        search.Tag = "Поиск в папке"; search.SetResourceReference(StyleProperty, "NativeSearchBox");
        System.Windows.Automation.AutomationProperties.SetName(search, "Поиск файлов в текущей папке");
        DockPanel.SetDock(search, Dock.Right); header.Children.Add(search);
        var navigation = new DockPanel(); navigation.SetResourceReference(BackgroundProperty, "Surface"); SetRow(navigation, 1);
        var navHeader = new TextBlock { Text = "РАЗДЕЛЫ", Margin = new Thickness(12, 14, 8, 12), FontWeight = FontWeights.SemiBold };
        navHeader.SetResourceReference(StyleProperty, "NativeMetadata"); DockPanel.SetDock(navHeader, Dock.Top); navigation.Children.Add(navHeader);
        var navTools = new StackPanel(); DockPanel.SetDock(navTools, Dock.Top); navigation.Children.Add(navTools);
        var all = Tool("📁 Все файлы", async () => await OpenFolder(Home)); all.HorizontalContentAlignment = HorizontalAlignment.Left; navTools.Children.Add(all);
        var foldersLabel = new TextBlock { Text = "ПАПКИ ТЕКУЩЕГО СПИСКА", Margin = new Thickness(12, 18, 8, 8), FontSize = 10, TextWrapping = TextWrapping.Wrap };
        foldersLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); navTools.Children.Add(foldersLabel);
        navigation.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = folderLinks });
        Children.Add(navigation);
        var card = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 12) }; card.SetResourceReference(Border.BackgroundProperty, "Panel");
        SetColumn(card, 1); SetRow(card, 1); Children.Add(card); var content = new DockPanel(); card.Child = content;
        DockPanel.SetDock(progress, Dock.Top); content.Children.Add(progress);
        var toolbar = new DockPanel { Margin = new Thickness(5), LastChildFill = false }; DockPanel.SetDock(toolbar, Dock.Top); content.Children.Add(toolbar);
        var commands = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(commands, Dock.Left); toolbar.Children.Add(commands);
        commands.Children.Add(Tool("←", GoBack)); commands.Children.Add(Tool("↑", Up)); commands.Children.Add(Tool("Обновить", Refresh));
        more.SetResourceReference(StyleProperty, "NativeToolbarButton"); more.Click += async (_, _) => await LoadMore(); commands.Children.Add(more);
        var original = Tool("Оригинал ↗", () => { this.showOriginal(); return Task.CompletedTask; });
        original.ToolTip = "Открыть сайт явно: загрузка, скачивание и другие операции с файлами";
        DockPanel.SetDock(original, Dock.Right); toolbar.Children.Add(original);
        search.TextChanged += (_, _) => RenderRows();
        title.SetResourceReference(StyleProperty, "NativeTitle"); DockPanel.SetDock(title, Dock.Top); content.Children.Add(title);
        var crumbs = new ScrollViewer { Content = breadcrumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(8, 2, 8, 2) };
        DockPanel.SetDock(crumbs, Dock.Top); content.Children.Add(crumbs);
        status.SetResourceReference(StyleProperty, "NativeMetadata"); DockPanel.SetDock(status, Dock.Top); content.Children.Add(status);
        login.Click += (_, _) => this.showLogin(); DockPanel.SetDock(login, Dock.Top); content.Children.Add(login);
        detail.SetResourceReference(StyleProperty, "NativeMetadata"); DockPanel.SetDock(detail, Dock.Bottom); content.Children.Add(detail);
        var columns = new Grid { Margin = new Thickness(13, 4, 25, 4) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        foreach (var (label, index) in new[] { ("Имя · папки сначала, А—Я", 0), ("Размер", 1), ("Изменено", 2) })
        {
            var text = new TextBlock { Text = label }; text.SetResourceReference(StyleProperty, "NativeMetadata"); SetColumn(text, index); columns.Children.Add(text);
        }
        DockPanel.SetDock(columns, Dock.Top); content.Children.Add(columns);
        files.SetResourceReference(StyleProperty, "NativeList"); files.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NativeListItem");
        files.SetResourceReference(Control.BackgroundProperty, "Panel"); files.SetResourceReference(Control.ForegroundProperty, "Ink");
        files.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid Margin="3,4">
         <Grid.ColumnDefinitions><ColumnDefinition Width="30"/><ColumnDefinition Width="*"/><ColumnDefinition Width="110"/><ColumnDefinition Width="170"/></Grid.ColumnDefinitions>
         <TextBlock VerticalAlignment="Center"><TextBlock.Style><Style TargetType="TextBlock"><Setter Property="Text" Value="📄"/><Style.Triggers><DataTrigger Binding="{Binding IsFolder}" Value="True"><Setter Property="Text" Value="📁"/></DataTrigger></Style.Triggers></Style></TextBlock.Style></TextBlock>
         <TextBlock Grid.Column="1" Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" ToolTip="{Binding Name}" Margin="0,0,10,0"/>
         <TextBlock Grid.Column="2" Text="{Binding Size}" Foreground="{DynamicResource Muted}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
         <TextBlock Grid.Column="3" Text="{Binding Modified}" Foreground="{DynamicResource Muted}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
        </Grid></DataTemplate>
        """);
        files.MouseDoubleClick += async (_, _) => await OpenSelected();
        files.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelected(); } };
        files.SelectionChanged += (_, _) => detail.Text = SelectedItem is { } item ? $"{(item.IsFolder ? "Папка" : "Файл")}: {item.Name}" + (item.IsFolder ? " · Enter — открыть" : " · Для просмотра и действий используйте «Оригинал»") : "";
        var fileArea = new Grid(); fileArea.Children.Add(files); emptyNotice.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); fileArea.Children.Add(emptyNotice); content.Children.Add(fileArea);
        timer.Tick += async (_, _) => { if (active && !IsLoading && !disposed) await SyncCurrentPage(); };
    }

    private static Button Tool(string text, Func<Task> action)
    {
        var button = new Button { Content = text }; button.SetResourceReference(StyleProperty, "NativeToolbarButton");
        button.Click += async (_, _) => await action(); return button;
    }
    public void FocusSearch() => search.Focus();
    public void SetActive(bool value)
    {
        if (value && !active) entryRefreshNeeded = true;
        active = value; Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value) timer.Start(); else { timer.Stop(); request?.Cancel(); }
    }
    public Task Start(string? url = null)
    {
        if (disposed) return Task.CompletedTask;
        if (url is null && Listing is not null && !entryRefreshNeeded) return BackgroundLoad;
        if (url is null && Listing is { Ready: true }) return BackgroundLoad = SyncCurrentPage();
        return BackgroundLoad = Load(url ?? folderUrl, false, false);
    }
    public Task Refresh() => BackgroundLoad = Load(folderUrl, true, false);
    public Task OpenFolder(string url) => BackgroundLoad = Load(url, false, true);
    public async Task GoBack()
    {
        if (history.Count == 0) return;
        await Load(history.Pop(), false, false);
    }
    private Task Up() => OpenFolder(CloudData.ParentUrl(folderUrl));
    private Task OpenSelected() => SelectedItem is { IsFolder: true } item ? OpenFolder(item.Url) : Task.CompletedTask;
    private async Task SyncCurrentPage()
    {
        if (disposed || IsLoading) return;
        var currentGeneration = ++generation;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = pending; IsLoading = true;
        try
        {
            var result = await data.Snapshot(pending.Token);
            if (disposed || pending.IsCancellationRequested || generation != currentGeneration) return;
            entryRefreshNeeded = false;
            if (result.AuthRequired)
            {
                Listing = result; items = []; login.Visibility = Visibility.Visible; more.Visibility = Visibility.Collapsed;
                status.Text = result.Message; emptyNotice.Text = "Войдите в Облако через официальный сайт"; RenderRows(); RenderNavigation(); return;
            }
            if (!result.Ready || result.FolderUrl != folderUrl) { login.Content = "Проверить вход"; login.Visibility = Visibility.Visible; status.Text = "Список сейчас не подтверждён сайтом. Нажмите «Обновить» для проверки."; return; }
            items = result.Partial
                ? items.Concat(result.Items).GroupBy(item => item.Url, StringComparer.Ordinal).Select(group => group.Last()).Take(2000).ToArray()
                : result.Items;
            Listing = result with { Items = items.ToList() }; more.Visibility = result.Partial ? Visibility.Visible : Visibility.Collapsed; login.Visibility = Visibility.Collapsed;
            emptyNotice.Text = items.Count == 0 && result.Empty ? "В папке пока нет файлов" : "";
            status.Text = $"Загружено: {items.Count}. " + (result.Partial ? "Показана доступная часть списка. " : "") + "Считано: " + DateTime.Now.ToString("HH:mm:ss");
            RenderRows(true); RenderNavigation();
        }
        catch (OperationCanceledException) { if (generation == currentGeneration) entryRefreshNeeded = true; }
        catch (Exception) { if (!disposed && generation == currentGeneration) status.Text = "Не удалось обновить текущую страницу. Ранее прочитанные записи сохранены."; }
        finally { if (ReferenceEquals(request, pending)) request = null; if (generation == currentGeneration) IsLoading = false; }
    }
    private async Task Load(string url, bool refresh, bool remember)
    {
        if (disposed) return;
        var currentGeneration = ++generation; request?.Cancel(); request?.Dispose();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = pending;
        IsLoading = true; progress.Visibility = Visibility.Visible; status.Text = "Обновляю список файлов…";
        if (items.Count == 0) emptyNotice.Text = "Загрузка файлов…";
        var oldUrl = folderUrl;
        try
        {
            var result = await data.LoadFolder(url, refresh, pending.Token);
            if (disposed || pending.IsCancellationRequested || generation != currentGeneration) return;
            if (remember && result.Ready && Listing is { Ready: true } && oldUrl != result.FolderUrl) history.Push(oldUrl);
            Listing = result; entryRefreshNeeded = false; if (CloudData.IsFolderUrl(result.FolderUrl)) folderUrl = result.FolderUrl;
            title.Text = string.IsNullOrWhiteSpace(result.FolderName) || result.FolderName == "Файлы" ? CloudData.FolderPath(folderUrl).Last().Name : result.FolderName;
            items = result.Ready ? result.Items : [];
            emptyNotice.Text = items.Count > 0 ? "" : result.Empty ? "В папке пока нет файлов" : result.AuthRequired ? "Войдите в Облако через официальный сайт" : "Список файлов пока недоступен\nНажмите «Обновить»";
            more.Visibility = result.Ready && result.Partial ? Visibility.Visible : Visibility.Collapsed;
            login.Content = result.AuthRequired ? "Войти в Облако" : "Проверить вход"; login.Visibility = result.Ready ? Visibility.Collapsed : Visibility.Visible;
            status.Text = result.Ready ? (result.Empty ? "Папка пуста. " : $"Загружено: {items.Count}. " + (result.Partial ? "Показана доступная часть списка. " : "")) + "Обновлено: " + DateTime.Now.ToString("HH:mm:ss")
                : string.IsNullOrWhiteSpace(result.Message) ? "Список пока недоступен. Обновите его или войдите в Облако." : result.Message;
            RenderRows(oldUrl == folderUrl); RenderNavigation();
        }
        catch (OperationCanceledException) { if (generation == currentGeneration) entryRefreshNeeded = true; }
        catch (Exception) { if (!disposed && generation == currentGeneration) { status.Text = "Не удалось прочитать папку. Обновите список или откройте оригинал явно."; } }
        finally
        {
            if (ReferenceEquals(request, pending)) request = null;
            if (!disposed && generation == currentGeneration) { IsLoading = false; progress.Visibility = Visibility.Collapsed; }
        }
    }
    public async Task LoadMore()
    {
        if (disposed || IsLoading || Listing is not { Ready: true, Partial: true }) return;
        var currentGeneration = ++generation; request?.Cancel(); request?.Dispose();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = pending;
        IsLoading = true; progress.Visibility = Visibility.Visible; more.IsEnabled = false;
        try
        {
            var result = await data.LoadMore(pending.Token);
            if (disposed || pending.IsCancellationRequested || generation != currentGeneration) return;
            if (!result.Ready || result.AuthRequired)
            {
                Listing = result; items = []; more.Visibility = Visibility.Collapsed; login.Content = result.AuthRequired ? "Войти в Облако" : "Проверить вход"; login.Visibility = Visibility.Visible;
                status.Text = result.Message; emptyNotice.Text = result.AuthRequired ? "Войдите в Облако через официальный сайт" : "Список файлов пока недоступен";
                RenderRows(); RenderNavigation(); return;
            }
            if (result.FolderUrl != folderUrl) return;
            var before = items.Count;
            items = items.Concat(result.Items).GroupBy(item => item.Url, StringComparer.Ordinal).Select(group => group.Last()).Take(2000).ToArray();
            Listing = result with { Items = items.ToList(), Partial = true };
            status.Text = $"Загружено: {items.Count}. Показана доступная часть списка. " + (items.Count == before ? "Новых строк пока нет." : "");
            RenderRows(true); RenderNavigation();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!disposed && generation == currentGeneration) status.Text = "Не удалось получить следующую часть списка. Можно повторить попытку."; }
        finally
        {
            if (ReferenceEquals(request, pending)) request = null;
            if (!disposed && generation == currentGeneration) { IsLoading = false; progress.Visibility = Visibility.Collapsed; more.IsEnabled = true; }
        }
    }
    private void RenderRows(bool preservePosition = false)
    {
        var selected = SelectedItem?.Url;
        var scroll = Scroller(files); var offset = scroll?.VerticalOffset ?? 0;
        var oldRows = files.Items.Cast<CloudItem>().ToArray(); var anchor = oldRows.ElementAtOrDefault(Math.Clamp((int)offset, 0, Math.Max(0, oldRows.Length - 1)))?.Url;
        var rendered = items.Where(item => item.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        files.ItemsSource = rendered;
        files.SelectedItem = files.Items.Cast<CloudItem>().FirstOrDefault(item => item.Url == selected);
        if (preservePosition) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (disposed || !ReferenceEquals(files.ItemsSource, rendered)) return;
            var index = Array.FindIndex(rendered, item => item.Url == anchor);
            Scroller(files)?.ScrollToVerticalOffset(index >= 0 ? index + offset % 1 : offset);
        }));
    }
    private static ScrollViewer? Scroller(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (Scroller(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    private void RenderNavigation()
    {
        breadcrumbs.Children.Clear(); folderLinks.Children.Clear();
        var path = Listing?.Breadcrumbs is { Count: > 0 } provided ? provided : CloudData.FolderPath(folderUrl);
        foreach (var folder in path) breadcrumbs.Children.Add(Tool(folder.Name, () => OpenFolder(folder.Url)));
        foreach (var folder in items.Where(item => item.IsFolder).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var link = Tool("📁 " + folder.Name, () => OpenFolder(folder.Url)); link.HorizontalContentAlignment = HorizontalAlignment.Left; folderLinks.Children.Add(link);
        }
    }
    public async Task StopAsync()
    {
        timer.Stop(); request?.Cancel();
        try { await BackgroundLoad; } catch (OperationCanceledException) { }
        await data.StopAsync();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; active = false; timer.Stop(); lifetime.Cancel(); request?.Cancel(); data.Dispose();
        items = []; files.ItemsSource = null; folderLinks.Children.Clear(); breadcrumbs.Children.Clear(); Listing = null; lifetime.Dispose();
    }
}
