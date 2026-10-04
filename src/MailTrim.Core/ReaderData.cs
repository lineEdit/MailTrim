namespace MailTrim.Core;

// The renderer receives bounded plain text and HTTPS image references, never page HTML.
public sealed record ReaderDocument(ReaderLetter Letter, IReadOnlyList<ReaderBlock> Blocks, bool Cached, bool Saved);

public static class ReaderData
{
    private static string Text(string? value, int limit) => (value ?? "").Length > limit ? value![..limit] : value ?? "";
    public static ReaderLetter? Clean(ReaderLetter? letter) => letter is null || !NavigationPolicy.IsMail(letter.Url) ? null
        : new(letter.Url, Text(letter.Sender, 500), Text(letter.Subject, 2000), Text(letter.Preview, 300), Text(letter.Date, 100));
    public static List<ReaderBlock> Clean(IEnumerable<ReaderBlock>? input)
    {
        var result = new List<ReaderBlock>(); int remaining = 500_000;
        foreach (var block in (input ?? []).Take(300))
        {
            if (block is null || remaining <= 0) continue;
            var text = Text(block.Text, Math.Min(20_000, remaining)); remaining -= text.Length;
            var image = block.Image ?? "";
            if (image.Length > 4000 || !Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) image = "";
            if (image.Length > remaining) image = "";
            remaining -= image.Length;
            if (text.Length > 0 || image.Length > 0) result.Add(new(text, image));
        }
        return result;
    }
}
