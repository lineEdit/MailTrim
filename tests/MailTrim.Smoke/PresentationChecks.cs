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
        var redirectClassifier = typeof(BrowserSession).GetMethod("IsStalledMailRedirect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Stalled(string title, bool hasMail, bool hasLogin) => (bool)redirectClassifier.Invoke(null, [title, hasMail, hasLogin])!;
        check(Stalled("Перенаправление", false, false) && Stalled("Redirect", false, false),
            "empty Mail.ru redirect documents are recognized for official sign-in recovery");
        check(!Stalled("Входящие - Почта Mail", true, false) && !Stalled("Перенаправление", false, true),
            "mail content and authentication forms are never replaced by redirect recovery");
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
            await core.ExecuteScriptAsync("""
                window.petActionClicks=0; window.petCloseClicks=0; window.petLeaks=0;
                window.insertPetPromo=outsideClose=>{
                  document.getElementById('pet-root')?.remove();
                  const close=`<button id="pet-close" aria-label="Закрыть" style="position:absolute;${outsideClose?'left:600px;top:58px':'right:8px;top:8px'};width:32px;height:32px" onclick="window.petCloseClicks++">×</button>`;
                  const copy=`<h2>1000 ₽ на вкусняшки питомцу</h2><p>А ещё 1 ТБ памяти по специальной цене — нужно только заполнить анкету, и всё!</p><a href="https://example.test/raffle" style="display:inline-block;cursor:pointer" onclick="event.preventDefault();window.petActionClicks++">К розыгрышу</a><small style="display:block">Реклама ООО ВК. Акция 15.10.26 — 26.11.26. Условия: vk.cc/d2TCyA. erid 2VtzqxWGbw5. 0+</small>`;
                  document.body.insertAdjacentHTML('beforeend',`<div id="pet-root" style="position:fixed;inset:0"><div id="pet-shade" style="position:absolute;inset:0;background:#8888"></div><section id="pet-frame" style="position:absolute;left:100px;top:50px;width:540px;height:500px;background:white;border-radius:20px"><div id="pet-art" role="img" aria-label="Synthetic pet artwork" style="height:210px;background:linear-gradient(#acf,#cfc)"></div><div id="pet-copy" style="padding:12px">${copy}</div>${outsideClose?'':close}</section>${outsideClose?close:''}</div>`);
                  requestAnimationFrame(()=>{if(document.getElementById('pet-frame').getBoundingClientRect().height>0 || document.getElementById('pet-shade').getBoundingClientRect().height>0)window.petLeaks++});
                };
                document.body.insertAdjacentHTML('beforeend',`<div id="pet-quote" class="letter-body" style="height:160px"><h2>1000 ₽ на вкусняшки питомцу</h2><p>Заполнить анкету. erid 2VtzqxWGbw5. Реклама.</p><button>К розыгрышу</button></div><div id="pet-questionnaire" role="dialog" style="position:fixed;left:660px;top:300px;width:250px;height:200px;background:white"><form><h2>Анкета питомца</h2><p>1000 ₽ на вкусняшки питомцу. Реклама. erid 2VtzqxWGbw5.</p><input aria-label="Имя питомца"><button type="button">К розыгрышу</button></form></div>`);
                window.insertPetPromo(false);
                """);
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("petLeaks===0 && petActionClicks===0 && document.getElementById('pet-root').getBoundingClientRect().height===0 && document.getElementById('pet-art').getBoundingClientRect().height===0 && document.getElementById('pet-shade').getBoundingClientRect().height===0")=="true",
                "Mail Space pet raffle with bare erid and its artwork, close and shade are hidden before the first frame without entering the raffle");
            check(await core.ExecuteScriptAsync("document.getElementById('pet-quote').getBoundingClientRect().height>0 && document.getElementById('pet-questionnaire').getBoundingClientRect().height>0 && document.querySelector('#pet-questionnaire input').getBoundingClientRect().height>0")=="true",
                "pet raffle filtering preserves quoted mail and a working questionnaire with the same promotional words");
            await core.ExecuteScriptAsync("window.insertPetPromo(true)");
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("petLeaks===0 && petActionClicks===0 && document.getElementById('pet-root').getBoundingClientRect().height===0 && document.getElementById('pet-close').getBoundingClientRect().height===0")=="true",
                "reinserted pet raffle with a separate close control leaves no visible card or shaded shell");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await core.ExecuteScriptAsync("document.getElementById('pet-frame').getBoundingClientRect().height>0 && document.getElementById('pet-shade').getBoundingClientRect().height>0 && petActionClicks===0")=="true",
                "pausing filters restores the complete pet raffle and its shade without activating its CTA");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("document.getElementById('pet-root').getBoundingClientRect().height===0 && petActionClicks===0 && document.getElementById('pet-quote').getBoundingClientRect().height>0 && document.getElementById('pet-questionnaire').getBoundingClientRect().height>0")=="true",
                "resuming filters hides the pet raffle again while retaining mail and working forms");
            check(await core.ExecuteScriptAsync("document.querySelectorAll('.vkuiPopoutRoot').length===0")=="true",
                "campaign-module fixture starts before the dedicated popup exists");
            await core.ExecuteScriptAsync("""
                window.moduleActionClicks=0; window.moduleLeaks=0; window.moduleImmediatelyHidden=true;
                window.insertCampaignModule=variant=>{
                  document.getElementById('campaign-module')?.remove();
                  const title=variant?'Новый формат привычных вещей':'Знакомьтесь: события рядом';
                  const action=variant?'Посмотреть историю':'Открыть подборку';
                  document.body.insertAdjacentHTML('beforeend',`<div id="campaign-module" class="vkuiPopoutRoot" style="position:fixed;inset:0"><div id="module-shade" style="position:absolute;inset:0;background:#8888"></div><section id="module-frame" style="position:absolute;left:220px;top:90px;width:480px;height:400px;background:white"><div id="module-art" role="img" aria-label="Synthetic campaign artwork" style="height:210px;background:linear-gradient(#def,#acd)"></div><h2>${title}</h2><p>Здесь совершенно другой текст без маркировки.</p><a href="https://vk.cc/d2TCyA" onclick="event.preventDefault();window.moduleActionClicks++">${action}</a></section><button id="module-close" aria-label="Закрыть" style="position:absolute;left:660px;top:98px;width:32px;height:32px">×</button></div>`);
                  const visible=()=>['campaign-module','module-shade','module-frame','module-art','module-close'].some(id=>document.getElementById(id).getBoundingClientRect().height>0);
                  if(visible())window.moduleImmediatelyHidden=false;
                  requestAnimationFrame(()=>{if(visible())window.moduleLeaks++});
                };
                document.body.insertAdjacentHTML('beforeend',`<div class="letter-body" id="module-quoted-mail" style="height:100px"><div class="vkuiPopoutRoot" id="module-inside-mail" style="height:80px"><a href="https://vk.cc/reference">Ссылка из письма</a></div></div><div class="vkuiPopoutRoot" id="module-containing-mail" style="height:100px"><div class="letter-body" style="height:80px"><a href="https://vk.cc/reference">Текст письма внутри оболочки</a></div></div><div class="vkuiPopoutRoot" id="module-working-form" style="height:120px"><form><label>Имя<input aria-label="Имя"></label><a href="https://vk.cc/reference">Справка к анкете</a><button type="button">Сохранить</button></form></div><div class="vkuiPopoutRoot" id="module-without-link" style="height:80px"><h2>Новый формат привычных вещей</h2><button>Посмотреть историю</button></div><div class="vkuiPopoutRootExample" id="module-class-lookalike" style="height:80px"><a href="https://vk.cc/reference">Обычное содержимое</a></div>`);
                window.insertCampaignModule(false);
                """);
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("moduleImmediatelyHidden && moduleLeaks===0 && moduleActionClicks===0")=="true",
                "dedicated Mail campaign module and full shade stay hidden immediately on late insertion without relying on title, CTA or erid");
            check(await core.ExecuteScriptAsync("['module-quoted-mail','module-inside-mail','module-containing-mail','module-working-form','module-without-link','module-class-lookalike'].every(id=>document.getElementById(id).getBoundingClientRect().height>0) && document.querySelector('#module-working-form input').getBoundingClientRect().height>0")=="true",
                "campaign-module rule preserves quoted mail, nested message content, working forms, unrelated roots and similar class names");
            await core.ExecuteScriptAsync("window.insertCampaignModule(true)");
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("moduleImmediatelyHidden && moduleLeaks===0 && moduleActionClicks===0 && document.getElementById('module-close').getBoundingClientRect().height===0")=="true",
                "campaign-module replacement with completely different text cannot paint its artwork, close control or backdrop");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await core.ExecuteScriptAsync("document.getElementById('module-frame').getBoundingClientRect().height>0 && document.getElementById('module-shade').getBoundingClientRect().height>0 && moduleActionClicks===0")=="true",
                "pausing filters restores the dedicated campaign module without activating a link");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            check(await core.ExecuteScriptAsync("document.getElementById('campaign-module').getBoundingClientRect().height===0 && document.getElementById('module-working-form').getBoundingClientRect().height>0 && moduleActionClicks===0")=="true",
                "resuming filters immediately hides the campaign module while keeping its working-form exception");
            await CheckMaxPromo();
            await Navigate("https://auth.mail.ru/login/render-test"); await Ready(session);
            check(!core.Settings.IsPasswordAutosaveEnabled && !core.Settings.IsWebMessageEnabled,"official authentication retains disabled password storage and host messaging");
            await core.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<div class=vkuiPopoutRoot id=auth-module style=\"height:80px\"><a href=\"https://vk.cc/reference\">Справка входа</a></div>')");
            check(await core.ExecuteScriptAsync("document.getElementById('auth-module').getBoundingClientRect().height>0")=="true",
                "dedicated campaign-module rule does not run on official authentication pages");
            paused=true;await session.ApplySettings();await Navigate("https://e.mail.ru/inbox/render-first");
            check(await core.ExecuteScriptAsync("document.getElementById('banner').getBoundingClientRect().height>0")=="true","pausing filters restores the original page without a waiting surface");
            session.SetActive(false);check(session.Surface.Visibility==Visibility.Collapsed,"inactive account hides its entire browser surface");
            await session.BackgroundSuspension;check(session.View.CoreWebView2.IsSuspended,"inactive account releases WebView2 renderer resources");
            session.SetActive(true);check(session.View.Visibility==Visibility.Visible&&!session.View.CoreWebView2.IsSuspended,"return to an account resumes its preserved WebView immediately");
            session.SetReadingOnly(true);check(session.View.Visibility==Visibility.Hidden,"native reading continues to collect with a hidden browser");
        }
        finally { host.Children.Remove(session.Surface); }

        async Task CheckMaxPromo()
        {
            var core = session.View.CoreWebView2;
            await core.ExecuteScriptAsync("""
                window.maxPromoFrames=0;
                window.maxPromoLeaks=0;
                window.maxPromoClicks=0;
                const promoCopy=`
                  <div role="img" aria-label="Synthetic MAX artwork" style="height:170px;background:linear-gradient(#acf,#bae)"></div>
                  <h2><span>Важные уведомления</span><span> от Mail </span><span>теперь в </span><strong>MAX</strong></h2>
                  <p>Получайте сообщения о новых письмах и напоминания о поездках — в MAX на любом устройстве.</p>
                  <button type="button" onclick="window.maxPromoClicks++">Попробовать</button>
                  <a href="https://max.ru/" onclick="event.preventDefault();window.maxPromoClicks++">Подробнее</a>`;
                const cardStyle='position:fixed;left:16px;bottom:16px;width:300px;height:400px;box-sizing:border-box;padding:12px;background:white;border-radius:16px';
                document.body.insertAdjacentHTML('beforeend',`
                  <div class="letter-body" id="max-quote"><section id="max-quoted-card" style="${cardStyle}">${promoCopy}</section></div>
                  <div id="max-working-toast" role="status" style="position:fixed;left:16px;bottom:16px;width:300px;height:100px;background:white">
                    <h2>Письмо отправлено</h2><button type="button" aria-label="Закрыть" onclick="window.maxPromoClicks++">×</button>
                  </div>`);
                window.insertMaxPromo=()=>{
                  document.getElementById('max-promo')?.remove();
                  document.body.insertAdjacentHTML('beforeend',`<section id="max-promo" style="${cardStyle}">${promoCopy}</section>`);
                  requestAnimationFrame(()=>{
                    if(document.getElementById('max-promo').getBoundingClientRect().height>0)window.maxPromoLeaks++;
                    window.maxPromoFrames++;
                  });
                };
                window.insertMaxPromo();
                """);
            await WaitForFrame(1);
            check(await core.ExecuteScriptAsync("maxPromoLeaks===0 && maxPromoClicks===0 && document.getElementById('max-promo').getBoundingClientRect().height===0") == "true",
                "unmarked bottom-left MAX notification offer is hidden before its first frame without activating either link");
            await core.ExecuteScriptAsync("window.insertMaxPromo()");
            await WaitForFrame(2);
            check(await core.ExecuteScriptAsync("maxPromoLeaks===0 && maxPromoClicks===0 && document.getElementById('max-promo').getBoundingClientRect().height===0") == "true",
                "a reinserted MAX notification offer also stays hidden before paint");
            check(await core.ExecuteScriptAsync("document.getElementById('max-quoted-card').getBoundingClientRect().height>0 && document.getElementById('max-working-toast').getBoundingClientRect().height>0 && maxPromoClicks===0") == "true",
                "MAX filtering preserves the same copy inside a letter and a working bottom-left notification");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, false, false));
            check(await core.ExecuteScriptAsync("document.getElementById('max-promo').getBoundingClientRect().height>0 && maxPromoClicks===0") == "true",
                "pausing filters restores the MAX offer without clicking any controls");
            await core.ExecuteScriptAsync(CosmeticScript.Create(store.Rules, true, false));
            check(await core.ExecuteScriptAsync("document.getElementById('max-promo').getBoundingClientRect().height===0 && document.getElementById('max-quoted-card').getBoundingClientRect().height>0 && document.getElementById('max-working-toast').getBoundingClientRect().height>0 && maxPromoClicks===0") == "true",
                "resuming filters hides the MAX offer again while preserving mail and the working notification");

            async Task WaitForFrame(int count)
            {
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    if (await core.ExecuteScriptAsync($"window.maxPromoFrames >= {count}") == "true") return;
                    await Task.Delay(25);
                }
                throw new TimeoutException("MAX promotion frame audit did not run");
            }
        }

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
