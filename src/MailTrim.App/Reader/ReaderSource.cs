using System.IO;
using System.Text.Json;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.App;

public sealed class ReaderSource(BrowserSession session)
{
    public static bool IsFolder(string url) => NavigationPolicy.IsMail(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/(inbox|sent|drafts|archive|spam|trash|\d+|folder/[^/]+)/?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
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
        catch (OperationCanceledException) { core.Stop(); throw; }
        finally { core.NavigationStarting -= Started; core.DOMContentLoaded -= Loaded; }
    }
    public async Task<List<ReaderBlock>> Read(ReaderLetter letter, CancellationToken token)
    {
        await Navigate(letter.Url, token);
        string? groupSignature = null; int stableGroup = 0;
        for (var i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!NavigationPolicy.IsMail(session.View.CoreWebView2.Source)) throw new IOException("sign_in_required");
            var blocks = await Extract<List<ReaderBlock>>(ReaderScript.Body);
            if (blocks?.Count > 0) return ReaderData.Clean(blocks);
            var group = await Extract<ReaderListSnapshot>(ReaderScript.GroupSnapshot);
            if (group is { Ready: true } && group.Letters.All(x => x.Url != letter.Url))
            {
                var signature = string.Join('\n', group.Letters.Select(x => x.Url));
                stableGroup = signature == groupSignature ? stableGroup + 1 : 1; groupSignature = signature;
                // Avoid mistaking an intermediate SPA render for the final category.
                // An empty loading shell alone does not prove an empty category.
                if (stableGroup >= 6 && (group.Letters.Count > 0 || i >= 15 && (letter.IsGroup || group.Empty || IsFolder(letter.Url))))
                    throw new ReaderGroupOpened(letter, group.Letters.Select(ReaderData.Clean).OfType<ReaderLetter>().ToList());
            }
            else { stableGroup = 0; groupSignature = null; }
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
    public const string FolderDetails = """
    (()=>{
      if(location.origin!=='https://e.mail.ru')return [];
      const result=new Map();
      for(const a of document.querySelectorAll('a[href]')){
        if(a.matches('.js-letter-list-item'))continue;
        const u=new URL(a.href,location.href);
        if(u.origin!==location.origin || u.search || u.hash || !/^\/(inbox|sent|drafts|archive|spam|trash|\d+|folder\/[^/]+)\/?$/.test(u.pathname))continue;
        const copy=a.cloneNode(true);
        copy.querySelectorAll('sup, .nav__folder-counter, .nav__folder-count, [data-testid="unread-count"], [aria-hidden="true"]').forEach(e=>e.remove());
        const label=copy.querySelector('.nav__folder-name, .folder-name, [data-testid="folder-name"]');
        const name=(label?.textContent || a.getAttribute('title') || a.getAttribute('aria-label') || copy.textContent || '').replace(/\s+/g,' ').trim().slice(0,100);
        const url=u.origin+u.pathname.replace(/\/$/,'')+'/';
        if(!result.has(url) || !result.get(url).name)result.set(url,{name,url});
        if(result.size>=100)break;
      }
      return [...result.values()];
    })()
    """;
    public sealed record ScrollState(bool Found, bool Bottom);
    public sealed record BatchResult(int Saved, int Failed, int Folders, int UncertainFolders, int Skipped = 0);
    public async Task<BatchResult> CacheMailbox(Action<string> progress, Action<ReaderLetter> discovered, CancellationToken token, bool onlyRead = false, Action<MailboxCacheProgress>? detail = null)
    {
        var folders = new Queue<string>(); var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        void AddFolder(string url) { if (NavigationPolicy.IsMail(url) && seenFolders.Add(url.TrimEnd('/'))) folders.Enqueue(url); }
        foreach (var name in new[] { "inbox", "sent", "archive", "drafts", "spam", "trash" }) AddFolder("https://e.mail.ru/" + name + "/");
        var messages = new Dictionary<string, ReaderLetter>();
        var knownGroups = (await Task.Run(session.Cache.List, token)).Where(x => x.IsGroup).Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
        int scanned = 0, uncertain = 0, saved = 0, failed = 0, completed = 0;
        var draftUrls = new HashSet<string>(StringComparer.Ordinal);
        void Report(MailboxCacheProgress state) { progress(state.Description); detail?.Invoke(state); }
        while (folders.TryDequeue(out var folder))
        {
            token.ThrowIfCancellationRequested();
            Report(new(CacheStage.Discovering, ++scanned, seenFolders.Count, messages.Count));
            await Navigate(folder, token);
            bool reachedEnd = false; int unchanged = 0; int previousCount = -1;
            for (var page = 0; page < 10000; page++)
            {
                await Task.Delay(700, token);
                if (!NavigationPolicy.IsMail(session.View.CoreWebView2.Source)) throw new IOException("sign_in_required");
                foreach (var extra in await Extract<List<string>>(Folders) ?? []) AddFolder(extra);
                var rows = await Extract<List<ReaderLetter>>(ReaderScript.List) ?? [];
                token.ThrowIfCancellationRequested();
                var cleanRows = rows.Select(ReaderData.Clean).OfType<ReaderLetter>().Select(x => knownGroups.Contains(x.Url) ? x with { IsGroup = true } : x).ToArray();
                bool headersChanged = false;
                foreach (var row in cleanRows)
                {
                    if (folder.TrimEnd('/').EndsWith("/drafts", StringComparison.Ordinal)) draftUrls.Add(row.Url);
                    if (messages.TryAdd(row.Url, row)) { discovered(row); headersChanged = true; }
                    else if (row.Unread != false && messages[row.Url].Unread != row.Unread) { messages[row.Url] = row; headersChanged = true; } // Any unread/unknown observation prevents automatic opening.
                }
                if (headersChanged) await Task.Run(() => session.Cache.SaveHeaders(cleanRows, token), token);
                unchanged = messages.Count == previousCount ? unchanged + 1 : 0; previousCount = messages.Count;
                var scroll = await Extract<ScrollState>(Advance);
                Report(new(CacheStage.Discovering, scanned, seenFolders.Count, messages.Count));
                if (unchanged >= 6 && scroll?.Bottom == true) { reachedEnd = true; break; }
                if (unchanged >= 12) break;
            }
            if (!reachedEnd) uncertain++;
        }
        var eligible = messages.Values.Where(x => !x.IsGroup && (!onlyRead || x.Unread == false && !draftUrls.Contains(x.Url))).ToArray();
        var skipped = messages.Count - eligible.Length;
        MailboxCacheProgress Saving(CacheStage stage = CacheStage.Saving) => new(stage, scanned, seenFolders.Count, messages.Count,
            eligible.Length, completed, saved, failed, skipped, uncertain, DiscoveryComplete: true);
        Report(Saving());
        foreach (var letter in eligible)
        {
            token.ThrowIfCancellationRequested();
            if (await Task.Run(() => session.Cache.Get(letter.Url), token) is not null) { saved++; completed++; Report(Saving()); continue; }
            try
            {
                var blocks = await Read(letter, token);
                token.ThrowIfCancellationRequested();
                await Task.Run(() => session.Cache.Save(letter, blocks), token); saved++;
            }
            catch (ReaderGroupOpened group)
            {
                // A list is not a failed or cached message. Never open its unread children.
                await Task.Run(() => session.Cache.SaveHeaders([group.Letter with { IsGroup = true }], token), token);
                skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (ex.Message is "cache_size_limit" or "sign_in_required") { throw; }
            catch { failed++; }
            completed++; Report(Saving());
            await Task.Delay(350, token); // Keep request frequency modest, including in the tray.
        }
        Report(Saving(CacheStage.Complete));
        return new(saved, failed, scanned, uncertain, skipped);
    }
}

public sealed class ReaderGroupOpened(ReaderLetter letter, List<ReaderLetter> letters) : IOException("message_group_opened")
{
    public ReaderLetter Letter { get; } = letter;
    public List<ReaderLetter> Letters { get; } = letters;
}
