using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class PresentationChecks
{
    public static Task Ready(BrowserSession session) => Until(() => session.View.Visibility == Visibility.Visible);
    private static async Task Until(Func<bool> condition)
    {
        for(var i=0;i<200;i++){if(condition())return;await Task.Delay(25);}
        throw new TimeoutException("live_browser_condition");
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
            check(session.Surface.Children.Count==1 && session.Surface.Children[0]==session.View,
                "original mode contains only the live browser without a screenshot or waiting overlay");
            var initial = await core.ExecuteScriptAsync("typeof window.__mailtrimPresentation==='undefined' && !document.documentElement.hasAttribute('data-mailtrim-render-pending') && getComputedStyle(document.documentElement).opacity==='1'");
            if(initial!="true") Console.WriteLine("Synthetic first document: "+await core.ExecuteScriptAsync("({ready:document.readyState,fixture:!!document.getElementById('subject'),bridge:typeof window.__mailtrimPresentation,pending:document.documentElement?.hasAttribute('data-mailtrim-render-pending'),opacity:document.documentElement?getComputedStyle(document.documentElement).opacity:null})"));
            check(initial=="true","no whole-page render gate or readiness polling bridge remains");
            var next=Navigate("https://e.mail.ru/inbox/render-next");await Task.Delay(50);
            check(session.View.Visibility==Visibility.Visible,"network navigation does not hide the browser behind a preparation screen");
            await next;
            await core.ExecuteScriptAsync("history.pushState(null,'','/inbox/render-spa');document.getElementById('subject').textContent='SPA message'");
            check(session.View.Visibility==Visibility.Visible && await core.ExecuteScriptAsync("getComputedStyle(document.documentElement).opacity==='1'")=="true",
                "SPA transitions do not introduce page hiding or a debounce delay");
            await core.ExecuteScriptAsync("""
                document.body.insertAdjacentHTML('beforeend',`<div id="game-shade" style="position:fixed;inset:0;background:#8888"><div id="game-dialog" style="position:absolute;left:100px;top:50px;width:420px;height:400px;background:white"><button aria-label="Закрыть" style="position:absolute;right:8px;top:8px;width:32px;height:32px">×</button><h2>Игра, чтобы вернуть лето!</h2><p>Разыгрываем призы.</p><div style="cursor:pointer" onclick="window.gameClicked=true">Играть</div><div>Сроки акции. erid: 2Vexample123</div></div></div>`);
                requestAnimationFrame(()=>window.gameLeaked=document.getElementById('game-dialog').getBoundingClientRect().height>0);
                """);
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("!window.gameLeaked && !window.gameClicked && document.getElementById('game-shade').getBoundingClientRect().height===0")=="true",
                "campaign and its shade are hidden before paint without freezing the page or activating its CTA");
            await core.ExecuteScriptAsync("document.getElementById('game-shade').remove();document.body.insertAdjacentHTML('beforeend','<div class=letter-body id=quote>Разыгрываем призы. erid: 2Vexample123<button>Играть</button></div><div role=dialog id=working style=\"position:fixed;left:600px;top:50px;width:200px;height:200px\"><h2>Подтвердите действие</h2><input><button>OK</button></div>')");
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("document.getElementById('quote').getBoundingClientRect().height>0 && document.getElementById('working').getBoundingClientRect().height>0")=="true",
                "message content and working dialogs remain visible in live mode");
            await Navigate("https://auth.mail.ru/login/render-test"); await Ready(session);
            check(!core.Settings.IsPasswordAutosaveEnabled && !core.Settings.IsWebMessageEnabled,"official authentication retains disabled password storage and host messaging");
            paused=true;await session.ApplySettings();await Navigate("https://e.mail.ru/inbox/render-first");
            check(await core.ExecuteScriptAsync("document.getElementById('banner').getBoundingClientRect().height>0")=="true","pausing filters restores the original page without a waiting surface");
            session.SetActive(false);check(session.Surface.Visibility==Visibility.Collapsed,"inactive account hides its entire browser surface");
            session.SetActive(true);check(session.View.Visibility==Visibility.Visible,"return to an account does not await readiness polling");
            session.SetReadingOnly(true);check(session.View.Visibility==Visibility.Hidden,"native reading continues to collect with a hidden browser");
        }
        finally { host.Children.Remove(session.Surface); }

        async Task Navigate(string url)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ulong? id = null;
            void Start(object? s, CoreWebView2NavigationStartingEventArgs e) { if(e.Uri==url) id = e.NavigationId; }
            void End(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess && e.NavigationId == id) done.TrySetResult(); }
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
