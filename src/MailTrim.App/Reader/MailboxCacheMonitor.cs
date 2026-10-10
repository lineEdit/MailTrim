using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>Separate official web view; never navigates the user's reader or reply editor.</summary>
public sealed class MailboxCacheMonitor(LocalStore store, Window owner, Grid host, Func<bool> canRun) : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<Guid, DateTimeOffset> nextRun = [];
    private CancellationTokenSource? cancellation;
    private Task running = Task.CompletedTask;
    private readonly DateTimeOffset startsAt = DateTimeOffset.UtcNow.AddSeconds(15);
    private bool disposed;
    private bool paused;
    public Task Running => running;
    public bool Paused => paused;

    public void Start()
    {
        timer.Tick += (_, _) => { timer.Interval = TimeSpan.FromMinutes(1); RequestCheck(); };
        timer.Start();
    }
    public void RequestCheck(bool now = false)
    {
        if (disposed || paused || !store.Settings.AutomaticMailboxCache || !canRun() || !running.IsCompleted) return;
        if (!now && DateTimeOffset.UtcNow < startsAt) return;
        if (now) nextRun.Clear();
        running = Check();
    }
    public async Task PauseAsync() { paused = true; await StopAsync(); }
    public void Resume() { paused = false; RequestCheck(true); }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        await running;
    }
    private async Task Check()
    {
        using var stop = new CancellationTokenSource(); cancellation = stop;
        try
        {
            foreach (var profile in store.Settings.Profiles.Where(p => p.Provider == MailProvider.MailRu).ToArray())
            {
                stop.Token.ThrowIfCancellationRequested();
                if (!store.Settings.AutomaticMailboxCache || !canRun()) return;
                if (nextRun.TryGetValue(profile.Id, out var next) && DateTimeOffset.UtcNow < next) continue;
                using var collector = new BrowserSession(store, owner, _ => { }, () => false);
                collector.View.Visibility = Visibility.Hidden; collector.View.IsHitTestVisible = false;
                host.Children.Add(collector.View);
                MailboxCacheProgress progress = new(CacheStage.Discovering, 0, 6, 0);
                try
                {
                    store.ReportCache(profile.Id, progress);
                    await collector.Initialize(profile.Id, background: true);
                    stop.Token.ThrowIfCancellationRequested();
                    await collector.ReaderData.CacheMailbox(_ => { }, _ => { }, stop.Token, onlyRead: true,
                        detail: state => { progress = state; store.ReportCache(profile.Id, state); });
                    nextRun[profile.Id] = DateTimeOffset.UtcNow.AddMinutes(15);
                }
                catch (OperationCanceledException)
                {
                    store.ReportCache(profile.Id, progress with { Stage = CacheStage.Stopped });
                    throw;
                }
                catch
                {
                    store.ReportCache(profile.Id, progress with { Stage = CacheStage.Failed });
                    nextRun[profile.Id] = DateTimeOffset.UtcNow.AddMinutes(2);
                    store.Log("automatic_cache_deferred");
                }
                finally { host.Children.Remove(collector.View); }
            }
        }
        catch (OperationCanceledException) { }
        finally { cancellation = null; }
    }
    public void Dispose() { disposed = true; timer.Stop(); cancellation?.Cancel(); }
}
