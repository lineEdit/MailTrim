using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailTrim.App;
using MailTrim.Core;

namespace MailTrim.Smoke;

internal static class ImageChecks
{
    public static async Task Run(BrowserSession session, Panel host, Action<bool, string> check)
    {
        var pixels = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var data = new MemoryStream(); encoder.Save(data);
        var handler = new ImageHandler(data.ToArray());
        var loader = new ReaderImageLoader(handler);
        var preference = session.Store.Settings.AutomaticallyLoadReaderImages;
        check(!preference, "automatic images are disabled by default");
        var pane = new ReaderPane(session, () => { }, loader); host.Children.Add(pane);
        try
        {
            await pane.Start(); await pane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(12));
            var list = ((DockPanel)pane.Children[0]).Children.OfType<ListBox>().Single();
            var body = (StackPanel)((ScrollViewer)((DockPanel)pane.Children[1]).Children[1]).Content;
            var letter = list.Items.Cast<ReaderLetter>().First();
            var blocks = new List<ReaderBlock> { new("Immediate clean text", "") };
            blocks.AddRange(Enumerable.Range(0, 15).Select(i => new ReaderBlock("Picture " + i, "https://images.invalid/" + i)));
            session.ReaderData.Prepared.Put(new ReaderDocument(letter, blocks, false, true), DateTimeOffset.UtcNow);
            list.SelectedIndex = -1; list.SelectedIndex = 0;
            check(handler.Calls == 0 && body.Children.OfType<Button>().Count() == 15, "manual image preference makes no HTTP request");
            body.Children.OfType<Button>().First().RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && !body.Children.OfType<Image>().Any(); i++) await Task.Delay(20);
            check(handler.Calls == 1 && body.Children.OfType<Image>().Count() == 1, "manual click decodes a cookie-free image from the injected HTTP handler");
            session.Store.Settings.AutomaticallyLoadReaderImages = true; session.Store.Save();
            check(new LocalStore(session.Store.Root).Settings.AutomaticallyLoadReaderImages, "automatic image preference persists between application instances");
            list.SelectedIndex = -1; list.SelectedIndex = 0;
            check(body.Children.OfType<TextBlock>().Any(t => t.Text == "Immediate clean text") && !pane.BackgroundImages.IsCompleted, "automatic images do not delay the clean message text");
            await pane.BackgroundImages.WaitAsync(TimeSpan.FromSeconds(5));
            check(handler.Calls == 13 && handler.Maximum == 2 && body.Children.OfType<Image>().Count() == 12 && body.Children.OfType<Button>().Count() == 3, "automatic images are capped at twelve with two concurrent requests; others stay manual");
            handler.Timeout = true;
            list.SelectedIndex = -1; list.SelectedIndex = 0;
            await pane.BackgroundImages.WaitAsync(TimeSpan.FromSeconds(5));
            check(body.Children.OfType<Button>().Count(x => x.IsEnabled && x.Content.ToString()!.Contains("повторить")) == 12, "image timeout returns retry buttons instead of leaving loading indicators stuck");
            handler.Timeout = false;
            handler.Delay = TimeSpan.FromSeconds(10);
            list.SelectedIndex = -1; list.SelectedIndex = 0;
            var tasks = pane.BackgroundImages;
            pane.Close(); await tasks.WaitAsync(TimeSpan.FromSeconds(2));
            check(handler.Cancelled >= 2 && body.Children.Count == 0, "closing the reader cancels active and queued image requests without late rendering");
        }
        finally { pane.Close(); host.Children.Remove(pane); session.Store.Settings.AutomaticallyLoadReaderImages = preference; session.Store.Save(); }

        using var boundsHandler = new ImageHandler(data.ToArray()); using var bounded = new ReaderImageLoader(boundsHandler);
        var unsafeRejected = false;
        try { await bounded.Load(new Uri("http://images.invalid/plain"), default); } catch (IOException) { unsafeRejected = true; }
        check(unsafeRejected && boundsHandler.Calls == 0, "image loader rejects insecure origins before requesting");
        boundsHandler.Size = 8_000_001;
        var largeRejected = false;
        try { await bounded.Load(new Uri("https://images.invalid/large"), default); } catch (IOException) { largeRejected = true; }
        check(largeRejected, "image loader rejects oversized HTTP content before decoding");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var stopped = false;
        try { await bounded.Load(new Uri("https://images.invalid/cancel"), cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped && boundsHandler.Calls == 1, "cancelled image load never reaches HTTP handler");
    }

    private sealed class ImageHandler(byte[] png) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Maximum { get; private set; }
        public int Cancelled { get; private set; }
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(100);
        public long? Size { get; set; }
        public bool Timeout { get; set; }
        private int active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Maximum = Math.Max(Maximum, ++active);
            try
            {
                await Task.Delay(Delay, token);
                if (Timeout) throw new TaskCanceledException("Synthetic HTTP timeout");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
                if (Size is { } size) response.Content.Headers.ContentLength = size;
                return response;
            }
            catch (OperationCanceledException) { Cancelled++; throw; }
            finally { active--; }
        }
    }
}
