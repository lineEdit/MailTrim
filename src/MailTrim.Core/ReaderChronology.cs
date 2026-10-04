using System.Globalization;
using System.Text.RegularExpressions;

namespace MailTrim.Core;

public static class ReaderChronology
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static DateTimeOffset? DateKey(ReaderLetter letter)
    {
        if (letter.ReceivedAt is { } exact) return exact;
        var anchor = letter.CapturedAt?.ToLocalTime();
        foreach (var label in new[] { letter.DateHint, letter.Date })
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            var text = Regex.Replace(label.Trim().ToLowerInvariant().Replace("г.", "").Replace("сегодня,", "сегодня").Replace("вчера,", "вчера"), @"\s+", " ").Trim();
            var formats = new[] { "dd.MM.yyyy", "dd.MM.yy", "d.MM.yy", "d.MM.yyyy", "dd.MM.yyyy HH:mm", "dd.MM.yy HH:mm", "d MMMM yyyy", "d MMMM yyyy HH:mm", "d MMMM yyyy, HH:mm", "d MMM yyyy", "d MMM yyyy HH:mm" };
            if (DateTime.TryParseExact(text, formats, Russian, DateTimeStyles.AllowWhiteSpaces, out var dated))
                return new DateTimeOffset(DateTime.SpecifyKind(dated, DateTimeKind.Unspecified), anchor?.Offset ?? TimeZoneInfo.Local.GetUtcOffset(dated));
            if (anchor is not { } reference) continue;
            if (text is "сегодня" or "today") return new DateTimeOffset(reference.Date, reference.Offset);
            if (text is "вчера" or "yesterday") return new DateTimeOffset(reference.Date.AddDays(-1), reference.Offset);
            var time = Regex.Match(text, @"^(?:(сегодня|вчера) )?(\d{1,2}):(\d{2})$");
            if (time.Success && int.TryParse(time.Groups[2].Value, out var hour) && hour < 24 && int.TryParse(time.Groups[3].Value, out var minute) && minute < 60)
                return new DateTimeOffset(reference.Date.AddDays(time.Groups[1].Value == "вчера" ? -1 : 0).AddHours(hour).AddMinutes(minute), reference.Offset);
            // Month-only dates refer to the most recent matching day at capture time.
            var shortDate = Regex.Match(text, @"^(\d{1,2}) ([а-яё]+)\.?$");
            if (shortDate.Success)
            {
                var monthNames = new[] { "янв", "фев", "мар", "апр", "ма", "июн", "июл", "авг", "сен", "окт", "ноя", "дек" };
                var month = Array.FindIndex(monthNames, name => shortDate.Groups[2].Value.StartsWith(name, StringComparison.Ordinal)) + 1;
                if (month > 0 && int.TryParse(shortDate.Groups[1].Value, out var day) && day > 0 && day <= DateTime.DaysInMonth(reference.Year, month))
                {
                    var candidate = new DateTimeOffset(reference.Year, month, day, 0, 0, 0, reference.Offset);
                    if (candidate.Date > reference.Date) candidate = candidate.AddYears(-1);
                    return candidate;
                }
            }
        }
        return null;
    }
    public static int Compare(ReaderLetter left, ReaderLetter right)
    {
        var a = DateKey(left); var b = DateKey(right);
        var order = Nullable.Compare(b, a);
        return order == 0 ? StringComparer.Ordinal.Compare(left.Url, right.Url) : order;
    }
    public static List<ReaderLetter> NewestFirst(IEnumerable<ReaderLetter> letters) => letters
        .Select(l => new { Letter = l, Key = DateKey(l) }).OrderByDescending(l => l.Key)
        .ThenBy(l => l.Letter.Url, StringComparer.Ordinal).Select(l => l.Letter).ToList();
}
