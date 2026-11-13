using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.App;

public sealed class BrowserSession : IDisposable
{
    public WebView2 View { get; } = new();
    public BrowserSurface Surface { get; }
    public WebView2? CloudView { get; private set; }
    public BrowserSurface? CloudSurface { get; private set; }
    public bool CloudOpen { get; private set; }
    public bool CloudOriginal { get; private set; }
    public WebView2 ActiveView => CloudOpen && CloudView is not null ? CloudView : View;
    public event Action<string>? CloudRequested;
    public event Action? MailRequested;
    public event Action? SignInStarted;
    public event Action? EmbeddedMailboxReady;
    public bool SignInInProgress => signInViews.Count > 0;
    public Guid ProfileId { get; private set; }
    public LocalStore Store => store;
    private ReaderDataService? readerData;
    public ReaderDataService ReaderData => readerData ??= new ReaderDataService(this);
    public bool ReadingOnly { get; private set; }
    private bool active;
    private bool hostVisible = true;
    private int suspensionVersion;
    public Task BackgroundSuspension { get; private set; } = Task.CompletedTask;
    public void SetReadingOnly(bool reading)
    {
        ReadingOnly = reading;
        UpdateSurfaces();
    }
    public ReaderPosition? ReaderState { get; set; }
    private bool readerReplyOpen;
    public bool ReaderReplyOpen { get => readerReplyOpen; set { readerReplyOpen = value; UpdateSurfaces(); } }
    public MessageCache Cache { get; private set; } = null!;
    private readonly LocalStore store;
    private readonly Window owner;
    private readonly Action<string> status;
    private readonly Func<bool> paused;
    private readonly Action<string>? externalHandler;
    private readonly List<(Window Window, WebView2 View)> popups = [];
    private readonly Dictionary<WebView2, string> scripts = [];
    private readonly HashSet<CoreWebView2> signInViews = [];
    private CoreWebView2Frame? mailFrame;
    private string? mailFrameSource;
    private bool embeddedMailboxReady;
    internal bool OriginalRequested { get; private set; }
    internal bool IsOpeningEmbeddedMailbox => !OriginalRequested && Surface.ProviderShellHidden && !HasEmbeddedMailbox;
    internal void SetOriginalRequested(bool value)
    {
        OriginalRequested = value;
        if (value) Surface.HideProviderShell(false);
    }
    private const string MailboxDocumentReady = """
        location.origin === 'https://e.mail.ru' &&
        !!document.querySelector('a.js-letter-list-item, .letter-list__react, .letter-list, .llct, .thread, .letter-body__body, .compose-app')
        """;

    internal bool HasEmbeddedMailbox => mailFrame is not null && mailFrameSource is not null
        && View.CoreWebView2 is { } core && NavigationPolicy.IsMailWorkspace(core.Source);
    internal string MailSource => HasEmbeddedMailbox ? mailFrameSource! : View.CoreWebView2.Source;
    internal Task<string> ExecuteMailScript(string script) => HasEmbeddedMailbox
        ? mailFrame!.ExecuteScriptAsync(script)
        : View.CoreWebView2.ExecuteScriptAsync(script);

