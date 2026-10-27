namespace MailTrim.Core;

// The native Cloud renderer receives only these bounded plain-data records.
// It never receives site HTML, executable markup, cookies or provider responses.
public sealed record CloudItem(string Name, string Url, bool IsFolder, string Size = "", string Modified = "");
public sealed record CloudFolder(string Name, string Url);
public sealed record CloudListing(bool Ready, bool AuthRequired, string FolderUrl, string FolderName,
    List<CloudItem> Items, List<CloudFolder> Breadcrumbs, bool Empty = false, bool Partial = false, string Message = "", string LoginUrl = "");

public static class CloudData
{
    public const string Home = "https://cloud.mail.ru/home/";
    private static readonly string[] Routes = ["/home", "/trash", "/trashbin", "/recent", "/shared", "/starred", "/favorites", "/public"];
    public static string CanonicalUrl(string? value)
    {
        if (value is null || value.Length > 6000 || !NavigationPolicy.IsCloud(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        var path = uri.AbsolutePath;
        if (!Routes.Any(route => path.Equals(route, StringComparison.Ordinal) || path.StartsWith(route + "/", StringComparison.Ordinal))) return "";
        // User interface navigation never carries search tokens, credentials or API query parameters.
        var canonical = uri.GetLeftPart(UriPartial.Path);
        return canonical.Length <= 6000 ? canonical : "";
    }
    public static bool IsFolderUrl(string? value) => CanonicalUrl(value).Length > 0;
    public static string ParentUrl(string? value)
    {
        var canonical = CanonicalUrl(value);
        if (canonical.Length == 0) return Home;
        var uri = new Uri(canonical); var path = uri.AbsolutePath;
        if (path is "/home" or "/home/") return Home;
        if (path.StartsWith("/home/", StringComparison.Ordinal))
        {
            var suffix = path[6..];
            if (suffix.StartsWith("%2f", StringComparison.OrdinalIgnoreCase))
            {
                var objectPath = Uri.UnescapeDataString(suffix).TrimEnd('/');
                if (!objectPath.StartsWith('/') || objectPath.StartsWith("//", StringComparison.Ordinal)
                    || objectPath.Split('/').Any(part => part is "." or "..")) return Home;
                var index = objectPath.LastIndexOf('/');
                return index <= 0 ? Home : "https://cloud.mail.ru/home/" + Uri.EscapeDataString(objectPath[..index]);
            }
        }
        var trimmed = path.TrimEnd('/'); var last = trimmed.LastIndexOf('/');
        return last <= 0 ? Home : CanonicalUrl("https://cloud.mail.ru" + trimmed[..(last + 1)]) is { Length: > 0 } parent ? parent : Home;
    }
    public static IReadOnlyList<CloudFolder> FolderPath(string? value)
    {
        var result = new List<CloudFolder> { new("Все файлы", Home) };
        var url = CanonicalUrl(value);
        if (url.Length == 0) return result;
        var path = new Uri(url).AbsolutePath;
        if (!path.StartsWith("/home/", StringComparison.Ordinal)) return result;
        var suffix = path[6..];
        var raw = Uri.UnescapeDataString(suffix).Trim('/');
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) return result;
        for (var i = 0; i < parts.Length && i < 40; i++)
        {
            var target = CanonicalUrl("https://cloud.mail.ru/home/" + Uri.EscapeDataString("/" + string.Join('/', parts.Take(i + 1))));
            if (target.Length > 0) result.Add(new(parts[i].Length > 300 ? parts[i][..300] : parts[i], target));
        }
        return result;
    }
    public static string LoginUrl(string? value)
    {
        if (value is null || value.Length > 8000 || !NavigationPolicy.IsInternal(value)
            || NavigationPolicy.IsCloud(value) || NavigationPolicy.IsMail(value)) return "";
        return value;
    }
    private static string Text(string? value, int limit)
    {
        var clean = (value ?? "").Replace('\0', ' ').Trim();
        return clean.Length > limit ? clean[..limit] : clean;
    }
    public static CloudListing Clean(CloudListing? value)
    {
        if (value is null) return Unknown();
        var folder = CanonicalUrl(value.FolderUrl);
        var items = new List<CloudItem>(); var urls = new HashSet<string>(StringComparer.Ordinal);
        var rejected = false;
        foreach (var item in (value.Items ?? []).Take(2001))
        {
            if (items.Count >= 2000) { rejected = true; break; }
            if (item is null) { rejected = true; continue; }
            var url = CanonicalUrl(item.Url); var name = Text(item.Name, 1000);
            if (url.Length == 0 || name.Length == 0 || url == folder || !urls.Add(url)) { rejected = true; continue; }
            items.Add(new(name, url, item.IsFolder, Text(item.Size, 100), Text(item.Modified, 150)));
        }
        var breadcrumbs = new List<CloudFolder>(); var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in (value.Breadcrumbs ?? []).Take(40))
        {
            if (part is null) continue;
            var url = CanonicalUrl(part.Url); var name = Text(part.Name, 300);
            if (url.Length > 0 && name.Length > 0 && parents.Add(url)) breadcrumbs.Add(new(name, url));
        }
        var auth = value.AuthRequired;
        var ready = !auth && value.Ready && folder.Length > 0 && (items.Count > 0 || value.Empty && !rejected);
        return new(ready, auth, folder, Text(value.FolderName, 300), auth ? [] : items, auth ? [] : breadcrumbs,
            ready && items.Count == 0 && value.Empty, value.Partial || rejected,
            Text(value.Message, 500), LoginUrl(value.LoginUrl));
    }
    public static CloudListing Unknown(string folder = "", string message = "Не удалось распознать список файлов. Откройте оригинал явно, если нужна операция сайта.")
        => new(false, false, CanonicalUrl(folder), "", [], [], Message: message);
}
