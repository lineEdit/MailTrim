using System.Globalization;
using System.Windows.Data;
using MailTrim.Core.Gmail;

namespace MailTrim.App;

public sealed class GmailDisplayConverter : IValueConverter
{
    public string Kind { get; set; } = "";
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Kind switch
    {
        "Sender" => GmailReadingView.SenderName(value as string ?? ""),
        "Initial" => Initial(value as string ?? ""),
        "Date" when value is DateTimeOffset date => GmailReadingView.CompactDate(date),
        "Preview" => (value as string ?? "").Replace('\r',' ').Replace('\n',' ').Trim(),
        "FolderGroup" => value is string id && id.StartsWith("Label_",StringComparison.Ordinal) ? "Метки" : "Папки",
        "FolderName" when value is GmailLabel folder => FolderName(folder),
        "FolderIcon" when value is GmailLabel folder => FolderIcon(folder.Id),
        _ => value
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    private static string Initial(string from)
    {
        var name = GmailReadingView.SenderName(from).Trim(' ', '"', '\'');
        return name.Length == 0 ? "@" : StringInfo.GetNextTextElement(name).ToUpperInvariant();
    }
    public static string FolderName(GmailLabel folder)
    {
        var name = folder.Name;
        if (folder.Id.StartsWith("Label_",StringComparison.Ordinal))
            foreach (var prefix in new[] { "[Gmail]/", "[Imap]/" })
                if (name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        return name.Replace("/", " › ", StringComparison.Ordinal);
    }
    public static string FolderIcon(string id) => id switch
    {
        "INBOX" => "\uE715", "" => "\uE8F1", "STARRED" => "\uE734", "SENT" => "\uE724",
        "DRAFT" => "\uE70F", "SPAM" => "\uE7BA", "TRASH" => "\uE74D", _ => "\uE8B7"
    };
}
