using System.IO;
using System.Threading.Channels;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>One bounded queue per mailbox. All WebView work stays on its owning UI thread.</summary>
public sealed class ReaderDataService : IDisposable
{
    private readonly BrowserSession session;
    private readonly ReaderSource source;
    private readonly Channel<Func<Task>> requests = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    public ReaderDataService(BrowserSession session)
    {
        this.session = session; source = new ReaderSource(session); worker = Process();
    }
    private async Task Process()
    {
        // Capture WPF's synchronization context; WebView2 is thread-affine.
        await foreach (var request in requests.Reader.ReadAllAsync()) await request();
    }
    private async Task<T> Enqueue<T>(Func<CancellationToken, Task<T>> work, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = linked.Token;
        await requests.Writer.WriteAsync(async () =>
        {
            try { ct.ThrowIfCancellationRequested(); var value = await work(ct); ct.ThrowIfCancellationRequested(); completion.TrySetResult(value); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, ct);
        // Keep linked token alive until the queued operation has stopped, even on cancellation.
        return await completion.Task;
    }
    public Task<List<ReaderLetter>> LoadList(bool refresh, bool more, int offset, CancellationToken token) => Enqueue(async ct =>
    {
        if (refresh || more && (await source.Extract<List<ReaderLetter>>(ReaderScript.List))?.Count is not > 0)
            await source.Navigate("https://e.mail.ru/inbox/", ct);
        if (more) await session.View.CoreWebView2.ExecuteScriptAsync($"document.querySelector('.ReactVirtualized__List')?.scrollTo(0,{offset})");
        for (var n = 0; n < 20; n++)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await source.Extract<List<ReaderLetter>>(ReaderScript.List);
            if (rows?.Count > 0)
            {
                var clean = rows.Select(ReaderData.Clean).OfType<ReaderLetter>().ToList();
                foreach (var letter in clean) session.Cache.RefreshMetadata(letter);
                ct.ThrowIfCancellationRequested();
                try { await Task.Run(() => session.Cache.SaveHeaders(clean), ct); }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { /* Fresh rows remain usable if local storage is unavailable. */ }
                return ReaderChronology.NewestFirst(clean);
            }
            await Task.Delay(400, ct);
        }
        return new List<ReaderLetter>();
    }, token);
    public Task<ReaderDocument> Read(ReaderLetter letter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        letter = ReaderData.Clean(letter) ?? throw new IOException("mail_origin_required");
        if (session.Cache.Get(letter.Url) is { } cached)
        {
            session.Cache.RefreshMetadata(letter);
            return Task.FromResult(new ReaderDocument(letter, ReaderData.Clean(cached).AsReadOnly(), true, true));
        }
        return Enqueue(async ct =>
        {
            var blocks = ReaderData.Clean(await source.Read(letter, ct));
            ct.ThrowIfCancellationRequested(); bool saved = true;
            try { session.Cache.Save(letter, blocks); }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { saved = false; }
            return new ReaderDocument(letter, blocks.AsReadOnly(), false, saved);
        }, token);
    }
    public Task<string?> PerformAction(ReaderLetter letter, ReaderAction action, CancellationToken token) => Enqueue(async ct =>
    {
        await source.Read(letter, ct); ct.ThrowIfCancellationRequested();
        return await source.Extract<string>(ReaderActionScript.Create(action, letter.Url));
    }, token);
    public Task<ReaderSource.BatchResult> CacheMailbox(Action<string> progress, Action<ReaderLetter> discovered, CancellationToken token) =>
        Enqueue(ct => source.CacheMailbox(progress, discovered, ct), token);
    public void Dispose() { lifetime.Cancel(); requests.Writer.TryComplete(); }
}
