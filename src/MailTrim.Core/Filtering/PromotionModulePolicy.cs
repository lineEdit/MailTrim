namespace MailTrim.Core;

public static class PromotionModulePolicy
{
    // Dedicated promotion bundle documented by easylist/ruadlist's
    // js-fixes-experimental.txt; general Mail, modal and authentication code stays allowed.
    private const string ModulePath = "/2promo.octavius.bundle.js";

    public static bool ShouldBlockScript(string requestUrl, string documentUrl, FilterRules rules, bool enabled)
    {
        if (!enabled || !NavigationPolicy.IsMail(documentUrl)
            || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)
            || request.Scheme != "https" || !request.IsDefaultPort || request.UserInfo.Length != 0)
            return false;
        if (rules.AllowedDomains.Any(domain => NavigationPolicy.HostMatches(request.IdnHost, domain))) return false;
        return request.AbsolutePath.EndsWith(ModulePath, StringComparison.Ordinal);
    }
}
