namespace MailTrim.Core;

public sealed class NewMailTracker
{
    private readonly Dictionary<Guid, string> heads = [];
    public void Clear() => heads.Clear();
    public int Observe(Guid profile, IEnumerable<string> snapshot)
    {
        var urls = snapshot.Where(NavigationPolicy.IsMail).Distinct().ToArray();
        if (urls.Length == 0) return 0;
        var added = heads.TryGetValue(profile, out var old) ? Array.IndexOf(urls, old) : 0;
        heads[profile] = urls[0];
        return Math.Max(0, added);
    }
}
