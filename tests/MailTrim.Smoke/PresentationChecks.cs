using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class PresentationChecks
{
    public static async Task Ready(BrowserSession session)
    {
        try { await Until(() => !session.Surface.IsPending && session.View.Visibility == Visibility.Visible); }
        catch
        {
            // These diagnostics run only against the synthetic fixture profile.
            Console.WriteLine("Presentation diagnostic: " + await session.View.CoreWebView2.ExecuteScriptAsync("(()=>{try{return window.__mailtrimPresentation?.()}catch(e){return {error:e.name+':'+e.message}}})()"));
            foreach (var name in new[] { "waitingDocument", "blockedDocument", "acceptedDocument", "generation", "polling", "capturing" })
                Console.WriteLine(name+"="+typeof(BrowserPresentation).GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)?.GetValue(session.Surface));
            Console.WriteLine("surface="+session.Surface.Visibility+", requested="+session.Surface.RequestedVisibility+", visible="+session.Surface.IsVisible+", view="+session.View.Visibility);
            throw;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var n = 0; n < 400; n++) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("presentation_condition");
    }
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        bool paused = false;
        using var session = new BrowserSession(store, window, _ => { }, () => paused, _ => { });
        host.Children.Add(session.Surface);
        try
        {
            await session.Initialize(Guid.NewGuid()); session.View.CoreWebView2.Stop();
            var core = session.View.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Fixture;
            session.SetActive(true);
            await Navigate("https://e.mail.ru/inbox/render-first"); await Ready(session);
            await Until(() => session.Surface.HasPreview);
            check(await core.ExecuteScriptAsync("window.__mailtrimPresentation().ready && getComputedStyle(document.documentElement).opacity==='1' && document.getElementById('banner').getBoundingClientRect().height===0") == "true", "only a cleaned and settled first document is published");
            check(!core.Settings.IsWebMessageEnabled, "presentation state polling keeps the page-to-host message channel disabled");
            var next = Navigate("https://e.mail.ru/inbox/render-next");
            await Until(() => session.Surface.IsPending);
            check(session.View.Visibility == Visibility.Hidden && session.Surface.HasPreview, "navigation hides the live browser and preserves its previous clean frame in memory");
            var bitmap = new RenderTargetBitmap((int)session.Surface.ActualWidth, (int)session.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(session.Surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(store.Root, "presentation-synthetic.png"))) png.Save(file);
            await next; await Ready(session);
            check(await core.ExecuteScriptAsync("!window.badPaint && document.getElementById('subject').textContent==='Next message'") == "true", "delayed next document never publishes its unfiltered banner frame");
            await core.ExecuteScriptAsync("""
                history.pushState(null,'','/inbox/render-spa');
                document.getElementById('subject').textContent='SPA message';
                setTimeout(()=>document.getElementById('banner').replaceWith(Object.assign(document.createElement('div'),{id:'banner',className:'js-banner-wrapper-container',textContent:'Late banner'})),120);
                """);
            await Until(() => session.Surface.IsPending); check(session.View.Visibility == Visibility.Hidden, "SPA transitions enter the same preparation stage as full navigations");
            await Ready(session);
            check(await core.ExecuteScriptAsync("!window.badPaint && document.getElementById('banner').getBoundingClientRect().height===0 && document.getElementById('subject').textContent==='SPA message'") == "true", "SPA updates settle after late ad insertion before publication");
            for (var i=0;i<3;i++)
            {
                await core.ExecuteScriptAsync($"history.pushState(null,'','/inbox/render-race-{i}');document.getElementById('subject').textContent='Rapid {i}'");
                await Until(() => session.Surface.IsPending); await Ready(session);
            }
            check(true, "rapid route changes remain usable while previous preview capture may still be completing");

            await core.ExecuteScriptAsync("""
                document.body.insertAdjacentHTML('beforeend',`<div id="game-shade" style="position:fixed;inset:0;background:#8888"><div id="game-dialog" style="position:absolute;left:100px;top:50px;width:420px;height:400px;background:white"><button aria-label="Закрыть" style="position:absolute;right:8px;top:8px;width:32px;height:32px">×</button><h2>Игра, чтобы вернуть лето!</h2><p>Соберите летние впечатления и запрыгните повыше! Разыгрываем призы.</p><div style="cursor:pointer" onclick="window.gameActionClicked=true">Играть</div><div>Сроки акции: 29.09–10.11. erid: 2Vexample123</div></div></div>`);
                requestAnimationFrame(()=>window.gameLeaked=getComputedStyle(document.documentElement).opacity!=='0' && document.getElementById('game-dialog').getBoundingClientRect().height>0);
                """);
            await Task.Delay(400); await Ready(session);
            check(await core.ExecuteScriptAsync("!window.gameLeaked && !window.gameActionClicked && document.getElementById('game-shade').getBoundingClientRect().height===0 && document.getElementById('game-dialog').getBoundingClientRect().height===0") == "true", "an erid-labelled game campaign is removed with its shade without a RuStore title");
            await core.ExecuteScriptAsync("document.getElementById('game-dialog').setAttribute('role','dialog');window.adAnimation=setInterval(()=>document.getElementById('game-dialog').style.opacity=String(Math.random()),12)");
            await Task.Delay(400); await Ready(session);
            check(await core.ExecuteScriptAsync("window.__mailtrimPresentation().ready") == "true", "continuous animation inside a filtered ad cannot keep the preparation stage busy");
            await core.ExecuteScriptAsync("clearInterval(window.adAnimation)");
            await core.ExecuteScriptAsync("""
                document.getElementById('game-shade').remove();
                document.body.insertAdjacentHTML('beforeend','<div class=letter-body id=protected-mail><h2>Игра, чтобы вернуть лето!</h2><p>Разыгрываем призы. erid: 2Vexample123</p><button>Играть</button></div><div role=dialog id=help-dialog style="position:fixed;left:600px;top:50px;width:280px;height:200px;background:white"><h2>Что такое erid?</h2><p>Идентификатор рекламы в акции.</p><button>Понятно</button></div>');
                """);
            await Task.Delay(400);
            check(await core.ExecuteScriptAsync("document.getElementById('protected-mail').getBoundingClientRect().height>0 && document.getElementById('help-dialog').getBoundingClientRect().height>0") == "true", "campaign-like message content and a working explanation dialog stay visible");
            await core.ExecuteScriptAsync("window.workingAnimation=setInterval(()=>document.getElementById('help-dialog').style.opacity=String(.8+Math.random()*.2),12)");
            await Until(() => session.Surface.IsPending); await Ready(session);
            check(true, "continuously changing working content still reaches a cleaned bounded presentation");
            await core.ExecuteScriptAsync("clearInterval(window.workingAnimation)");
            await core.ExecuteScriptAsync("document.getElementById('protected-mail').remove();document.getElementById('help-dialog').remove();document.body.insertAdjacentHTML('beforeend','<div class=compose-app><textarea>Unsent draft</textarea></div>')");
            await Until(() => !session.Surface.HasPreview);
            check(true, "composition content clears the in-memory browser preview");
            await Navigate("https://auth.mail.ru/login/render-test");
            await Ready(session);
            check(!session.Surface.HasPreview && !core.Settings.IsPasswordAutosaveEnabled && !core.Settings.IsWebMessageEnabled, "official authentication stays usable without a stored preview or password autosave");
            paused = true; await session.ApplySettings();
            await Navigate("https://e.mail.ru/inbox/render-first"); await Ready(session);
            check(await core.ExecuteScriptAsync("!document.documentElement.hasAttribute('data-mailtrim-render-pending') && document.getElementById('banner').getBoundingClientRect().height>0") == "true", "pausing filters releases both DOM and native presentation guards");
            paused = false; await session.ApplySettings(); await Ready(session);
            check(await core.ExecuteScriptAsync("document.getElementById('banner').getBoundingClientRect().height===0") == "true", "reenabling filtering restores preparation and clean presentation");
            await core.ExecuteScriptAsync("delete window.__mailtrimPresentation");
            await Until(() => session.Surface.IsPending);
            check(session.View.Visibility == Visibility.Hidden && window.IsEnabled, "missing preparation state keeps raw content hidden while the application toolbar stays usable");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false)); await Ready(session);
            session.SetActive(false);
            check(session.Surface.Visibility == Visibility.Collapsed && session.View.Visibility == Visibility.Collapsed, "switching profiles hides the entire presentation surface");
            session.SetActive(true); await Ready(session);
            session.SetReadingOnly(true);
            check(session.Surface.Visibility == Visibility.Hidden && session.View.Visibility == Visibility.Hidden, "native reading keeps the collection browser and its presentation hidden");
        }
        finally { host.Children.Remove(session.Surface); }

        async Task Navigate(string url)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ulong? id = null;
            void Start(object? s, CoreWebView2NavigationStartingEventArgs e) => id = e.NavigationId;
            void End(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.NavigationId == id) done.TrySetResult(); }
            var core = session.View.CoreWebView2; core.NavigationStarting += Start; core.NavigationCompleted += End;
            try { core.Navigate(url); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { core.NavigationStarting -= Start; core.NavigationCompleted -= End; }
        }
        async void Fixture(object? s, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (e.ResourceContext != CoreWebView2WebResourceContext.Document) { e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not found", ""); return; }
            using var deferral = e.GetDeferral(); var uri = new Uri(e.Request.Uri);
            if (uri.AbsolutePath.EndsWith("render-next")) await Task.Delay(600);
            var html = uri.Host == "auth.mail.ru" ? "<form><h2>Вход</h2><input type=password><button>Продолжить</button></form>" : """
                <style>body{margin:0;font:16px Arial}.js-banner-wrapper-container{height:90px;background:#ccc}.letter-list__react{height:300px}</style>
                <div id=banner class=js-banner-wrapper-container>Advertisement</div><div class=letter-list__react><h2 id=subject>__SUBJECT__</h2><a href=/inbox/render-next class=js-letter-list-item>Open message</a></div>
                <script>window.badPaint=false;function audit(){if(getComputedStyle(document.documentElement).opacity!=='0' && document.getElementById('banner')?.getBoundingClientRect().height>0)window.badPaint=true;requestAnimationFrame(audit)}requestAnimationFrame(audit);</script>
                """.Replace("__SUBJECT__", uri.AbsolutePath.EndsWith("render-next") ? "Next message" : "First message");
            e.Response = session.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body>"+html+"</body></html>")), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
    }
}
