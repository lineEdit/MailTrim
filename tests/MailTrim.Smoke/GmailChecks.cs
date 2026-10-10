using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using MailTrim.Core.Gmail;

namespace MailTrim.Smoke;

internal static class GmailChecks
{
    public static async Task Run(LocalStore store,Grid host,Action<bool,string> check)
    {
        var profile = new AccountProfile { Name = "Synthetic Gmail", Provider = MailProvider.Gmail };
        var vault = new GmailVault(store.Root,profile.Id);
        var credentials = new GmailCredentials("synthetic.apps.googleusercontent.com","synthetic-secret","synthetic-refresh","synthetic-access",DateTimeOffset.UtcNow.AddHours(1));
        vault.Save("credentials",credentials);
        check(vault.Load<GmailCredentials>("credentials") == credentials,"Gmail tokens round trip in Windows encrypted vault");
        var file = Path.Combine(store.Root,"Gmail",profile.Id.ToString("N"),"credentials.bin");
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("synthetic-refresh"),"Gmail credentials are absent from plaintext on disk");
        var other = new GmailVault(store.Root,Guid.NewGuid());
        check(other.Load<GmailCredentials>("credentials") is null,"Gmail profiles have isolated tokens");
        check(GmailOAuth.ParseCallback("/oauth2callback?code=code%2Fpart&state=expected","expected").Code == "code/part","OAuth callback validates state and decodes authorization code");
        check(!GmailOAuth.ParseCallback("/oauth2callback?code=stolen&state=wrong","expected").Valid
            && !GmailOAuth.ParseCallback("/other?code=stolen&state=expected","expected").Valid
            && !GmailOAuth.ParseCallback("/oauth2callback?code=x&state=expected&state=wrong","expected").Valid,
            "OAuth callback rejects wrong state, route and duplicate parameters");
        var config = GmailOAuth.ParseDesktopConfig("{\"installed\":{\"client_id\":\"synthetic.apps.googleusercontent.com\",\"client_secret\":\"secret\"}}");
        check(config.Id.StartsWith("synthetic"),"Desktop OAuth JSON import succeeds");
        bool rejected = false; try { GmailOAuth.ParseDesktopConfig("{\"web\":{\"client_id\":\"x\"}}"); } catch (FormatException) { rejected = true; }
        check(rejected,"web OAuth client cannot be imported as Desktop client");
        int sends = 0; int posts = 0; MailboxStatus? last = null;
        using var pane = new GmailPane(store,profile,(_,s) => last = s,_ => { },new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/send")) sends++;
            var path = request.RequestUri.PathAndQuery;
            string json = path.EndsWith("/profile") ? "{\"emailAddress\":\"synthetic@example.test\"}" : path.Contains("labels/INBOX") ? "{\"messagesUnread\":3}"
                : path.EndsWith("/labels") ? "{\"labels\":[{\"id\":\"Label_1\",\"name\":\"Projects\"}]}"
                : path.Contains("messages?") ? "{\"messages\":[{\"id\":\"first\"}]}"
                : JsonSerializer.Serialize(new { id = "first", threadId = "t", internalDate = "1760000000000", labelIds = new[] { "INBOX","UNREAD" }, payload = new { mimeType = "text/plain", headers = new[] { new { name = "From", value = "Synthetic sender" }, new { name = "Subject", value = "Synthetic subject" } }, body = new { data = GmailMime.Base64Url(Encoding.UTF8.GetBytes("Synthetic body")) } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        host.Children.Add(pane); await pane.Ready;
        check(last?.Unread == 3,"Gmail native pane publishes exact unread count without WebView");
        var list = Descendants<ListBox>(pane).Single(x => x.ItemsSource is GmailMessage[]);
        list.SelectedIndex = 0; await Task.Delay(250);
        check(Descendants<TextBox>(pane).Any(t => t.Text == "Synthetic body"),"Gmail full message renders as native text");
        pane.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)pane.ActualWidth,(int)pane.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(pane); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(store.Root,"gmail-synthetic.png"))) png.Save(output);
        check(posts == 0 && sends == 0,"opening unread Gmail does not mark it read or send mail");
        var cache = vault.Load<GmailCache>("cache");
        check(cache?.Bodies.Single().Text == "Synthetic body" && cache.Pages.Single().Page.Messages.Length == 1,"Gmail headers and ready body persist in encrypted cache");
        await pane.ClearData();
        check(!pane.Connected && vault.Load<GmailCredentials>("credentials") is null && vault.Load<GmailCache>("cache") is null,"disconnect clears Gmail tokens, cache and native list");
        host.Children.Remove(pane);
        var old = store.Settings.Profiles;
        store.Settings.Profiles = [profile]; store.Save();
        check(new LocalStore(store.Root).Settings.Profiles.Single().Provider == MailProvider.Gmail,"Gmail provider persists across restart");
        using (var monitor = new MailMonitor(store,host,_ => { },() => true,(_,_) => throw new Exception("Gmail passed to web monitor")))
        { monitor.RequestCheck(); await monitor.StopAsync(); }
        var autoCache = store.Settings.AutomaticMailboxCache; store.Settings.AutomaticMailboxCache = true;
        using (var monitor = new MailboxCacheMonitor(store,Window.GetWindow(host),host,() => true))
        { monitor.RequestCheck(true); await monitor.Running; }
        store.Settings.AutomaticMailboxCache = autoCache;
        check(!host.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Any(),"Mail.ru background monitors skip Gmail profiles entirely");
        store.Settings.Profiles.Add(new AccountProfile { Provider = MailProvider.Gmail, Name = "Second Gmail" });
        var shell = new MainWindow(store) { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false }; shell.Show(); await Task.Delay(100);
        check(((Grid)shell.FindName("BrowserHost")).Children.Count == 0 && ((Grid)shell.FindName("GmailHost")).Children.Count == 2,"Gmail tabs never initialize a WebView session");
        typeof(MainWindow).GetField("readerMode",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(shell,true);
        ((ListBox)shell.FindName("Profiles")).SelectedIndex = 1; await Task.Delay(100);
        check((bool)typeof(MainWindow).GetField("readerMode",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shell)!
            && ((Button)shell.FindName("SettingsButton")).Visibility == Visibility.Visible && ((Grid)shell.FindName("ReaderHost")).Visibility == Visibility.Collapsed,
            "Gmail switching preserves Mail.ru reading preference and visible settings");
        shell.Close();
        store.Settings.Profiles = old; store.Save();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = System.Windows.Media.VisualTreeHelper.GetChild(root,i); if (child is T value) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); } }
}
