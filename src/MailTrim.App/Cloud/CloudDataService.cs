using System.IO;
using System.Text.Json;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;

namespace MailTrim.App;

// One cancellable read/navigation lane per profile; the hidden provider never
// opens files, selects rows or performs mutating site commands on its own.
public sealed class CloudDataService(BrowserSession session) : IDisposable
{
    private readonly SemaphoreSlim lane = new(1, 1);
    private CancellationTokenSource pending = new();
    private bool disposed;
    public void CancelPending()
    {
        if (disposed) return;
        var previous = pending; pending = new(); previous.Cancel(); previous.Dispose();
    }
    public async Task StopAsync()
    {
        CancelPending();
        await lane.WaitAsync(); lane.Release();
    }
    public async Task<CloudListing> LoadFolder(string url, bool refresh, CancellationToken token)
    {
        var folder = CloudData.CanonicalUrl(url);
        if (folder.Length == 0) throw new IOException("cloud_folder_required");
        return await Run(async ct =>
        {
            var core = session.CloudView?.CoreWebView2 ?? throw new IOException("cloud_not_initialized");
            var current = CloudData.CanonicalUrl(core.Source);
            if (refresh || current != folder) await Navigate(folder, ct);
            CloudListing? latest = null; var emptyCount = 0;
            for (var i = 0; i < 36; i++)
            {
                ct.ThrowIfCancellationRequested(); latest = await Read(ct);
                if (latest.AuthRequired || latest.Ready && latest.Items.Count > 0) return latest;
                if (latest.Ready && latest.Empty && ++emptyCount >= 2) return latest;
                if (!latest.Empty) emptyCount = 0;
                await Task.Delay(200, ct);
            }
            return latest ?? CloudData.Unknown(folder);
        }, token);
    }
    public Task<CloudListing> Snapshot(CancellationToken token) => Run(Read, token);
    public Task<CloudListing> LoadMore(CancellationToken token) => Run(async ct =>
    {
        var before = await Read(ct);
        if (!before.Ready || before.AuthRequired) return before;
        var core = session.CloudView!.CoreWebView2;
        var folder = CloudData.CanonicalUrl(core.Source);
        var result = await core.ExecuteScriptAsync(CloudScript.LoadMore);
        ct.ThrowIfCancellationRequested();
        using var moved = JsonDocument.Parse(result);
        if (!moved.RootElement.TryGetProperty("moved", out var flag) || !flag.GetBoolean())
            return before with { Partial = true, Message = "Новые строки не появились. Полнота списка сайтом не подтверждена." };
        var previous = string.Join('\n', before.Items.Select(item => item.Url));
        var captured = before.Items.ToDictionary(item => item.Url, StringComparer.Ordinal);
        CloudListing latest = before;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(150, ct); latest = await Read(ct);
            if (latest.AuthRequired) return latest;
            if (CloudData.CanonicalUrl(core.Source) != folder) return CloudData.Unknown(folder);
            if (latest.Ready) foreach (var item in latest.Items) if (captured.Count < 2000 || captured.ContainsKey(item.Url)) captured[item.Url] = item;
            if (latest.Ready && string.Join('\n', latest.Items.Select(item => item.Url)) != previous)
                return latest with { Items = captured.Values.ToList(), Partial = true };
        }
        return latest with { Items = latest.Ready ? captured.Values.ToList() : latest.Items, Partial = true, Message = "Новые строки не появились. Полнота списка сайтом не подтверждена." };
    }, token);
    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, pending.Token);
        await lane.WaitAsync(linked.Token);
        try { linked.Token.ThrowIfCancellationRequested(); var value = await action(linked.Token); linked.Token.ThrowIfCancellationRequested(); return value; }
        finally { lane.Release(); }
    }
    private async Task<CloudListing> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var core = session.CloudView?.CoreWebView2 ?? throw new IOException("cloud_not_initialized");
        var json = await core.ExecuteScriptAsync(CloudScript.Snapshot);
        token.ThrowIfCancellationRequested();
        if (json.Length > 4_000_000) return CloudData.Unknown(CloudData.CanonicalUrl(core.Source), "Список файлов слишком большой для одной страницы.");
        try { return CloudData.Clean(JsonSerializer.Deserialize<CloudListing>(json, FilterRules.Json)); }
        catch (JsonException) { return CloudData.Unknown(CloudData.CanonicalUrl(core.Source)); }
    }
    private async Task Navigate(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var core = session.CloudView!.CoreWebView2;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ulong? id = null;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e) { if (id is null && e.Uri == url) id = e.NavigationId; }
        void Loaded(object? s, CoreWebView2DOMContentLoadedEventArgs e) { if (e.NavigationId == id) done.TrySetResult(); }
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId != id) return;
            if (e.IsSuccess) done.TrySetResult();
            else done.TrySetException(new IOException("cloud_navigation_failed"));
        }
        core.NavigationStarting += Starting; core.DOMContentLoaded += Loaded; core.NavigationCompleted += Completed;
        try { core.Navigate(url); await done.Task.WaitAsync(TimeSpan.FromSeconds(25), token); }
        catch (OperationCanceledException) { core.Stop(); throw; }
        finally { core.NavigationStarting -= Starting; core.DOMContentLoaded -= Loaded; core.NavigationCompleted -= Completed; }
    }
    public void Dispose() { if (disposed) return; pending.Cancel(); pending.Dispose(); disposed = true; }
}
