using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using MailTrim.App;
using MailTrim.Core;
using MailTrim.Core.Gmail;

namespace MailTrim.Smoke;

/// <summary>Mailbox layout checks use only synthetic messages, bitmap data and an in-memory API.</summary>
internal static class GmailLayoutChecks
{
    public static async Task Run(LocalStore store, Grid host, Action<bool, string> check)
    {
        var window = Window.GetWindow(host)!;
        var oldWidth = window.Width; var oldHeight = window.Height;
        var oldAutoCache = store.Settings.AutomaticMailboxCache;
        var oldAutoImages = store.Settings.AutomaticallyLoadReaderImages;
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Line", "Accent", "ServiceSelected" };
        var resources = Application.Current.Resources;
        var originalResources = keys.ToDictionary(key => key, key => resources[key]);
        var profile = new AccountProfile { Name = "Synthetic Gmail layout", Provider = MailProvider.Gmail };
        new GmailVault(store.Root, profile.Id).Save("credentials", new GmailCredentials(
            "layout-fixture.apps.googleusercontent.com", "layout-secret", "layout-refresh", "layout-access", DateTimeOffset.UtcNow.AddHours(1)));
        var fixture = new MailboxFixture();
        store.Settings.AutomaticMailboxCache = false; store.Settings.AutomaticallyLoadReaderImages = false;
        using var pane = new GmailPane(store, profile, (_, _) => { }, _ => { }, fixture);
        host.Children.Add(pane);
        try
        {
            window.Width = 1280; window.Height = 860;
            await pane.Ready; await Layout(pane);
            var folders = Named<ListBox>(pane, "GmailFolders");
            var messages = Named<ListBox>(pane, "GmailMessages");
            check(folders.Items.OfType<GmailLabel>().Any(folder => folder.Id == "Label_projects"),
                "Gmail's styled folder navigation retains custom labels from the API");
            folders.SelectedItem = folders.Items.OfType<GmailLabel>().Single(folder => folder.Id == "Label_projects");
            await WaitFor(pane, () => fixture.RequestedProjects && messages.Items.Count == 3);
            messages.SelectedIndex = 0;
            await WaitFor(pane, () => Descendants<TextBox>(pane).Any(text => text.IsReadOnly && text.Text.Contains("Первый абзац", StringComparison.Ordinal)));
            var body = Named<StackPanel>(pane, "GmailReadingBody");
            check(body.MaxWidth <= GmailReadingView.MaxContentWidth && body.HorizontalAlignment == HorizontalAlignment.Left,
                "Gmail reading uses a bounded left-aligned text column rather than stretching paragraphs across a wide monitor");

            var first = messages.Items.OfType<GmailMessage>().First();
            var second = messages.Items.OfType<GmailMessage>().Skip(1).First();
            var firstRow = (ListBoxItem)messages.ItemContainerGenerator.ContainerFromItem(first)!;
            var secondRow = (ListBoxItem)messages.ItemContainerGenerator.ContainerFromItem(second)!;
            var rowText = Descendants<TextBlock>(firstRow).ToArray();
            var sender = rowText.Single(text => text.Text == GmailReadingView.SenderName(first.From));
            var subject = rowText.Single(text => text.Text == first.Subject);
            var preview = rowText.Single(text => text.Text == first.Preview);
            var date = rowText.Single(text => text.Text == GmailReadingView.CompactDate(first.ReceivedAt));
            check(sender != subject && subject != preview && preview != date
                && subject.TranslatePoint(new Point(), firstRow).Y > sender.TranslatePoint(new Point(), firstRow).Y
                && preview.TranslatePoint(new Point(), firstRow).Y > subject.TranslatePoint(new Point(), firstRow).Y,
                "Gmail message rows show separate sender, subject, preview and compact date with a clear vertical hierarchy");
            check(preview.TextTrimming == TextTrimming.CharacterEllipsis && preview.TextWrapping == TextWrapping.NoWrap
                && !rowText.Any(text => text.Text == first.From),
                "long Gmail previews are ellipsized while the sender address remains out of the compact display name");
            var readSender = Descendants<TextBlock>(secondRow).Single(text => text.Text == GmailReadingView.SenderName(second.From));
            var unreadMark = Descendants<TextBlock>(firstRow).Any(text => text.IsVisible && text.Text is "●" or "•")
                || Descendants<Ellipse>(firstRow).Any(dot => dot.IsVisible && dot.ActualWidth > 0);
            check(unreadMark && sender.FontWeight.ToOpenTypeWeight() > readSender.FontWeight.ToOpenTypeWeight(),
                "unread Gmail has a visible marker and stronger sender weight than a read message");

            var normalized = GmailReadingView.NormalizeText("Первый абзац\r\n\r\n\r\n\r\nВторой абзац\r\n    строка с отступом\r\n");
            check(normalized.Contains("Первый абзац", StringComparison.Ordinal)
                && normalized.Contains("Второй абзац", StringComparison.Ordinal)
                && normalized.Contains("    строка с отступом", StringComparison.Ordinal)
                && !normalized.Contains("\r", StringComparison.Ordinal)
                && !normalized.Contains("\n\n\n", StringComparison.Ordinal),
                "Gmail presentation collapses excessive blank lines without losing paragraphs or indentation");

            var logo = GmailReadingView.CreateImage(Bitmap(120, 40, 96));
            var photograph = GmailReadingView.CreateImage(Bitmap(2400, 800, 96));
            var lowDpiLogo = GmailReadingView.CreateImage(Bitmap(120, 40, 24));
            body.Children.Add(logo); body.Children.Add(photograph); body.Children.Add(lowDpiLogo);
            await Layout(pane);
            check(logo.ActualWidth <= 120.1 && logo.ActualHeight <= 40.1
                && lowDpiLogo.ActualWidth <= 120.1 && lowDpiLogo.ActualHeight <= 40.1,
                "small Gmail logos retain natural pixel dimensions even when source DPI would otherwise enlarge them");
            check(logo.Stretch == Stretch.Uniform && logo.StretchDirection == StretchDirection.DownOnly
                && photograph.ActualWidth <= GmailReadingView.MaxContentWidth + 1
                && photograph.ActualWidth > 0 && Math.Abs(photograph.ActualWidth / photograph.ActualHeight - 3) < .02,
                "large Gmail images shrink into the reading column without distortion or upscaling");
            Capture(pane, Path.Combine(store.Root, "gmail-layout-light-1280.png"));

            var toggle = Named<Button>(pane, "FolderToggleButton");
            var togglePosition = toggle.TranslatePoint(new Point(), pane);
            var selectedFolder = ((GmailLabel)folders.SelectedItem).Id;
            var selectedMessage = ((GmailMessage)messages.SelectedItem).Id;
            var getCount = fixture.GetCount;
            pane.ToggleFolders(); await Layout(pane);
            check(pane.FoldersCollapsed && Math.Abs(pane.ColumnDefinitions[0].ActualWidth - 48) < 1
                && Near(togglePosition, toggle.TranslatePoint(new Point(), pane))
                && ((GmailLabel)folders.SelectedItem).Id == selectedFolder && ((GmailMessage)messages.SelectedItem).Id == selectedMessage
                && fixture.GetCount == getCount,
                "collapsing Gmail folders keeps the toggle fixed and preserves folder, message and network state");
            Capture(pane, Path.Combine(store.Root, "gmail-layout-collapsed-1280.png"));
            pane.ToggleFolders(); await Layout(pane);
            check(!pane.FoldersCollapsed && Near(togglePosition, toggle.TranslatePoint(new Point(), pane)),
                "expanding Gmail folders returns to the same fixed toggle position");

            window.Width = 1000; window.Height = 760; await Layout(pane);
            var narrowWidth = photograph.ActualWidth;
            check(narrowWidth > 0 && narrowWidth <= body.ActualWidth + 1 && body.ActualWidth <= GmailReadingView.MaxContentWidth + 1,
                "Gmail reading and photos stay inside a compact window after resizing");
            CheckListWidth(messages, check);
            Capture(pane, Path.Combine(store.Root, "gmail-layout-light-1000.png"));

            window.Width = 2560; window.Height = 1000; await Layout(pane);
            check(body.ActualWidth <= GmailReadingView.MaxContentWidth + 1 && photograph.ActualWidth <= GmailReadingView.MaxContentWidth + 1
                && photograph.ActualWidth >= narrowWidth && logo.ActualWidth <= 120.1,
                "wide Gmail windows improve photo space without stretching text or enlarging logos");
            CheckListWidth(messages, check);
            Capture(pane, Path.Combine(store.Root, "gmail-layout-light-2560.png"));

            var dark = new[] { "#111827", "#182235", "#E6EDF7", "#9DADC5", "#2D3C54", "#6FA8FF", "#163C66" };
            for (int index = 0; index < keys.Length; index++) resources[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark[index]));
            await Layout(pane);
            var bodyText = Descendants<TextBox>(body).Single(text => text.IsReadOnly && text.Text.Contains("Первый абзац", StringComparison.Ordinal));
            check(bodyText.Foreground is SolidColorBrush foreground && foreground.Color == (Color)ColorConverter.ConvertFromString(dark[2])
                && messages.Foreground is SolidColorBrush listForeground && listForeground.Color == foreground.Color,
                "Gmail message text and list follow theme changes without retaining unreadable light-theme colors");
            Capture(pane, Path.Combine(store.Root, "gmail-layout-dark-2560.png"));
            window.Width = 1280; window.Height = 860; await Layout(pane);
            Capture(pane, Path.Combine(store.Root, "gmail-layout-dark-1280.png"));

            Named<Button>(pane, "GmailComposeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(pane, () => pane.HasDraft);
            var draft = Descendants<TextBox>(body).Single(text => text.AcceptsReturn && !text.IsReadOnly);
            draft.Text = "Локальный черновик макета";
            pane.ToggleFolders(); await Layout(pane);
            check(pane.HasDraft && draft.Text == "Локальный черновик макета" && fixture.PostCount == 0,
                "Gmail folder layout changes preserve the local compose draft and never send or mutate messages");
        }
        finally
        {
            host.Children.Remove(pane);
            foreach (var entry in originalResources) resources[entry.Key] = entry.Value;
            store.Settings.AutomaticMailboxCache = oldAutoCache; store.Settings.AutomaticallyLoadReaderImages = oldAutoImages;
            window.Width = oldWidth; window.Height = oldHeight; host.UpdateLayout();
        }
    }