    internal async Task NavigateEmbeddedMail(string url, CancellationToken token)
    {
        if (!NavigationPolicy.IsMail(url) || !HasEmbeddedMailbox) throw new System.IO.IOException("mail_origin_required");
        var frame = mailFrame!;
        ulong? navigation = null;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri == url) navigation = e.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigation == e.NavigationId) loaded.TrySetResult(e.IsSuccess);
        }
        void Destroyed(object? sender, object e) => loaded.TrySetResult(false);
        frame.NavigationStarting += Starting;
        frame.NavigationCompleted += Completed;
        frame.Destroyed += Destroyed;
        try
        {
            token.ThrowIfCancellationRequested();
            var destination = JsonSerializer.Serialize(url);
            // Schedule after the script result is returned so navigation cannot interrupt it.
            var scheduled = await frame.ExecuteScriptAsync(
                $"location.origin === 'https://e.mail.ru' && (setTimeout(() => location.assign({destination}), 0), true)");
            if (scheduled != "true" || !await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), token))
                throw new System.IO.IOException("mail_frame_unavailable");
        }
        finally
        {
            if (frame.IsDestroyed() == 0)
            {
                frame.NavigationStarting -= Starting;
                frame.NavigationCompleted -= Completed;
                frame.Destroyed -= Destroyed;
            }
        }
    }
    private CoreWebView2Environment? environment;
    private bool disposed;
    private bool clearing;
    private int blocked;
    public double ReaderListWidth => double.IsFinite(store.Settings.ReaderListWidth) ? Math.Clamp(store.Settings.ReaderListWidth, 260, 520) : 310;
    public bool ReaderFoldersVisible => store.Settings.ReaderFoldersVisible;
    public void SaveReaderFoldersVisible(bool visible)
    {
        var previous = store.Settings.ReaderFoldersVisible;
        store.Settings.ReaderFoldersVisible = visible;
        try { store.Save(); } catch { store.Settings.ReaderFoldersVisible = previous; status("Не удалось сохранить видимость папок."); }
    }
    public void SaveReaderListWidth(double width)
    {
        if (!double.IsFinite(width)) return;
        var previous = store.Settings.ReaderListWidth;
        store.Settings.ReaderListWidth = Math.Clamp(width, 260, 520);
        try { store.Save(); } catch { store.Settings.ReaderListWidth = previous; status("Не удалось сохранить ширину списка."); }
    }

    public void SetActive(bool active)
    {
        this.active = active;
        // A suspended WebView2 must be resumed while its controller is still hidden.
        // Making the WPF host visible first can leave the compositor showing a white
        // surface even though CoreWebView2 reports that Resume() succeeded.
        UpdateResourceState();
        UpdateSurfaces();
        foreach (var p in popups.ToArray()) { if (active) p.Window.Show(); else p.Window.Hide(); }
        if (active) QueueResumeVerification(suspensionVersion);
    }

    public void SetHostVisible(bool visible)
    {
        hostVisible = visible;
        UpdateResourceState();
    }

    private void UpdateResourceState()
    {
        var version = ++suspensionVersion;
        if (NeedsLiveWebView) ResumeWebViews();
        else BackgroundSuspension = SuspendWebViews(version);
    }

    private bool NeedsLiveWebView => active && (hostVisible || store.Settings.UseSiteNotifications);

    private void ResumeWebViews()
    {
        foreach (var core in WebViewCores())
            if (core.IsSuspended) core.Resume();
    }

    private void QueueResumeVerification(int version)
    {
        _ = owner.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || !NeedsLiveWebView || version != suspensionVersion) return;
            ResumeWebViews();
            View.InvalidateVisual();
            CloudView?.InvalidateVisual();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private async Task SuspendWebViews(int version)
    {
        try
        {
            // Let WPF apply Collapsed to the native controllers before WebView2 checks visibility.
            await owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (disposed || NeedsLiveWebView || version != suspensionVersion) return;
            foreach (var core in WebViewCores())
            {
                if (disposed || NeedsLiveWebView || version != suspensionVersion) return;
                if (!core.IsSuspended) await core.TrySuspendAsync();
                if (NeedsLiveWebView || version != suspensionVersion) { if (core.IsSuspended) core.Resume(); return; }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Suspension is an optimization. A navigating or closing view may reject it.
        }
    }

    private IEnumerable<CoreWebView2> WebViewCores()
    {
        if (View.CoreWebView2 is { } mail) yield return mail;
        if (CloudView?.CoreWebView2 is { } cloud) yield return cloud;
    }

    private void UpdateSurfaces()
    {
        if (disposed) return;
        Surface.SetRequestedVisibility(!active || CloudOpen ? Visibility.Collapsed : ReadingOnly && !ReaderReplyOpen ? Visibility.Hidden : Visibility.Visible);
        CloudSurface?.SetRequestedVisibility(!active || !CloudOpen ? Visibility.Collapsed : CloudOriginal ? Visibility.Visible : Visibility.Hidden);
        if (View.CoreWebView2 is { } mailCore) mailCore.Settings.AreDefaultScriptDialogsEnabled = !SourceIsHidden(false);
        if (CloudView?.CoreWebView2 is { } cloudCore) cloudCore.Settings.AreDefaultScriptDialogsEnabled = !SourceIsHidden(true);
    }
    private bool SourceIsHidden(bool cloud) => cloud
        ? !CloudOriginal || CloudSurface?.RequestedVisibility != Visibility.Visible
        : CloudOpen || Surface.RequestedVisibility != Visibility.Visible || ReadingOnly && !ReaderReplyOpen;
    public void SetCloudOpen(bool open)
    {
        CloudOpen = open && CloudView?.CoreWebView2 is not null;
        UpdateSurfaces();
    }
    public void SetCloudOriginal(bool original)
    {
        CloudOriginal = original && CloudView?.CoreWebView2 is not null;
        UpdateSurfaces();
    }
    public async Task ShowCloudSignIn(string? loginUrl, Action finished)
    {
        if (disposed || environment is null || popups.Count >= 4) return;
        var url = NavigationPolicy.IsInternal(loginUrl ?? "") && !NavigationPolicy.IsMail(loginUrl!) && !NavigationPolicy.IsCloud(loginUrl!)
            ? loginUrl! : "https://account.mail.ru/login/";
        var child = new WebView2();
        var window = new Window { Owner = owner, Title = "MailTrim • вход в Облако", Width = 720, Height = 780, Content = child, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        popups.Add((window, child));
        var enteredSignIn = false;
        window.Closed += (_, _) =>
        {
            if (child.CoreWebView2 is { } core) FinishSignIn(core);
            popups.RemoveAll(p => p.Window == window); scripts.Remove(child); child.Dispose();
            if (!disposed) finished();
        };
        try
        {
            window.Show(); await child.EnsureCoreWebView2Async(environment); await Configure(child, true, cloud: true);
            var core = child.CoreWebView2;
            core.NavigationStarting += (_, e) =>
            {
                enteredSignIn |= NavigationPolicy.IsAuthentication(e.Uri);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (enteredSignIn && e.IsSuccess && !signInViews.Contains(core)
                    && (NavigationPolicy.IsCloud(core.Source) || NavigationPolicy.IsMail(core.Source)))
                    _ = owner.Dispatcher.BeginInvoke(() => { if (popups.Any(p => p.Window == window)) window.Close(); });
            };
            core.WindowCloseRequested += (_, _) => window.Close();
            core.Navigate(url);
        }
        catch { store.Log("cloud_signin_failed"); window.Close(); status("Не удалось открыть официальный вход в Облако."); }
    }
    public async Task EnsureCloud(Action<BrowserSurface>? mount = null)
    {
        if (disposed || environment is null) throw new InvalidOperationException("Profile is not initialized.");
        if (CloudView is not null) return;
        var view = new WebView2();
        CloudView = view; CloudSurface = new BrowserSurface(view);
        CloudSurface.SetRequestedVisibility(Visibility.Collapsed);
        try
        {
            mount?.Invoke(CloudSurface);
            // Share this account's cookie jar, never another account's or a system browser's.
            await view.EnsureCoreWebView2Async(environment);
            if (disposed) throw new ObjectDisposedException(nameof(BrowserSession));
            await Configure(view, false, cloud: true);
        }
        catch
        {
            if (CloudSurface?.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(CloudSurface);
            scripts.Remove(view); view.Dispose(); CloudView = null; CloudSurface = null; throw;
        }
    }

    public BrowserSession(LocalStore store, Window owner, Action<string> status, Func<bool> paused, Action<string>? externalHandler = null)
    {
        (this.store, this.owner, this.status, this.paused, this.externalHandler) = (store, owner, status, paused, externalHandler);
        Surface = new BrowserSurface(View);
    }

    public async Task Initialize(Guid profile, bool background = false)
    {
        ProfileId = profile; Cache = new MessageCache(store.Root, profile);
        environment = await CoreWebView2Environment.CreateAsync(null, store.ProfilePath(profile));
        if (disposed) return;
        await View.EnsureCoreWebView2Async(environment);
        if (disposed) return;
        await Configure(View, false, background);
        Surface.Initialize(background);
        if (!background) View.CoreWebView2.Navigate("https://e.mail.ru/inbox/");
    }

    private async Task Configure(WebView2 view, bool popup, bool background = false, bool cloud = false, Action? closeWorkspacePopup = null)
    {
        var core = view.CoreWebView2;
        var provisionalPopup = closeWorkspacePopup is not null;
        var popupRoutePending = false;
        ulong? signInReturnNavigation = null;
        ulong? signInNavigation = null;
        ulong? mailNavigation = null;
        var primary = ReferenceEquals(view, View) && !background;
        if (!background && signInViews.Count == 0)
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        if (!popup) core.Settings.AreDefaultScriptDialogsEnabled = !SourceIsHidden(cloud);
        if (primary)
        {
            core.FrameCreated += (_, e) => TrackMailboxFrame(core, e.Frame);
            core.SourceChanged += (_, _) =>
            {
                if (NavigationPolicy.IsMailWorkspace(core.Source)) return;
                mailFrame = null;
                mailFrameSource = null;
                embeddedMailboxReady = false;
                Surface.HideProviderShell(false);
            };
        }
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!signInViews.Contains(core)) return;
            if (NavigationPolicy.IsAuthentication(e.Uri)) store.Log("mail_signin_frame_provider");
            else if (NavigationPolicy.IsMail(e.Uri) || NavigationPolicy.IsCloud(e.Uri)) store.Log("mail_signin_frame_return");
        };
        core.FrameNavigationCompleted += (_, e) =>
        {
            if (signInViews.Contains(core) && !e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                LogSignInFailure(e.HttpStatusCode, "mail_signin_frame_failed");
        };
        core.NavigationStarting += (_, e) =>
        {
            if (popupRoutePending) { e.Cancel = true; return; }
            if (primary && NavigationPolicy.IsMail(e.Uri)) mailNavigation = e.NavigationId;
            var mailboxWorkspace = primary && NavigationPolicy.IsMailWorkspace(e.Uri);
            var fromMail = mailboxWorkspace && (mailNavigation == e.NavigationId
                || NavigationPolicy.IsMail(core.Source) || NavigationPolicy.IsMailWorkspace(core.Source));
            // The return can pass through app.mail.ru in the same sign-in view.
            // Keep tracking the flow until the mailbox itself loads.
            if (!background && !clearing && NavigationPolicy.IsSignInRelay(e.Uri)
                && (signInViews.Contains(core) || fromMail))
            {
                if (signInViews.Contains(core))
                {
                    signInReturnNavigation = null;
                    signInNavigation = e.NavigationId;
                    store.Log("mail_signin_app_relay_allowed");
                }
                if (mailboxWorkspace && !OriginalRequested && !ReaderReplyOpen)
                {
                    Surface.HideProviderShell(true);
                    status("Загружаю список писем…");
                    _ = ReportSlowMailbox(core);
                }
                return;
            }
            if (provisionalPopup && NavigationPolicy.IsInternal(e.Uri))
            {
                var mailTarget = NavigationPolicy.IsMail(e.Uri);
                var cloudTarget = NavigationPolicy.IsCloud(e.Uri);
                if (mailTarget || cloudTarget)
                {
                    e.Cancel = true; popupRoutePending = true;
                    var destination = e.Uri;
                    _ = owner.Dispatcher.BeginInvoke(() =>
                    {
                        if (disposed) return;
                        closeWorkspacePopup!();
                        if (mailTarget) MailRequested?.Invoke();
                        else CloudRequested?.Invoke(destination);
                    });
                    return;
                }
                // Once an official sign-in page owns a blank window, preserve its
                // opener and all subsequent authentication redirects in that window.
                provisionalPopup = false;
            }
            if (NavigationPolicy.IsCloud(e.Uri) && !cloud && !popup)
            {
                e.Cancel = true;
                if (!background && !SourceIsHidden(false)) owner.Dispatcher.BeginInvoke(() => { if (!disposed) CloudRequested?.Invoke(e.Uri); });
                return;
            }
            if (cloud && !popup && NavigationPolicy.IsMail(e.Uri))
            {
                e.Cancel = true;
                if (!SourceIsHidden(true)) owner.Dispatcher.BeginInvoke(() => { if (!disposed) MailRequested?.Invoke(); });
                return;
            }
            if (NavigationPolicy.IsInternal(e.Uri))
            {
                if (NavigationPolicy.IsAuthentication(e.Uri))
                {
                    if (background && !active) { e.Cancel = true; return; }
                    signInNavigation = e.NavigationId;
                    if (signInViews.Add(core))
                    {
                        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
                        store.Log("mail_signin_started");
                        SignInStarted?.Invoke();
                    }
                    if (signInReturnNavigation is not null) store.Log("mail_signin_returned_to_login");
                    signInReturnNavigation = null;
                    var host = new Uri(e.Uri).IdnHost;
                    store.Log(host is "id.vk.ru" or "id.vk.com" or "login.vk.ru" or "login.vk.com" or "oauth.vk.ru" or "oauth.vk.com"
                        ? "mail_signin_top_vk"
                        : "mail_signin_top_mail");
                    status("Официальный вход Mail.ru");
                }
                else if (signInViews.Contains(core))
                {
                    if (signInReturnNavigation is null) store.Log("mail_signin_return_started");
                    signInReturnNavigation = e.NavigationId;
                    status("Mail.ru возвращает вас в ящик…");
                }
                else if (!background) status("Загрузка официальной страницы…");
                return;
            }
            if ((popup || clearing) && e.Uri == "about:blank") return;
            e.Cancel = true;
            if (signInViews.Contains(core)) store.LogBlockedSignInDestination(e.Uri);
            if (background || !popup && SourceIsHidden(cloud)) return;
            // Do not leak an OAuth redirect URL into another browser automatically.
            if (NavigationPolicy.IsExternal(e.Uri) && e.IsUserInitiated) QueueExternal(e.Uri);
            else status("Переход на внешний сайт остановлен. Откройте ссылку вручную в браузере.");
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (mailNavigation == e.NavigationId) mailNavigation = null;
            if (background && !signInViews.Contains(core)) return;
            if (signInNavigation == e.NavigationId)
            {
                signInNavigation = null;
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    LogSignInFailure(e.HttpStatusCode, "mail_signin_provider_failed");
            }
            if (signInReturnNavigation == e.NavigationId)
            {
                signInReturnNavigation = null;
                if (e.IsSuccess && (NavigationPolicy.IsMail(core.Source) || NavigationPolicy.IsCloud(core.Source)))
                {
                    FinishSignIn(core);
                    store.Log("mail_signin_return_loaded");
                }
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    LogSignInFailure(e.HttpStatusCode, "mail_signin_return_failed");
            }
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            if (primary && e.IsSuccess && NavigationPolicy.IsMailWorkspace(core.Source))
            {
                if (!HasEmbeddedMailbox && !OriginalRequested) status("Загружаю список писем…");
                return;
            }
            if (e.IsSuccess && signInViews.Contains(core))
            {
                status("Официальный вход Mail.ru");
                return;
            }
            status(e.IsSuccess ? $"Готово · заблокировано запросов: {blocked}" : "Страница не загрузилась. Проверьте сеть или временно отключите фильтры.");
            if (!e.IsSuccess) store.Log("navigation_failed");
            else if (!popup && !cloud && NavigationPolicy.IsMail(core.Source)) _ = RecoverStalledMailRedirect(view, core.Source);
        };
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (signInViews.Contains(core)) store.Log("mail_signin_certificate_error");
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        // Let WebView2 own notification display/click/close. Drop unsolicited origins,
        // muted notifications and the alternate polling mode, even with a prior permission.
        core.NotificationReceived += (_, e) => e.Handled = background || !SiteNotificationPolicy.CanDeliver(store.Settings, e.SenderOrigin);
        core.PermissionRequested += async (_, e) =>
        {
            e.SavesInProfile = false;
            e.State = CoreWebView2PermissionState.Deny;
            if (background) return;
            if (!popup && SourceIsHidden(cloud)) return;
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                if (SiteNotificationPolicy.CanDeliver(store.Settings, e.Uri)) e.State = CoreWebView2PermissionState.Allow;
                return;
            }
            if (!e.IsUserInitiated || !NavigationPolicy.IsInternal(e.Uri)) return;
            using var deferral = e.GetDeferral();
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (!disposed && MessageBox.Show(owner, "Разрешить официальной странице доступ: " + e.PermissionKind + "?", "Разрешение сайта", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    e.State = CoreWebView2PermissionState.Allow;
            });
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            // Official Mail.ru/VK ID sign-in embeds resources from several provider
            // origins. Never apply advertising rules while that authentication
            // document is active or its return page is still loading.
            var enabled = store.Settings.BlockRequests && !paused() && !signInViews.Contains(core)
                && !NavigationPolicy.IsAuthentication(core.Source);
            var mailDocument = ReferenceEquals(view, View) && HasEmbeddedMailbox ? MailSource : core.Source;
            var promotionModule = !popup && !cloud && e.ResourceContext == CoreWebView2WebResourceContext.Script
                && PromotionModulePolicy.ShouldBlockScript(e.Request.Uri, mailDocument, store.Rules, enabled);
            if (!promotionModule && !store.Rules.ShouldBlock(e.Request.Uri, enabled)) return;
            e.Response = environment!.CreateWebResourceResponse(null, 403, "Blocked by MailTrim", "Cache-Control: no-store");
            blocked++;
            if (!background && (blocked == 1 || blocked % 10 == 0)) status($"Заблокировано запросов: {blocked}");
        };
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            if (signInViews.Contains(core)) store.Log("mail_signin_popup_requested");
            if (background) return;
            if (!popup && SourceIsHidden(cloud)) return;
            if (cloud && !popup && NavigationPolicy.IsMail(e.Uri))
            {
                _ = owner.Dispatcher.BeginInvoke(() => { if (!disposed) MailRequested?.Invoke(); });
                return;
            }
            if (NavigationPolicy.IsCloud(e.Uri))
            {
                if (cloud) core.Navigate(e.Uri);
                else _ = owner.Dispatcher.BeginInvoke(() => { if (!disposed) CloudRequested?.Invoke(e.Uri); });
                return;
            }
            if (!NavigationPolicy.IsInternal(e.Uri) && e.Uri != "about:blank")
            {
                if (signInViews.Contains(core)) store.LogBlockedSignInDestination(e.Uri, popup: true);
                if (e.IsUserInitiated && NavigationPolicy.IsExternal(e.Uri)) QueueExternal(e.Uri);
                return;
            }
            using var deferral = e.GetDeferral();
            if (popups.Count >= 4 || disposed) return;
            var child = new WebView2();
            var window = new Window { Owner = owner, Title = "MailTrim • окно сайта", Width = 720, Height = 780, Content = child, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            popups.Add((window, child));
            window.Closed += (_, _) =>
            {
                if (child.CoreWebView2 is { } popupCore) FinishSignIn(popupCore);
                popups.RemoveAll(p => p.Window == window); scripts.Remove(child); child.Dispose();
            };
            try
            {
                window.Show();
                await child.EnsureCoreWebView2Async(environment);
                await Configure(child, true, cloud: cloud, closeWorkspacePopup: cloud && !popup && e.Uri == "about:blank" ? window.Close : null);
                child.CoreWebView2.SourceChanged += (_, _) =>
                {
                    if (Uri.TryCreate(child.CoreWebView2.Source, UriKind.Absolute, out var origin))
                        window.Title = "MailTrim • " + origin.IdnHost;
                };
                child.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
                e.NewWindow = child.CoreWebView2; // Preserve opener + official authentication flow in this profile.
            }
            catch { store.Log("popup_failed"); window.Close(); status("Не удалось открыть окно авторизации."); }
        };
        core.DownloadStarting += async (_, e) =>
        {
            if (background || !popup && SourceIsHidden(cloud)) { e.Cancel = true; return; }
            // Use WebView2's download UI; always ask for a path, never auto-open attachments.
            using var deferral = e.GetDeferral();
            e.Cancel = true;
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                var dialog = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileName(e.ResultFilePath), Title = "Сохранить файл" };
                if (dialog.ShowDialog(owner) == true) { e.ResultFilePath = dialog.FileName; e.Cancel = false; }
            });
        };
        core.ProcessFailed += (_, _) => { store.Log("webview_process_failed"); status("Процесс WebView2 остановился. Перезапустите MailTrim."); };
        await SetScript(view);
        SetTheme(view);
    }

    private async Task ReportSlowMailbox(CoreWebView2 core)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (disposed || !IsOpeningEmbeddedMailbox || !NavigationPolicy.IsMailWorkspace(core.Source)) return;
        status("Список писем ещё не распознан. «Оригинал сайта» доступен в меню ⋯.");
    }

    private void TrackMailboxFrame(CoreWebView2 core, CoreWebView2Frame frame)
    {
        ulong? navigation = null;
        string? destination = null;
        frame.NavigationStarting += (_, e) =>
        {
            navigation = e.NavigationId;
            destination = NavigationPolicy.IsMail(e.Uri) ? e.Uri : null;
            if (ReferenceEquals(mailFrame, frame)) mailFrameSource = null;
        };
        frame.Destroyed += (_, _) =>
        {
            if (!ReferenceEquals(mailFrame, frame)) return;
            mailFrame = null;
            mailFrameSource = null;
        };
        frame.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess || e.NavigationId != navigation || destination is null || disposed
                || !NavigationPolicy.IsMailWorkspace(core.Source)) return;
            var source = destination;
            try
            {
                if (ReferenceEquals(mailFrame, frame) && embeddedMailboxReady)
                {
                    mailFrameSource = source;
                    return;
                }
                // The provider mounts the message list asynchronously after the iframe loads.
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    if (disposed || navigation != e.NavigationId || frame.IsDestroyed() != 0
                        || !NavigationPolicy.IsMailWorkspace(core.Source)) return;
                    if (await frame.ExecuteScriptAsync(MailboxDocumentReady) == "true")
                    {
                        if (disposed || navigation != e.NavigationId || frame.IsDestroyed() != 0
                            || !NavigationPolicy.IsMailWorkspace(core.Source)) return;
                        mailFrame = frame;
                        mailFrameSource = source;
                        FinishSignIn(core);
                        if (!embeddedMailboxReady)
                        {
                            embeddedMailboxReady = true;
                            store.Log("mail_signin_embedded_mail_ready");
                            status("Почта Mail.ru подключена");
                            EmbeddedMailboxReady?.Invoke();
                        }
                        return;
                    }
                    await Task.Delay(200);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The provider replaced or closed the frame during the probe.
            }
        };
    }

    private void LogSignInFailure(int httpStatusCode, string stage)
    {
        var reason = httpStatusCode switch
        {
            401 => "_unauthorized",
            403 => "_forbidden",
            429 => "_rate_limited",
            >= 400 and < 500 => "_client_error",
            >= 500 and < 600 => "_server_error",
            _ => ""
        };
        store.Log(stage + reason);
    }

    private void FinishSignIn(CoreWebView2 core)
    {
        // Mail, Cloud and sign-in popups share this WebView2 profile.
        if (signInViews.Remove(core) && signInViews.Count == 0)
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
    }

    private async Task RecoverStalledMailRedirect(WebView2 view, string completedSource)
    {
        try
        {
            // An expired Mail.ru session can leave e.mail.ru on a visually empty
            // document titled "Redirect" instead of continuing to the login page.
            // Give the site's own redirect a chance first, then use the same official
            // in-profile sign-in flow that the page was trying to reach.
            await Task.Delay(1200);
            if (disposed || view.CoreWebView2 is not { } core || core.Source != completedSource) return;
            var json = await core.ExecuteScriptAsync("(() => ({Title:(document.title||'').trim(),HasMail:!!document.querySelector('.letter-list__react,.llct,.letter-list,.thread,.compose-app'),HasLogin:!!document.querySelector('input[type=password],form')}))()");
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<RedirectDocument>(json);
            if (!IsStalledMailRedirect(snapshot?.Title, snapshot?.HasMail == true, snapshot?.HasLogin == true) || core.Source != completedSource) return;
            store.Log("mail_stalled_redirect");
            status("Сессия Mail.ru истекла · открываю официальный вход…");
            core.Navigate("https://account.mail.ru/login/?page=https%3A%2F%2Fe.mail.ru%2Finbox%2F");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A concurrent navigation or closed view makes recovery unnecessary.
        }
    }

    private sealed record RedirectDocument(string Title, bool HasMail, bool HasLogin);
    internal static bool IsStalledMailRedirect(string? title, bool hasMail, bool hasLogin) =>
        !hasMail && !hasLogin && title is not null
        && (title.Equals("Redirect", StringComparison.OrdinalIgnoreCase)
            || title.Equals("Перенаправление", StringComparison.OrdinalIgnoreCase));

    private async Task SetScript(WebView2 view)
    {
        if (scripts.Remove(view, out var old)) view.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(old);
        scripts[view] = await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript.Create(store.Rules, store.Settings.CosmeticFilters && !paused(), store.Settings.Aggressive));
    }
    private void SetTheme(WebView2 view) => view.CoreWebView2.Profile.PreferredColorScheme = store.Settings.Theme switch
    {
        "Dark" => CoreWebView2PreferredColorScheme.Dark,
        "Light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto
    };
    public async Task ApplySettings()
    {
        foreach (var view in new[] { View }.Concat(CloudView is { } cloud ? new[] { cloud } : []).Concat(popups.Select(p => p.View)).ToArray())
        {
            if (view.CoreWebView2 is null) continue;
            await SetScript(view); SetTheme(view);
            // Reload is explicit in the settings UI: needed to undo both CSS and previously blocked requests.
            view.Reload();
        }
    }
    public async Task ClearData()
    {
        ReaderState = null; ReaderReplyOpen = false; readerData?.Prepared.Clear(); Cache.Clear();
        foreach (var p in popups.ToArray()) p.Window.Close();
        SetCloudOpen(false);
        SetCloudOriginal(false);
        try
        {
            clearing = true;
            // Both live documents must leave before wiping their shared profile.
            if (CloudView?.CoreWebView2 is not null) await LeaveDocument(CloudView);
            await LeaveDocument(View);
            await View.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        finally
        {
            if (CloudView?.CoreWebView2 is { } cloudCore) FinishSignIn(cloudCore);
            if (View.CoreWebView2 is { } mailCore) FinishSignIn(mailCore);
            clearing = false;
        }
    }
    private static async Task LeaveDocument(WebView2 view)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong navigation = 0;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri == "about:blank") navigation = e.NavigationId; }
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess && e.NavigationId == navigation) done.TrySetResult(); }
        var core = view.CoreWebView2;
        core.NavigationStarting += Starting; core.NavigationCompleted += Completed;
        try { core.Stop(); core.Navigate("about:blank"); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; }
    }
    private void QueueExternal(string url) => owner.Dispatcher.BeginInvoke(() => { if (!disposed) OpenExternal(url); });
    private void OpenExternal(string url)
    {
        if (!NavigationPolicy.IsExternal(url)) return;
        if (externalHandler is not null) { externalHandler(url); return; }
        var host = new Uri(url).IdnHost;
        if (MessageBox.Show(owner, $"Открыть внешний сайт {host} в системном браузере?", "Внешняя ссылка", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { store.Log("external_open_failed"); status("Не удалось открыть системный браузер."); }
    }
    public void Dispose()
    {
        disposed = true; readerData?.Dispose();
        foreach (var p in popups.ToArray()) p.Window.Close();
        foreach (var core in signInViews.ToArray()) FinishSignIn(core);
        CloudView?.Dispose(); View.Dispose(); scripts.Clear();
    }
}

