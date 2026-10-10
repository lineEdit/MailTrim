using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class GroupChecks
{
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        using var session = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        host.Children.Add(session.View);
        ReaderPane? pane = null;
        var groupUrl = "https://e.mail.ru/inbox/category?kind=newsletter";
        var emptyUrl = "https://e.mail.ru/inbox/category?kind=receipts";
        var groupRequests = 0; var messageRequests = 0; var empty = false;
        try
        {
            await session.Initialize(Guid.NewGuid()); session.View.CoreWebView2.Stop();
            session.View.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            session.View.CoreWebView2.WebResourceRequested += Fixture;
            pane = new ReaderPane(session, () => { }); host.Children.Add(pane); await pane.Start();
            var list = ((DockPanel)pane.Children[0]).Children.OfType<ListBox>().Single();
            var body = (StackPanel)((ScrollViewer)((DockPanel)pane.Children[1]).Children[1]).Content;
            var folders = pane.Children.OfType<DockPanel>().SelectMany(x => x.Children.OfType<ListBox>()).Single(x => x.Name == "ReaderFolders");
            list.SelectedItem = list.Items.Cast<ReaderLetter>().Single(x => x.Url == groupUrl);
            await pane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(8));
            check(list.Items.Cast<ReaderLetter>().Single().Subject == "Настоящее письмо" && ((ReaderFolder)folders.SelectedItem).Name == "Рассылки", "category destination renders clean child messages and selects its folder");
            check(groupRequests == 1 && messageRequests == 0 && session.Cache.Get(groupUrl) is null, "opening a category needs one list navigation and never opens its unread children or caches a group body");
            check(body.Children.OfType<TextBlock>().Any(x => x.Text.Contains("Выберите письмо из группы")) && !body.Children.OfType<TextBlock>().Any(x => x.Text.Contains("Не удалось")), "group displays a selection prompt instead of a reading error");
            check(session.Cache.List().Single(x => x.Url == groupUrl).IsGroup, "confirmed group type is preserved in encrypted metadata");
            await pane.RefreshList(); check(groupRequests == 2 && list.Items.Count == 1, "confirmed category route with query parameters can refresh without relaxing ordinary folder URL validation");
            check(!session.ReaderData.IsFolder("https://e.mail.ru/folder/fake/?action=delete") && !session.ReaderData.IsFolder("https://example.com/inbox/"), "unconfirmed action URLs and external URLs cannot become reader folders");
            list.SelectedIndex = 0; await pane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(8));
            check(body.Children.OfType<TextBlock>().Any(x => x.Text == "Текст настоящего письма") && !session.Cache.List().Single(x => x.Url.EndsWith("/message-real")).IsGroup, "real sender named Рассылки remains a message and renders its body");
            var denied = false;
            try { await session.ReaderData.PerformAction(session.Cache.List().Single(x => x.Url == groupUrl), ReaderAction.MarkRead, default); }
            catch (IOException) { denied = true; }
            check(denied && groupRequests == 2, "message commands never run against confirmed groups");
            await pane.OpenInbox();
            check(list.Items.Cast<ReaderLetter>().Single(x => x.Url == groupUrl).IsGroup && list.Items.Cast<ReaderLetter>().Single(x => x.Url == groupUrl).DisplaySubject == "Группа писем ›", "inbox labels known categories as groups after returning");
            list.SelectedItem = list.Items.Cast<ReaderLetter>().Single(x => x.Url == emptyUrl);
            await pane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(8));
            check(list.Items.Count == 0 && ((ReaderFolder)folders.SelectedItem).Name == "Чеки", "empty category renders an empty list without an error");
            await pane.OpenInbox();
            list.SelectedItem = list.Items.Cast<ReaderLetter>().Single(x => x.Url == groupUrl);
            await pane.BackgroundMessageRefresh.WaitAsync(TimeSpan.FromSeconds(8));
            pane.Close(); host.Children.Remove(pane);
            pane = new ReaderPane(session, () => { }); host.Children.Add(pane); await pane.Start();
            await pane.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(8));
            folders = pane.Children.OfType<DockPanel>().SelectMany(x => x.Children.OfType<ListBox>()).Single(x => x.Name == "ReaderFolders");
            check(((ReaderFolder)folders.SelectedItem).Name == "Рассылки", "returning to a reader tab keeps the confirmed group and its name");
            // A stale body must not swallow a confirmed category transition in the broker.
            var stale = new ReaderLetter(groupUrl, "Рассылки", "Old mistaken body", "", "");
            session.Cache.Save(stale, [new("Old fallback", "")]);
            var openedGroup = false; empty = true;
            try { await session.ReaderData.ReadFresh(stale, default); } catch (ReaderGroupOpened x) { openedGroup = x.Letters.Count == 0; }
            check(openedGroup, "category transition is not hidden by an old cached-body fallback");
            var delayed = await session.ReaderData.ReadFresh(new("https://e.mail.ru/inbox/delayed-body", "Чеки", "Delayed real mail", "", ""), default).WaitAsync(TimeSpan.FromSeconds(8));
            check(delayed.Blocks.Single().Text == "Late real body" && !session.ReaderData.Describe(delayed.Letter).IsGroup, "an empty loading shell is never classified as a category while a real body is loading");
        }
        finally { pane?.Close(); if (pane is not null) host.Children.Remove(pane); host.Children.Remove(session.View); }

        void Fixture(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (e.ResourceContext != CoreWebView2WebResourceContext.Document) { e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not found", ""); return; }
            var uri = new Uri(e.Request.Uri); string html;
            if (uri.AbsolutePath == "/inbox/category")
            {
                groupRequests += uri.Query.Contains("newsletter") ? 1 : 0;
                html = "<div class=letter-list>" + (uri.Query.Contains("receipts") || empty ? "<div class=letter-list__empty>Писем нет</div>" : "<a class='js-letter-list-item' data-unread=true href='/inbox/message-real'><span>Рассылки</span><span>Настоящее письмо</span><span>Описание</span><time datetime='2026-10-09T12:00:00Z'>9 окт</time></a>") + "</div>";
            }
            else if (uri.AbsolutePath == "/inbox/delayed-body") html = "<div class=letter-list></div><script>setTimeout(()=>{document.body.innerHTML='<div class=letter-body__body>Late real body</div>'},3000)</script>";
            else if (uri.AbsolutePath == "/inbox/message-real") { messageRequests++; html = "<div class=letter-body__body><p>Текст настоящего письма</p></div>"; }
            else html = "<div class=letter-list><a class=js-letter-list-item href='/inbox/category?kind=newsletter'><span>Рассылки</span><span>Газпромбанк,</span><span>Другие отправители</span><span>9 окт</span></a><a class=js-letter-list-item href='/inbox/category?kind=receipts'><span>Чеки</span><span>paygine.net,</span><span>Другие чеки</span><span>8 окт</span></a></div>";
            e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body>" + html + "</body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
    }
}