    private static void CheckListWidth(ListBox messages, Action<bool, string> check)
    {
        var scroll = Descendants<ScrollViewer>(messages).First();
        check(ScrollViewer.GetHorizontalScrollBarVisibility(messages) == ScrollBarVisibility.Disabled && scroll.ScrollableWidth <= 1,
            "the Gmail list fits its pane without a horizontal scrollbar for long subjects or addresses");
    }
    private static bool Near(Point first, Point second) => Math.Abs(first.X - second.X) < 1 && Math.Abs(first.Y - second.Y) < 1;
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants<T>(root).Single(element => element.Name == name);
    private static async Task Layout(FrameworkElement element)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Window.GetWindow(element)?.UpdateLayout(); element.UpdateLayout();
    }
    private static async Task WaitFor(FrameworkElement element, Func<bool> ready)
    {
        for (int attempt = 0; attempt < 150 && !ready(); attempt++) { await Task.Delay(20); await Layout(element); }
        await Layout(element);
        if (!ready()) throw new InvalidOperationException("Gmail layout fixture did not settle");
    }
    private static BitmapSource Bitmap(int width, int height, double dpi)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            pixels[pixel * 4] = 225;
            pixels[pixel * 4 + 1] = (byte)(70 + (pixel / width / 16 % 2) * 30);
            pixels[pixel * 4 + 2] = 35;
            pixels[pixel * 4 + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }
    private static void Capture(FrameworkElement element, string path)
    {
        Window.GetWindow(element)?.UpdateLayout(); element.UpdateLayout();
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) throw new InvalidOperationException("Gmail layout capture requires an arranged pane");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class MailboxFixture : HttpMessageHandler
    {
        public int GetCount { get; private set; }
        public int PostCount { get; private set; }
        public bool RequestedProjects { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host != "gmail.googleapis.com") throw new InvalidOperationException("unexpected_synthetic_origin");
            if (request.Method != HttpMethod.Get) { PostCount++; throw new InvalidOperationException("unexpected_mail_mutation_in_layout_check"); }
            GetCount++;
            var path = request.RequestUri.PathAndQuery;
            RequestedProjects |= path.Contains("labelIds=Label_projects", StringComparison.Ordinal);
            string json;
            if (path.EndsWith("/profile", StringComparison.Ordinal)) json = "{\"emailAddress\":\"layout@example.test\"}";
            else if (path.Contains("/labels/INBOX", StringComparison.Ordinal)) json = "{\"messagesUnread\":1}";
            else if (path.EndsWith("/labels", StringComparison.Ordinal)) json = "{\"labels\":[{\"id\":\"Label_projects\",\"name\":\"Проекты\"},{\"id\":\"Label_receipts\",\"name\":\"Покупки и электронные чеки\"}]}";
            else if (request.RequestUri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)) json = "{\"messages\":[{\"id\":\"layout-first\"},{\"id\":\"layout-second\"},{\"id\":\"layout-third\"}]}";
            else
            {
                var id = request.RequestUri.AbsolutePath.Split('/').Last();
                var first = id == "layout-first";
                var second = id == "layout-second";
                var from = first ? "\"Команда проекта\" <updates@example.test>" : second ? "Служба поддержки <support@example.test>" : "Чеки <receipt@example.test>";
                var subject = first ? "Новый макет готов — проверьте письмо и вложенные материалы" : second ? "Ответ на ваш вопрос о доставке" : "Электронный чек за покупку";
                var preview = first ? "Краткое описание с длинной строкой, которая должна аккуратно обрезаться в компактном списке писем." : second ? "Заказ подготовлен к выдаче, подробности находятся в письме." : "Ваш электронный чек доступен для скачивания.";
                var text = "Первый абзац: письмо удобно читать в ограниченной по ширине колонке.\n\n\n\n\nВторой абзац остаётся на месте, а лишние пустые строки убираются только при показе.\n\nСпасибо за внимание!";
                json = JsonSerializer.Serialize(new
                {
                    id, threadId = "thread-" + id, snippet = preview,
                    internalDate = DateTimeOffset.UtcNow.AddMinutes(first ? -5 : second ? -10 : -15).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    labelIds = first ? new[] { "INBOX", "Label_projects", "UNREAD" } : new[] { "INBOX", "Label_projects" },
                    payload = new
                    {
                        mimeType = "text/plain",
                        headers = new[] { new { name = "From", value = from }, new { name = "To", value = "layout@example.test" }, new { name = "Subject", value = subject } },
                        body = new { data = GmailMime.Base64Url(Encoding.UTF8.GetBytes(text)) }
                    }
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
