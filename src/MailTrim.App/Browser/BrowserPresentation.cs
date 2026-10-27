using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailTrim.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.App;

/// <summary>Publishes a cleaned browser frame; all preview pixels stay in this profile's memory.</summary>
public sealed class BrowserPresentation : Grid, IDisposable
{
    private readonly WebView2 view;
    private readonly Func<bool> enabled;
    private readonly Grid cover = new();
    private readonly Image preview = new() { Stretch = Stretch.Fill };
    private readonly TextBlock label = new() { Text = "Подготавливаю почту…", FontSize = 14 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private CoreWebView2? core;
    private bool background, polling, disposed, waitingDocument, capturing;
    private bool restoreFocus;
    private string? blockedDocument, acceptedDocument, captureStamp;
    private long generation;
    private ulong navigation;
    private DateTimeOffset pendingSince;
    public bool IsPending { get; private set; }
    public bool HasPreview => preview.Source is not null;
    public Visibility RequestedVisibility { get; private set; } = Visibility.Visible;
    public Task PendingPoll { get; private set; } = Task.CompletedTask;
    public Task BackgroundPreview { get; private set; } = Task.CompletedTask;
    private const string ReadState = """
        (()=>{try{const s=window.__mailtrimPresentation?.();
        return s?.version===1 && typeof s.doc==='string' && s.doc.length>0 && s.doc.length<=128
          && Number.isSafeInteger(s.revision) && s.revision>=0
          ? {version:1,doc:s.doc,revision:s.revision,ready:s.ready===true,sensitive:s.sensitive!==false}:null;
        }catch{return null}})()
        """;

    public BrowserPresentation(WebView2 view, Func<bool> enabled)
    {
        this.view = view; this.enabled = enabled; Focusable = true;
        SetResourceReference(BackgroundProperty, "Panel");
        cover.SetResourceReference(BackgroundProperty, "Panel");
        Children.Add(view); cover.Children.Add(preview);
        var card = new Border { Child = label, Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel"); label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        cover.Children.Add(card); cover.Visibility = Visibility.Collapsed; Children.Add(cover);
        timer.Tick += (_, _) => { if (!polling) PendingPoll = Poll(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && RequestedVisibility == Visibility.Visible && core is not null && Protect(core.Source)) Begin();
            RefreshTimer();
        };
        Unloaded += (_, _) => timer.Stop();
        Loaded += (_, _) => RefreshTimer();
    }
    public void Initialize(CoreWebView2 browser, bool isBackground)
    {
        core = browser; background = isBackground;
        if (background) SetRequestedVisibility(Visibility.Hidden);
        core.NavigationStarting += (_, e) =>
        {
            if (e.Cancel || !NavigationPolicy.IsInternal(e.Uri)) return;
            navigation = e.NavigationId;
            if (Protect(e.Uri)) { waitingDocument = true; blockedDocument = acceptedDocument; Begin(); }
            else Bypass(clearPreview: true);
        };
        core.SourceChanged += (_, e) =>
        {
            if (Protect(core.Source)) { if (!e.IsNewDocument) waitingDocument = false; Begin(); }
            else Bypass(clearPreview: true);
        };
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && e.NavigationId == navigation) Bypass(clearPreview: true); };
        core.ProcessFailed += (_, _) => Bypass(clearPreview: true);
        RefreshTimer();
    }
    private bool Protect(string uri) => !disposed && !background && enabled() && NavigationPolicy.IsMail(uri);
    public void SetRequestedVisibility(Visibility value)
    {
        var showing = RequestedVisibility != Visibility.Visible && value == Visibility.Visible;
        RequestedVisibility = value; Visibility = value;
        if (showing && core is not null && Protect(core.Source)) Begin();
        UpdateVisibility(); RefreshTimer();
    }
    private void UpdateVisibility()
    {
        if (IsPending && RequestedVisibility == Visibility.Visible && view.IsKeyboardFocusWithin && Window.GetWindow(this)?.IsActive == true)
        { restoreFocus = true; Keyboard.Focus(this); }
        view.Visibility = RequestedVisibility == Visibility.Visible && IsPending ? Visibility.Hidden : RequestedVisibility;
        cover.Visibility = IsPending ? Visibility.Visible : Visibility.Collapsed;
        if (!IsPending && RequestedVisibility == Visibility.Visible && restoreFocus)
        {
            if (Keyboard.FocusedElement == this && Window.GetWindow(this)?.IsActive == true) view.Focus();
            restoreFocus = false;
        }
    }
    private void RefreshTimer()
    {
        if (!disposed && !background && core is not null && IsVisible && RequestedVisibility == Visibility.Visible) timer.Start();
        else timer.Stop();
    }
    private void Begin()
    {
        if (disposed) return;
        generation++;
        if (!IsPending) pendingSince = DateTimeOffset.UtcNow;
        IsPending = true; label.Text = "Подготавливаю почту…"; UpdateVisibility();
    }
    private void Bypass(bool clearPreview = false)
    {
        if (disposed) return;
        generation++; IsPending = false; waitingDocument = false;
        if (clearPreview) { preview.Source = null; captureStamp = null; acceptedDocument = null; }
        UpdateVisibility();
    }
    private async Task Poll()
    {
        if (core is null || disposed || polling) return;
        polling = true;
        try
        {
            if (!Protect(core.Source)) { if (!waitingDocument) Bypass(clearPreview: true); return; }
            var epoch = generation;
            var json = await core.ExecuteScriptAsync(ReadState);
            if (disposed || epoch != generation) return;
            var state = JsonSerializer.Deserialize<FrameState>(json, FilterRules.Json);
            if (state is null || state.Version != 1 || state.Doc is not { Length: > 0 and <= 128 } || state.Revision < 0)
            {
                if (!IsPending) Begin();
                if (DateTimeOffset.UtcNow - pendingSince > TimeSpan.FromSeconds(8)) label.Text = "Почта ещё подготавливается · ↻ — повторить загрузку";
                return;
            }
            if (waitingDocument && state.Doc == blockedDocument) return;
            waitingDocument = false;
            if (state.Sensitive) { preview.Source = null; captureStamp = null; }
            if (!state.Ready) { if (!IsPending) Begin(); return; }
            acceptedDocument = state.Doc;
            IsPending = false; UpdateVisibility();
            var stamp = state.Doc + ":" + state.Revision;
            if (!state.Sensitive && !capturing && captureStamp != stamp && RequestedVisibility == Visibility.Visible && IsVisible)
                // Capture can wait for a browser frame. It must never block the polling
                // loop that releases a hidden browser after the next navigation.
                BackgroundPreview = Capture(stamp, generation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or System.Runtime.InteropServices.COMException)
        {
            // No page text, message URLs or images are written to the log. A failed
            // preparation keeps the loading surface; the toolbar's retry remains usable.
            if (!disposed && IsPending) label.Text = "Не удалось подготовить почту · ↻ — повторить";
        }
        finally { polling = false; }
    }
    private async Task Capture(string stamp, long epoch)
    {
        capturing = true;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(view);
            var pixels = view.ActualWidth * dpi.DpiScaleX * view.ActualHeight * dpi.DpiScaleY;
            if (pixels <= 0 || pixels > 20_000_000) return;
            using var memory = new MemoryStream();
            await core!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, memory);
            if (disposed || epoch != generation || memory.Length > 8_000_000 || !IsVisible || RequestedVisibility != Visibility.Visible) return;
            var current = JsonSerializer.Deserialize<FrameState>(await core.ExecuteScriptAsync(ReadState), FilterRules.Json);
            if (current is not { Ready: true, Sensitive: false } || current.Doc + ":" + current.Revision != stamp || epoch != generation) return;
            memory.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory; bitmap.EndInit();
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 20_000_000) return;
            bitmap.Freeze(); preview.Source = bitmap; captureStamp = stamp;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or JsonException or NotSupportedException or ArgumentException or FormatException) { }
        finally { capturing = false; }
    }
    public void ClearPreview() { generation++; preview.Source = null; captureStamp = null; }
    public void Dispose() { disposed = true; generation++; timer.Stop(); preview.Source = null; }
    private sealed record FrameState(int Version, string? Doc, long Revision, bool Ready, bool Sensitive);
}
