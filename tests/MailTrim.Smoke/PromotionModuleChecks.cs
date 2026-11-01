using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class PromotionModuleChecks
{
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        var isolated = new LocalStore(Path.Combine(store.Root, "promotion-module-source"));
        var paused = false;
        using var session = new BrowserSession(isolated, window, _ => { }, () => paused, _ => { });
        host.Children.Add(session.Surface);
        var moduleDecisions = new List<bool>();
        try
        {
            // Background initialization leaves the new profile on about:blank. Install
            // every fixture response before making the first provider navigation.
            await session.Initialize(Guid.NewGuid(), background: true);
            var core = session.View.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Fixture;
            session.SetActive(true);

            await Navigate("https://e.mail.ru/inbox/promotion-module-first");
            check(moduleDecisions.LastOrDefault() && await Evaluate("!window.promoExecuted && !document.getElementById('module-offer') && window.promoScriptError === true"),
                "the dedicated Mail promo script is blocked before it executes or creates an unknown advertising card");
            check(await Evaluate("window.normalExecuted === true && window.apiAllowed === true && window.fetchedScriptAllowed === true"),
                "promo source blocking keeps ordinary scripts and mail API requests, including a non-executable fetch of the same filename");
            check(await Evaluate("document.getElementById('compose').click();document.getElementById('folder').click();!document.getElementById('compose-panel').hidden && window.folderClicks === 1 && !!document.querySelector('#compose-panel input[type=file]')"),
                "blocking the promotional module leaves composition, attachments and folder controls usable");

            paused = true; await session.ApplySettings();
            await Navigate("https://e.mail.ru/inbox/promotion-module-paused");
            check(!moduleDecisions[^1] && await Evaluate("window.promoExecuted === true && !!document.getElementById('module-offer') && window.normalExecuted === true"),
                "pausing filters permits the promotional script without altering ordinary page scripts");

            paused = false; await session.ApplySettings();
            await Navigate("https://account.mail.ru/login/promotion-module-auth");
            check(!moduleDecisions[^1] && await Evaluate("window.promoExecuted === true && !!document.getElementById('auth-form') && !!document.querySelector('#auth-form input[type=password]')")
                && !core.Settings.IsPasswordAutosaveEnabled && !core.Settings.AreHostObjectsAllowed,
                "the promotional module policy does not intercept authentication documents or weaken their browser restrictions");

            isolated.Rules.AllowedDomains.Add("e.mail.ru");
            await Navigate("https://e.mail.ru/inbox/promotion-module-allowed");
            check(!moduleDecisions[^1] && await Evaluate("window.promoExecuted === true && window.apiAllowed === true"),
                "an explicit resource-domain exception overrides the built-in promotional module block");
            isolated.Rules.AllowedDomains.Clear();

            isolated.Settings.BlockRequests = false;
            await Navigate("https://e.mail.ru/inbox/promotion-module-requests-off");
            check(!moduleDecisions[^1] && await Evaluate("window.promoExecuted === true && window.normalExecuted === true"),
                "disabling request filtering also disables promotional source interception");
        }
        finally { host.Children.Remove(session.Surface); }

        async Task<bool> Evaluate(string script) => await session.View.CoreWebView2.ExecuteScriptAsync(script) == "true";

        async Task Navigate(string url)
        {
            var core = session.View.CoreWebView2;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong? navigation = null;
            void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri == url) navigation = e.NavigationId; }
            void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (e.NavigationId == navigation && e.IsSuccess) done.TrySetResult();
            }
            core.NavigationStarting += Starting; core.NavigationCompleted += Completed;
            try
            {
                core.Navigate(url); await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
                for (var i = 0; i < 200; i++)
                {
                    if (await Evaluate("window.moduleFixtureReady === true")) return;
                    await Task.Delay(25);
                }
                throw new TimeoutException("promotion_module_fixture_ready");
            }
            finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; }
        }

        void Fixture(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var core = session.View.CoreWebView2;
            var path = new Uri(e.Request.Uri).AbsolutePath;
            if (path.EndsWith("/2promo.octavius.bundle.js", StringComparison.Ordinal) && e.ResourceContext == CoreWebView2WebResourceContext.Script)
                moduleDecisions.Add(e.Response?.StatusCode == 403);
            // Production handlers run first. Preserve their blocked response instead
            // of accidentally replacing it with this test's successful module body.
            if (e.Response is not null) return;
            string content; string type;
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
            {
                type = "text/html";
                content = """
                <!doctype html><html><body>
                <div class="letter-list__react"><h2>Synthetic mail</h2><button id="compose">Compose</button><button id="folder">Inbox</button>
                  <form id="compose-panel" hidden><input aria-label="Recipient"><textarea aria-label="Message"></textarea><input type="file"></form>
                </div><form id="auth-form"><input type="password" aria-label="Synthetic password"></form>
                <script src="https://e.mail.ru/static/fixture-hash/main.octavius.bundle.js"></script>
                <script src="https://e.mail.ru/static/fixture-hash/2promo.octavius.bundle.js?v=1" onerror="window.promoScriptError=true"></script>
                <script>
                  Promise.all([
                    fetch('/api/v1/messages-fixture').then(async r=>window.apiAllowed=r.status===200 && (await r.json()).fixture==='mail'),
                    fetch('/static/fixture-hash/2promo.octavius.bundle.js?as=fetch').then(async r=>window.fetchedScriptAllowed=r.status===200 && (await r.text()).includes('promoExecuted'))
                  ]).then(()=>window.moduleFixtureReady=true,()=>{window.moduleFixtureFailed=true;window.moduleFixtureReady=true});
                </script></body></html>
                """;
            }
            else if (path.EndsWith("/2promo.octavius.bundle.js", StringComparison.Ordinal))
            {
                type = "application/javascript";
                content = "window.promoExecuted=true;var offer=document.createElement('div');offer.id='module-offer';offer.style='position:fixed;inset:50px;background:lightblue';offer.textContent='Completely new content with no advertising keywords';document.body.appendChild(offer);";
            }
            else if (path.EndsWith("/main.octavius.bundle.js", StringComparison.Ordinal))
            {
                type = "application/javascript";
                content = "window.normalExecuted=true;window.folderClicks=0;document.getElementById('compose').onclick=()=>document.getElementById('compose-panel').hidden=false;document.getElementById('folder').onclick=()=>window.folderClicks++;";
            }
            else if (path == "/api/v1/messages-fixture")
            {
                type = "application/json"; content = "{\"fixture\":\"mail\"}";
            }
            else { e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Fixture not found", "Cache-Control: no-store"); return; }
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 200, "OK", $"Content-Type: {type}; charset=utf-8\r\nCache-Control: no-store");
        }
    }
}
