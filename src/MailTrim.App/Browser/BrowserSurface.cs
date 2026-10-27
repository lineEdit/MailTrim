using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace MailTrim.App;

// Movable owner for original and inline reply views; no polling or frame cache.
public sealed class BrowserSurface : Grid
{
    private readonly WebView2 view;
    public Visibility RequestedVisibility { get; private set; } = Visibility.Visible;
    public BrowserSurface(WebView2 view) { this.view = view; Children.Add(view); }
    public void Initialize(bool background) { if (background) SetRequestedVisibility(Visibility.Hidden); }
    public void SetRequestedVisibility(Visibility visibility)
    {
        RequestedVisibility = visibility; Visibility = visibility; view.Visibility = visibility;
    }
}
