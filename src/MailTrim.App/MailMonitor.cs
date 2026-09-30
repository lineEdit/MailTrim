using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.App;

public sealed class MailMonitor(LocalStore store, Grid host, Action<int> notify, Func<bool> canCheck, Action<Guid, MailboxStatus> status) : IDisposable
{
    private readonly NewMailTracker tracker = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? cancellation;
    private Task? running;
    private bool disposed;
    public void Start()
    {
        timer.Tick += (_, _) => { timer.Interval = TimeSpan.FromMinutes(2); RequestCheck(); };
        timer.Start();
    }
    public void RequestCheck() { if (!disposed && canCheck() && (running is null || running.IsCompleted)) running = Check(); }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (running is not null) await running;
    }
    private async Task Check()
    {
        if (!store.Settings.NotifyNewMail) tracker.Clear();
        cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        try
        {
            foreach (var profile in store.Settings.Profiles.ToArray())
            {
                token.ThrowIfCancellationRequested();
                status(profile.Id, new("Проверка…", null, null));
                using var view = new WebView2 { Visibility = Visibility.Hidden };
                host.Children.Add(view);
                try
                {
                    var environment = await CoreWebView2Environment.CreateAsync(null, store.ProfilePath(profile.Id));
                    token.ThrowIfCancellationRequested();
                    await view.EnsureCoreWebView2Async(environment);
                    var core = view.CoreWebView2;
                    core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.NavigationStarting += (_, e) => e.Cancel = !NavigationPolicy.IsInternal(e.Uri);
                    core.NewWindowRequested += (_, e) => e.Handled = true;
                    core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false; };
                    core.DownloadStarting += (_, e) => e.Cancel = true;
                    core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested += (_, e) => { if (store.Rules.ShouldBlock(e.Request.Uri, store.Settings.BlockRequests)) e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", ""); };
                    token.ThrowIfCancellationRequested(); core.Navigate("https://e.mail.ru/inbox/");
                    bool completed = false;
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        await Task.Delay(1000, token);
                        // IDs only, never subjects, bodies, contacts or authentication data.
                        var urls = JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync("location.origin==='https://e.mail.ru' ? [...document.querySelectorAll('a.js-letter-list-item')].slice(0,100).map(a=>a.href) : []")) ?? [];
                        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var page) && NavigationPolicy.IsInternal(core.Source) && page.Host != "e.mail.ru")
                        { status(profile.Id, new("Нужен вход", null, null)); completed = true; break; }
                        var snapshot = JsonSerializer.Deserialize<MailboxStatusScript.SnapshotResult>(await core.ExecuteScriptAsync(MailboxStatusScript.Snapshot), FilterRules.Json);
                        if (urls.Length == 0 && snapshot?.Ready != true) continue;
                        status(profile.Id, new("Проверено", snapshot?.Unread, DateTimeOffset.Now)); completed = true;
                        var added = tracker.Observe(profile.Id, urls);
                        if (added > 0 && store.Settings.NotifyNewMail) notify(added);
                        break;
                    }
                    if (!completed) status(profile.Id, new(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "Не удалось проверить" : "Нет сети", null, null));
                }
                catch (OperationCanceledException) { status(profile.Id, new("Проверка отложена", null, null)); throw; }
                catch { status(profile.Id, new("Ошибка проверки", null, null)); }
                finally { host.Children.Remove(view); }
            }
        }
        catch (OperationCanceledException) { }
        catch { store.Log("mail_monitor_failed"); }
        finally { cancellation.Dispose(); cancellation = null; }
    }
    public void Dispose() { disposed = true; timer.Stop(); cancellation?.Cancel(); tracker.Clear(); }
}





