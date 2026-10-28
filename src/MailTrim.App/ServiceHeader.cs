using System.Windows;
using System.Windows.Controls;

namespace MailTrim.App;

// Familiar service navigation is owned by the app, never copied from provider HTML.
public sealed class ServiceHeader : Grid
{
    public RadioButton MailTab { get; } = new() { Name = "ServiceMailTab", Content = "Почта" };
    public RadioButton CloudTab { get; } = new() { Name = "ServiceCloudTab", Content = "Облако" };
    public event Action? MailRequested;
    public event Action? CloudRequested;

    public ServiceHeader() : this(false) { }
    public ServiceHeader(bool cloud, FrameworkElement? trailing = null)
    {
        Height = 60;
        SetResourceReference(BackgroundProperty, "Surface");
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 126, Margin = new Thickness(14, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        var at = new TextBlock { Text = "@", FontSize = 32, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 4, 0) };
        at.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); brand.Children.Add(at);
        var name = new TextBlock { Text = cloud ? "облако" : "mail", FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); brand.Children.Add(name); Children.Add(brand);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(tabs, 1); Children.Add(tabs);
        foreach (var tab in new[] { MailTab, CloudTab })
        {
            tab.SetResourceReference(StyleProperty, "ServiceTab"); tabs.Children.Add(tab);
            System.Windows.Automation.AutomationProperties.SetName(tab, tab.Content.ToString());
        }
        MailTab.ToolTip = "Вернуться к открытой почте";
        CloudTab.ToolTip = "Файлы и папки Облака текущего ящика";
        var restoringSelection = false;
        SelectCurrent();
        // Checked also covers keyboard and accessibility selection, which may not raise Click.
        MailTab.Checked += (_, _) => Request(false);
        CloudTab.Checked += (_, _) => Request(true);
        if (trailing is not null)
        {
            SetColumn(trailing, 3); trailing.Margin = new Thickness(8, 0, 14, 0); Children.Add(trailing);
        }
        void SelectCurrent()
        {
            restoringSelection = true;
            try { MailTab.IsChecked = !cloud; CloudTab.IsChecked = cloud; }
            finally { restoringSelection = false; }
        }
        void Request(bool requestedCloud)
        {
            if (restoringSelection || requestedCloud == cloud) return;
            // Keep the current workspace selected until its owner accepts the transition.
            SelectCurrent();
            if (requestedCloud) CloudRequested?.Invoke(); else MailRequested?.Invoke();
        }
    }
}
