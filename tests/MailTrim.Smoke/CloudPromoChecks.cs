using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MailTrim.App;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.Smoke;

internal static class CloudPromoChecks
{
    public static async Task Run(LocalStore store, Grid host, Window window, Action<bool, string> check)
    {
        using var session = new BrowserSession(store, window, _ => { }, () => false, _ => { });
        host.Children.Add(session.Surface);
        try
        {
            await session.Initialize(Guid.NewGuid(), background: true);
            await session.EnsureCloud(surface => host.Children.Add(surface));
            var core = session.CloudView!.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(new Uri(e.Request.Uri).AbsolutePath.Contains("__mailtrim_max", StringComparison.Ordinal) ? MaxFixture
                    : new Uri(e.Request.Uri).AbsolutePath.Contains("__mailtrim_app", StringComparison.Ordinal) ? AppFixture : Fixture)),
                200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            session.SetActive(true); session.SetCloudOpen(true);
            await Navigate("https://cloud.mail.ru/home/__mailtrim_promos");
            await Until("window.cloudFirstFrame !== undefined");
            check(await Is("!cloudFirstFrame && document.getElementById('offer-panel').getBoundingClientRect().height === 0 && document.getElementById('offer-shade').getBoundingClientRect().height === 0"),
                "Cloud subscription offer and its full-page shade are hidden before the first frame");
            check(await Is("!window.cloudCtaClicks && document.getElementById('file-grid').getBoundingClientRect().height > 0 && getComputedStyle(document.documentElement).opacity !== '0'"),
                "Cloud cleaning keeps files visible without freezing the page or activating an advertising CTA");
            check(await Is("['upload','rename','share','delete'].every(id => document.getElementById(id).getBoundingClientRect().height > 0) && !window.workingCloseClicks"),
                "Cloud upload, rename, share and delete dialogs including their close controls remain untouched");
            check(await Is("document.getElementById('campaign-file').getBoundingClientRect().height > 0 && document.getElementById('preview').getBoundingClientRect().height > 0 && document.getElementById('preview-document').getBoundingClientRect().height > 0 && !window.previewCtaClicks"),
                "Cloud file names and a document preview quoting the same offer and erid are not advertising dialogs");
            await core.ExecuteScriptAsync("""
                window.cloudReplacementLeaks=0;
                let replacements=0;
                function replaceCloudOffer(){
                  document.getElementById('offer-shade').outerHTML=window.cloudOfferMarkup;
                  requestAnimationFrame(()=>{
                    if(document.getElementById('offer-shade').getBoundingClientRect().height>0 || document.getElementById('offer-panel').getBoundingClientRect().height>0)window.cloudReplacementLeaks++;
                    if(++replacements<5)replaceCloudOffer();else window.cloudReplacementsDone=true;
                  });
                }
                replaceCloudOffer();
                """);
            await Until("window.cloudReplacementsDone === true");
            check(await Is("cloudReplacementLeaks === 0 && !window.cloudCtaClicks"), "Cloud repeated SPA offer replacements stay hidden at every observed frame");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await Is("document.getElementById('offer-shade').getBoundingClientRect().height > 0 && document.getElementById('offer-panel').getBoundingClientRect().height > 0"),
                "pausing cosmetic filtering restores the Cloud offer and its shade");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            await Until("document.getElementById('offer-shade').getBoundingClientRect().height === 0");
            check(await Is("document.getElementById('offer-panel').getBoundingClientRect().height === 0 && !window.cloudCtaClicks"), "reenabling cosmetic filtering removes the Cloud offer again without clicking its CTA");
            await core.ExecuteScriptAsync("document.getElementById('offer-panel').innerHTML='<button aria-label=Закрыть>×</button>'");
            await Frame();
            check(await Is("document.getElementById('offer-shade').getBoundingClientRect().height === 0"), "a previously identified Cloud offer does not leave an empty shaded shell after its content disappears");
            await core.ExecuteScriptAsync("document.getElementById('offer-panel').innerHTML='<h2>Переименовать файл</h2><input value=Отчёт><button>Сохранить</button><button>Отмена</button>'");
            await Frame();
            check(await Is("document.getElementById('offer-shade').getBoundingClientRect().height > 0 && document.querySelector('#offer-panel input').getBoundingClientRect().height > 0"),
                "Cloud restores a former offer container when the site reuses it for a rename form");
            await core.ExecuteScriptAsync("""
                const detachedOffer=document.createElement('div');detachedOffer.innerHTML=window.cloudOfferMarkup;
                const sidecarPanel=detachedOffer.querySelector('#offer-panel');sidecarPanel.id='sidecar-panel';sidecarPanel.style.zIndex='101';
                const sidecarShade=document.createElement('div');sidecarShade.id='sidecar-shade';sidecarShade.className='p39';
                document.body.append(sidecarShade,sidecarPanel);
                """);
            await Frame();
            check(await Is("document.getElementById('sidecar-panel').getBoundingClientRect().height === 0 && document.getElementById('sidecar-shade').getBoundingClientRect().height === 0"),
                "Cloud removes a subscription offer and its separate sibling backdrop");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await Is("document.getElementById('sidecar-panel').getBoundingClientRect().height > 0 && document.getElementById('sidecar-shade').getBoundingClientRect().height > 0"),
                "pausing Cloud filtering restores a separately tracked offer backdrop");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            await Frame();
            check(await Is("document.getElementById('sidecar-panel').getBoundingClientRect().height === 0 && document.getElementById('sidecar-shade').getBoundingClientRect().height === 0"),
                "Cloud filtering reidentifies separate offer and backdrop after resuming");
            await core.ExecuteScriptAsync("document.getElementById('sidecar-shade').innerHTML='<div role=dialog><h2>Поделиться файлом</h2><input value=Ссылка><button>Копировать</button><button>Отмена</button></div>'");
            await Frame();
            check(await Is("document.getElementById('sidecar-shade').getBoundingClientRect().height > 0 && document.querySelector('#sidecar-shade input').getBoundingClientRect().height > 0 && document.getElementById('sidecar-panel').getBoundingClientRect().height === 0"),
                "Cloud releases a reused backdrop containing a working sharing form while keeping the old offer hidden");
            await Navigate("https://cloud.mail.ru/home/__mailtrim_max");
            await Until("window.cloudFirstFrame !== undefined");
            check(await Is("!cloudFirstFrame && document.getElementById('offer-panel').getBoundingClientRect().height === 0 && document.getElementById('offer-shade').getBoundingClientRect().height === 0 && !/erid|₽|руб|терабайт/iu.test(document.getElementById('offer-panel').textContent)"),
                "Cloud MAX channel invitation without a price or ERID is hidden with its shade before the first frame");
            check(await Is("document.getElementById('max-campaign-file').getBoundingClientRect().height > 0 && document.getElementById('max-preview').getBoundingClientRect().height > 0 && !window.cloudCtaClicks && !window.maxPreviewCtaClicks"),
                "Cloud keeps MAX-themed file names and document contents visible without clicking subscribe controls");
            check(await Is("['upload','rename','share','delete'].every(id => document.getElementById(id).getBoundingClientRect().height > 0) && !window.workingCloseClicks"),
                "Cloud MAX promo filtering preserves working upload, rename, share and delete dialogs");
            check(await Is("document.getElementById('ordinary-notice').getBoundingClientRect().height > 0 && document.getElementById('ordinary-notice').textContent.includes('Что нового в Mail') && !window.workingCloseClicks"),
                "Cloud preserves an ordinary notice with the same heading when it contains no marketing invitation");
            check(await Is("document.getElementById('folder-invitation').getBoundingClientRect().height > 0 && !window.folderJoinClicks && !window.workingCloseClicks"),
                "Cloud preserves a real folder invitation named Our MAX channel with its working Join control");
            await core.ExecuteScriptAsync("""
                window.maxReplacementLeaks=0;
                let maxReplacements=0;
                function replaceMaxOffer(){
                  document.getElementById('offer-shade').outerHTML=window.cloudOfferMarkup;
                  requestAnimationFrame(()=>{
                    if(document.getElementById('offer-shade').getBoundingClientRect().height>0 || document.getElementById('offer-panel').getBoundingClientRect().height>0)window.maxReplacementLeaks++;
                    if(++maxReplacements<5)replaceMaxOffer();else window.maxReplacementsDone=true;
                  });
                }
                replaceMaxOffer();
                """);
            await Until("window.maxReplacementsDone === true");
            check(await Is("maxReplacementLeaks === 0 && !window.cloudCtaClicks"),
                "Cloud MAX invitations reinserted by the SPA remain hidden at every observed frame");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await Is("document.getElementById('offer-panel').getBoundingClientRect().height > 0 && document.getElementById('offer-shade').getBoundingClientRect().height > 0"),
                "pausing cosmetic filtering restores a MAX invitation and its shade");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            await Frame();
            check(await Is("document.getElementById('offer-panel').getBoundingClientRect().height === 0 && document.getElementById('offer-shade').getBoundingClientRect().height === 0 && document.getElementById('max-preview').getBoundingClientRect().height > 0 && !window.cloudCtaClicks"),
                "Cloud filtering resumes MAX promo cleanup while preserving its quoted preview");
            await Navigate("https://cloud.mail.ru/home/__mailtrim_app");
            await Until("window.cloudFirstFrame !== undefined");
            check(await Is("!cloudFirstFrame && document.getElementById('offer-panel').getBoundingClientRect().height === 0 && document.getElementById('offer-shade').getBoundingClientRect().height === 0 && !window.cloudCtaClicks && !/erid|₽|руб/iu.test(document.getElementById('offer-panel').textContent)"),
                "Cloud app-install invitation without a price or ERID is hidden before the first frame without activating install");
            await Navigate("https://account.mail.ru/login/__mailtrim_cloud_promos");
            await Until("window.cloudFirstFrame !== undefined");
            check(await Is("cloudFirstFrame && document.getElementById('offer-panel').getBoundingClientRect().height > 0 && document.getElementById('auth-password').getBoundingClientRect().height > 0"),
                "Cloud cosmetic filtering does not run on an official authentication origin");

            async Task<bool> Is(string expression) => await core.ExecuteScriptAsync(expression) == "true";
            async Task Until(string expression)
            {
                for (var i = 0; i < 300; i++) { if (await Is(expression)) return; await Task.Delay(25); }
                throw new TimeoutException("cloud_promo_condition: " + expression);
            }
            async Task Frame()
            {
                await core.ExecuteScriptAsync("window.cloudNextFrame=false;requestAnimationFrame(()=>window.cloudNextFrame=true)");
                await Until("window.cloudNextFrame === true");
            }
            async Task Navigate(string url)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ulong? id = null;
                void Starting(object? s, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri == url) id = e.NavigationId; }
                void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess && e.NavigationId == id) done.TrySetResult(); }
                core.NavigationStarting += Starting; core.NavigationCompleted += Completed;
                try { core.Navigate(url); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; }
            }
        }
        finally
        {
            host.Children.Remove(session.Surface);
            if (session.CloudSurface is { } surface) host.Children.Remove(surface);
        }
    }

    private static string MaxFixture
    {
        get
        {
            var start = Fixture.IndexOf("<div id=\"offer-shade\"", StringComparison.Ordinal);
            var end = Fixture.IndexOf("<input id=\"auth-password\"", start, StringComparison.Ordinal);
            var html = Fixture[..start] + """
                <div id="offer-shade" class="p39"><div id="offer-panel" class="p41"><button class="p43" aria-label="Закрыть">×</button><div class="p45" aria-hidden="true"><img alt="" width="300" height="135" src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='300' height='135'%3E%3Crect width='300' height='135' fill='%2308f'/%3E%3C/svg%3E"></div><div id="max-copy"><h2>Что нового в Mail</h2><p>Подписывайтесь на наш канал в MAX, чтобы быть в курсе обновлений и акций</p><button onclick="cloudCtaClicks++">Подписаться</button></div></div></div>
                """ + Fixture[end..];
            return html.Replace("window.cloudCtaClicks=0;", "window.cloudCtaClicks=0;window.maxPreviewCtaClicks=0;window.folderJoinClicks=0;", StringComparison.Ordinal)
                .Replace("<div role=\"row\">Отчёт.xlsx", "<div role=\"row\" id=\"max-campaign-file\">Что нового в Mail — канал в MAX — Подписаться.pdf<button>Открыть</button></div><div role=\"row\">Отчёт.xlsx", StringComparison.Ordinal)
                .Replace("<div id=\"offer-shade\"", "<div id=\"ordinary-notice\" class=\"working\" role=\"dialog\" style=\"top:570px\"><h2>Что нового в Mail</h2><p>Файл сохранён. Синхронизация завершена.</p><button>Понятно</button><button aria-label=\"Закрыть\" onclick=\"workingCloseClicks++\">×</button></div><div id=\"folder-invitation\" class=\"working\" role=\"dialog\" style=\"left:500px;top:470px;width:400px;height:130px\"><h2>Общая папка</h2><p>Вас пригласили в папку «Наш канал MAX».</p><button onclick=\"folderJoinClicks++\">Присоединиться</button><button aria-label=\"Закрыть\" onclick=\"workingCloseClicks++\">×</button></div><div id=\"offer-shade\"", StringComparison.Ordinal)
                .Replace("<div id=\"preview-document\" role=\"document\">", "<div id=\"preview-document\" role=\"document\"><section id=\"max-preview\"><h3>Что нового в Mail</h3><p>Подписывайтесь на наш канал в MAX, чтобы быть в курсе обновлений и акций</p><button onclick=\"maxPreviewCtaClicks++\">Подписаться</button></section>", StringComparison.Ordinal);
        }
    }

    private static string AppFixture => MaxFixture
        .Replace("Что нового в Mail", "Установите Облако Mail", StringComparison.Ordinal)
        .Replace("Подписывайтесь на наш канал в MAX, чтобы быть в курсе обновлений и акций", "Установите приложение Облако Mail на телефон, чтобы ваши файлы были под рукой", StringComparison.Ordinal)
        .Replace(">Подписаться</button>", ">Установить</button>", StringComparison.Ordinal);

    private const string Fixture = """
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{margin:0;font:16px Arial;background:#f5f6f7}main{padding:16px;width:900px}button{padding:5px}input{width:130px}
        #file-grid{height:130px;background:white}#campaign-file{height:45px}#preview{position:fixed;left:550px;top:130px;width:350px;height:300px;background:white;overflow:auto}
        #preview-document{padding:12px}.working{position:fixed;left:16px;width:450px;height:95px;background:white}#upload{top:170px}#rename{top:270px}#share{top:370px}#delete{top:470px}
        .p39{position:fixed;inset:0;background:rgba(0,0,0,.4);z-index:100}.p41{position:absolute;left:200px;top:100px;width:540px;height:440px;background:white;border-radius:20px;box-sizing:border-box;padding:24px}
        .p43{position:absolute;right:8px;top:8px;width:32px;height:32px;padding:0}.p45{height:135px;background:#08f}.p47{font-size:11px;color:#999}
        </style><script>window.cloudCtaClicks=0;window.previewCtaClicks=0;window.workingCloseClicks=0;</script></head><body>
        <main><h1>Все файлы</h1><div id="file-grid" role="grid"><div role="row" id="campaign-file">Ого! Как выгодно! Подключите 1 терабайт памяти. Реклама erid: 2Vsynthetic123. Попробовать Облако.pdf<button>Скачать</button></div><div role="row">Отчёт.xlsx<button>Открыть</button></div></div></main>
        <div id="upload" class="working" role="dialog"><h2>Загрузка файлов</h2><input type="file"><button>Загрузить</button><button aria-label="Закрыть" onclick="workingCloseClicks++">×</button></div>
        <div id="rename" class="working" role="dialog"><h2>Переименовать файл</h2><input value="Отчёт"><button>Сохранить</button><button aria-label="Закрыть" onclick="workingCloseClicks++">×</button></div>
        <div id="share" class="working" role="dialog"><h2>Поделиться ссылкой</h2><input value="https://cloud.mail.ru/public/test"><button>Копировать</button><button aria-label="Закрыть" onclick="workingCloseClicks++">×</button></div>
        <div id="delete" class="working" role="dialog"><h2>Удалить файл?</h2><span>Ого! Как выгодно! Реклама erid: 2Vsynthetic123.pdf</span><button>Удалить</button><button>Отмена</button><button aria-label="Закрыть" onclick="workingCloseClicks++">×</button></div>
        <div id="preview" role="dialog"><h2>Просмотр документа</h2><div id="preview-document" role="document"><h3>Ого! Как выгодно!</h3><p>Подключите 1 терабайт памяти в Облаке по выгодной цене — 130 ₽ в месяц</p><button onclick="previewCtaClicks++">Попробовать Облако</button><p>Реклама. ООО ВК. erid: 2Vsynthetic123. Сроки акции.</p></div><button>Скачать</button></div>
        <div id="offer-shade" class="p39"><div id="offer-panel" class="p41"><button class="p43" aria-label="Закрыть">×</button><div class="p45"></div><h2>Ого! Как выгодно!</h2><p>Подключите 1 терабайт памяти в Облаке по выгодной цене — 130 ₽ в месяц</p><button onclick="cloudCtaClicks++">Попробовать Облако</button><div class="p47">Реклама. ООО ВК, ИНН 7743001840, erid: 2Vsynthetic123. Сроки акции: 15.10.26 — 19.11.26. Цена за первый год — 1560 руб.</div></div></div>
        <input id="auth-password" type="password" style="position:fixed;left:750px;top:600px">
        <script>window.cloudOfferMarkup=document.getElementById('offer-shade').outerHTML;requestAnimationFrame(()=>window.cloudFirstFrame=document.getElementById('offer-shade').getBoundingClientRect().height>0 || document.getElementById('offer-panel').getBoundingClientRect().height>0);</script>
        </body></html>
        """;
}
