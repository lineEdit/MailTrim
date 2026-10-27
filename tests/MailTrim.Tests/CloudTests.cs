using MailTrim.Core;

internal static class CloudTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var url in new[] { "https://cloud.mail.ru.evil.test/home/", "https://user@cloud.mail.ru/home/", "http://cloud.mail.ru/home/", "https://cloud.mail.ru:444/home/", "https://e.mail.ru/inbox/", "https://cloud.mail.ru/api/files" })
            check(CloudData.CanonicalUrl(url) == "", "native Cloud rejects non-folder origin or route " + url);
        check(CloudData.CanonicalUrl("https://cloud.mail.ru/home/%2FDocs?token=test#fragment") == "https://cloud.mail.ru/home/%2FDocs", "native folder navigation strips query and fragment without changing object encoding");
        check(CloudData.ParentUrl("https://cloud.mail.ru/home/%2FDocs%2FSub") == "https://cloud.mail.ru/home/%2FDocs"
            && CloudData.ParentUrl("https://cloud.mail.ru/home/%2FDocs") == CloudData.Home, "native Up respects Cloud's encoded whole object path");
        check(CloudData.FolderPath("https://cloud.mail.ru/home/%2FDocs%2FSub").Select(part => part.Name).SequenceEqual(new[] { "Все файлы", "Docs", "Sub" }), "native breadcrumbs retain each encoded provider folder level");
        var rejected = CloudData.Clean(new(true, false, CloudData.Home, "Files", [new("Bad", "https://example.test/file", false)], [], Empty: true));
        check(!rejected.Ready && !rejected.Empty && rejected.Partial, "rejected file data cannot become a confirmed empty folder");
        var item = new CloudItem(new string('a', 1200), "https://cloud.mail.ru/home/%2Freport.pdf", false, new string('s', 200), new string('d', 300));
        var clean = CloudData.Clean(new(true, false, CloudData.Home, "Files", [item, item], []));
        check(clean.Ready && clean.Items.Count == 1 && clean.Items[0].Name.Length == 1000 && clean.Items[0].Size.Length == 100 && clean.Items[0].Modified.Length == 150, "native file records are bounded and deduplicated before rendering");
        var auth = CloudData.Clean(new(true, true, CloudData.Home, "Files", [item], [new("Root", CloudData.Home)], LoginUrl: "https://evil.test/login"));
        check(!auth.Ready && auth.Items.Count == 0 && auth.Breadcrumbs.Count == 0 && auth.LoginUrl == "", "authentication state cannot expose stale file data or an external login target");
        check(CloudData.Clean(new(true, false, CloudData.Home, "Files", [item], [], Partial: true)).Partial, "a partial source listing never becomes a complete folder claim");
    }
}
