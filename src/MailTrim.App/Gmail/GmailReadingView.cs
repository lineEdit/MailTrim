using System.Globalization;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailTrim.Core.Gmail;

namespace MailTrim.App;

/// <summary>Native presentation only; original message data and image loading preferences stay unchanged.</summary>
public static class GmailReadingView
{
    public const double MaxContentWidth = 920;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Keep paragraphs and indented content, removing only empty padding from the display copy.</summary>
    public static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var input = new StringReader(text);
        var result = new StringBuilder(text.Length);
        var hasText = false;
        var blank = false;
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { blank = hasText; continue; }
            if (hasText) { result.Append('\n'); if (blank) result.Append('\n'); }
            result.Append(line); hasText = true; blank = false;
        }
        return result.ToString();
    }

    public static TextBox CreateBodyText(string text)
    {
        var body = new TextBox
        {
            Text = NormalizeText(text), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontSize = 15, Padding = new Thickness(0),
            Margin = new Thickness(0,0,0,18), MaxWidth = MaxContentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsReadOnlyCaretVisible = false
        };
        body.SetResourceReference(Control.ForegroundProperty,"Ink");
        AutomationProperties.SetName(body,"Текст письма");
        return body;
    }

    public static StackPanel CreateHeader(GmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var header = new StackPanel { MaxWidth = MaxContentWidth, Margin = new Thickness(0,0,0,20) };
        header.Children.Add(Text(message.Subject.Length == 0 ? "(Без темы)" : message.Subject,26,"Ink",FontWeights.SemiBold,new Thickness(0,0,0,18)));
        var metadata = new Grid();
        metadata.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
        metadata.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var sender = new StackPanel();
        var name = SenderName(message.From); var email = SenderAddress(message.From);
        sender.Children.Add(Text(name,15,"Ink",FontWeights.SemiBold,new Thickness(0,0,0,3)));
        if (email.Length > 0 && !string.Equals(name,email,StringComparison.OrdinalIgnoreCase))
            sender.Children.Add(Text(email,12,"Muted",FontWeights.Normal,new Thickness(0,0,0,4)));
        if (message.To.Length > 0)
            sender.Children.Add(Text("Кому: " + message.To,12,"Muted",FontWeights.Normal,new Thickness(0)));
        metadata.Children.Add(sender);
        var date = Text(FullDate(message.ReceivedAt),12,"Muted",FontWeights.Normal,new Thickness(20,2,0,0));
        date.MaxWidth = 180; date.TextAlignment = TextAlignment.Right; Grid.SetColumn(date,1); metadata.Children.Add(date);
        header.Children.Add(metadata);
        var line = new Border { Height = 1, Margin = new Thickness(0,18,0,0) };
        line.SetResourceReference(Border.BackgroundProperty,"Line"); header.Children.Add(line);
        return header;
    }

    /// <summary>Images may shrink into the reader, but a logo must never grow to fill the window.</summary>
    public static Image CreateImage(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var image = new Image
        {
            Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = Math.Min(MaxContentWidth,NaturalSize(bitmap.PixelWidth,bitmap.Width)),
            MaxHeight = Math.Min(1800,NaturalSize(bitmap.PixelHeight,bitmap.Height)),
            Margin = new Thickness(0,4,0,16), SnapsToDevicePixels = true, UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image,"Изображение из письма");
        return image;
    }

    // A low-DPI file can claim thousands of DIPs for a tiny bitmap. Pixel bounds
    // prevent that metadata from turning a small newsletter logo into a banner.
    private static double NaturalSize(int pixels, double dips) => Math.Max(1,
        double.IsFinite(dips) && dips > 0 ? Math.Min(pixels,dips) : pixels);

    public static string SenderName(string from)
    {
        var address = ParseAddress(from);
        return address is null ? from.Trim() : address.DisplayName.Length > 0 ? address.DisplayName : address.Address;
    }
    public static string SenderAddress(string from) => ParseAddress(from)?.Address ?? "";
    private static MailAddress? ParseAddress(string from)
    {
        try { return new MailAddress(from); }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }
    public static string CompactDate(DateTimeOffset value) => CompactDate(value,DateTimeOffset.Now);
    public static string CompactDate(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime; var today = now.LocalDateTime;
        return local.ToString(local.Date == today.Date ? "HH:mm" : local.Year == today.Year ? "d MMM" : "dd.MM.yy",Russian);
    }
    public static string FullDate(DateTimeOffset value) => value.LocalDateTime.ToString("d MMMM yyyy, HH:mm",Russian);

    private static TextBlock Text(string value,double size,string color,FontWeight weight,Thickness margin)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        text.SetResourceReference(TextBlock.ForegroundProperty,color); return text;
    }
}
