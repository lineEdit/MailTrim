namespace MailTrim.Core;

/// <summary>Bounded, profile-owned plain data ready for rendering. No browser nodes or executable HTML.</summary>
public sealed class PreparedMessageStore
{
    private readonly Dictionary<string, (PreparedMessage Data, long Use)> items = [];
    private long clock;
    private readonly int maxItems;
    private readonly int maxCharacters;
    public PreparedMessageStore(int maxItems = 80, int maxCharacters = 4_000_000)
    {
        if (maxItems < 1 || maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        this.maxItems = maxItems; this.maxCharacters = maxCharacters;
    }
    public int Count => items.Count;
    public PreparedMessage? Get(ReaderLetter letter)
    {
        if (letter.IsGroup || !items.TryGetValue(letter.Url, out var item)) return null;
        var data = item.Data;
        // Changed website metadata makes a prepared body a preview until revalidated.
        if (!SameContent(data.Letter, letter)) data = data with { VerifiedAt = null };
        data = data with { Letter = letter };
        items[letter.Url] = (data, ++clock);
        return data;
    }
    public PreparedMessage? Put(ReaderDocument document, DateTimeOffset now)
    {
        if (ReaderData.Clean(document.Letter) is not { IsGroup: false } letter) return null;
        var blocks = ReaderData.Clean(document.Blocks).AsReadOnly();
        if (blocks.Count == 0 || blocks.Sum(b => b.Text.Length + b.Image.Length) > maxCharacters) return null;
        if (document.Cached && Get(letter) is { VerifiedAt: not null } current) return current;
        var data = new PreparedMessage(letter, blocks, document.Cached ? null : now, document.Saved);
        items[letter.Url] = (data, ++clock);
        while (items.Count > maxItems || items.Values.Sum(x => x.Data.Blocks.Sum(b => b.Text.Length + b.Image.Length)) > maxCharacters)
            items.Remove(items.MinBy(x => x.Value.Use).Key);
        return data;
    }
    public void Invalidate(IEnumerable<ReaderLetter> letters)
    {
        foreach (var letter in letters)
            if (items.TryGetValue(letter.Url, out var item)) items[letter.Url] = (item.Data with { VerifiedAt = null }, item.Use);
    }
    public void Clear() => items.Clear();
    private static bool SameContent(ReaderLetter a, ReaderLetter b) => a.Sender == b.Sender && a.Subject == b.Subject
        && a.Preview == b.Preview && a.Date == b.Date && a.ReceivedAt == b.ReceivedAt;
}

public sealed record PreparedMessage(ReaderLetter Letter, IReadOnlyList<ReaderBlock> Blocks, DateTimeOffset? VerifiedAt, bool Saved)
{
    public bool IsFresh(DateTimeOffset now) => VerifiedAt is { } time && now >= time && now - time < TimeSpan.FromMinutes(5);
}
