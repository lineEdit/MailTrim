using System.IO;
using System.Text.Json;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.App;

public sealed class ReaderSource(BrowserSession session)
{
    public async Task<T?> Extract<T>(string script) => JsonSerializer.Deserialize<T>(await session.View.CoreWebView2.ExecuteScriptAsync(script), FilterRules.Json);
    public async Task Navigate(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!NavigationPolicy.IsMail(url)) throw new IOException("mail_origin_required");
        var core = session.View.CoreWebView2;
        var ready = new TaskCompletionSource(); ulong? id = null;
        void Started(object? s, CoreWebView2NavigationStartingEventArgs e) => id = e.NavigationId;
        void Loaded(object? s, CoreWebView2DOMContentLoadedEventArgs e) { if (id == e.NavigationId) ready.TrySetResult(); }
        core.NavigationStarting += Started; core.DOMContentLoaded += Loaded;
        try { core.Navigate(url); await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), token); }
        finally { core.NavigationStarting -= Started; core.DOMContentLoaded -= Loaded; }
    }
    public async Task<List<ReaderBlock>> Read(ReaderLetter letter, CancellationToken token)
    {
        await Navigate(letter.Url, token);
        for (var i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!NavigationPolicy.IsMail(session.View.CoreWebView2.Source)) throw new IOException("sign_in_required");
            var blocks = await Extract<List<ReaderBlock>>(ReaderScript.Body);
            if (blocks?.Count > 0) return ReaderData.Clean(blocks);
            await Task.Delay(150, token);
        }
        throw new IOException("message_not_ready");
    }

    // Discover folder URLs only, never message links, actions, settings or external pages.
    public const string Folders = """
    (()=>location.origin!=='https://e.mail.ru'?[]:[...new Set([...document.querySelectorAll('a[href]')]
      .filter(a=>!a.matches('.js-letter-list-item')).map(a=>new URL(a.href,location.href))
      .filter(u=>u.origin===location.origin && /^\/(inbox|sent|drafts|archive|spam|trash|\d+|folder\/[^/]+)\/?$/.test(u.pathname))
      .map(u=>u.origin+u.pathname))])()
    """;
    public const string Advance = """
    (()=>{
      let e=document.querySelector('a.js-letter-list-item');
      while(e && !(e.scrollHeight>e.clientHeight+2 && /auto|scroll/.test(getComputedStyle(e).overflowY))) e=e.parentElement;
      e=e||document.querySelector('.ReactVirtualized__List');
      if(!e)return {found:false,bottom:false};
      const bottom=e.scrollTop+e.clientHeight>=e.scrollHeight-3;
      if(!bottom)e.scrollTop+=Math.max(100,e.clientHeight*.8);
      return {found:true,bottom};
    })()
    """;
    public sealed record ScrollState(bool Found, bool Bottom);
    public sealed record BatchResult(int Saved, int Failed, int Folders, int UncertainFolders);
    public async Task<BatchResult> CacheMailbox(Action<string> progress, Action<ReaderLetter> discovered, CancellationToken token)
    {
        var folders = new Queue<string>(); var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        void AddFolder(string url) { if (NavigationPolicy.IsMail(url) && seenFolders.Add(url.TrimEnd('/'))) folders.Enqueue(url); }
        foreach (var name in new[] { "inbox", "sent", "archive", "drafts", "spam", "trash" }) AddFolder("https://e.mail.ru/" + name + "/");
        var messages = new Dictionary<string, ReaderLetter>();
        int scanned = 0, uncertain = 0, saved = 0, failed = 0;
        while (folders.TryDequeue(out var folder))
        {
            token.ThrowIfCancellationRequested();
            progress($"Поиск писем: папка {++scanned}, найдено {messages.Count}…");
            await Navigate(folder, token);
            bool reachedEnd = false; int unchanged = 0; int previousCount = -1;
            for (var page = 0; page < 10000; page++)
            {
                await Task.Delay(700, token);
                if (!NavigationPolicy.IsMail(session.View.CoreWebView2.Source)) throw new IOException("sign_in_required");
                foreach (var extra in await Extract<List<string>>(Folders) ?? []) AddFolder(extra);
                var rows = await Extract<List<ReaderLetter>>(ReaderScript.List) ?? [];
                token.ThrowIfCancellationRequested();
                foreach (var row in rows.Select(ReaderData.Clean).OfType<ReaderLetter>())
                    if (messages.TryAdd(row.Url, row)) discovered(row);
                unchanged = messages.Count == previousCount ? unchanged + 1 : 0; previousCount = messages.Count;
                var scroll = await Extract<ScrollState>(Advance);
                progress($"Поиск писем: папка {scanned}, найдено {messages.Count}…");
                if (unchanged >= 6 && scroll?.Bottom == true) { reachedEnd = true; break; }
                if (unchanged >= 12) break;
            }
            if (!reachedEnd) uncertain++;
        }
        foreach (var letter in messages.Values)
        {
            token.ThrowIfCancellationRequested();
            progress($"Сохранение {saved + failed + 1}/{messages.Count} · ошибок {failed}");
            if (session.Cache.Get(letter.Url) is not null) { saved++; continue; }
            try
            {
                var blocks = await Read(letter, token);
                token.ThrowIfCancellationRequested();
                session.Cache.Save(letter, blocks); saved++;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (ex.Message is "cache_size_limit" or "sign_in_required") { throw; }
            catch { failed++; }
        }
        return new(saved, failed, scanned, uncertain);
    }
}
