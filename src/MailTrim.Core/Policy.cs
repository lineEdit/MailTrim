using System.Text.Json;
using System.Text.RegularExpressions;

namespace MailTrim.Core;

public static class NavigationPolicy
{
    // Exact hosts only. Never permit *.mail.ru or *.vk.com for top-level navigation.
    private static readonly HashSet<string> Internal = new(StringComparer.OrdinalIgnoreCase)
    { "e.mail.ru", "account.mail.ru", "auth.mail.ru", "oauth.mail.ru", "id.mail.ru", "id.vk.com", "id.vk.ru", "login.vk.com", "login.vk.ru", "oauth.vk.com", "oauth.vk.ru" };

    public static bool IsInternal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && Internal.Contains(u.IdnHost);
    public static bool IsExternal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme is "https" or "http" && u.UserInfo.Length == 0;
    public static bool IsMail(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.IdnHost == "e.mail.ru";
    public static bool HostMatches(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}

public sealed class FilterRules
{
    public int SchemaVersion { get; set; } = 1;
    public string Revision { get; set; } = "2026-09-30.1";
    public List<string> BlockedDomains { get; set; } = [];
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> HideSelectors { get; set; } = [];
    public List<string> AggressiveSelectors { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static FilterRules Parse(string json)
    {
        if (json.Length > 256_000) throw new FormatException("Файл правил слишком большой (максимум 256 КБ).");
        var r = JsonSerializer.Deserialize<FilterRules>(json, Json) ?? throw new FormatException("Пустые правила.");
        if (r.SchemaVersion != 1 || string.IsNullOrWhiteSpace(r.Revision) || r.Revision.Length > 80)
            throw new FormatException("Неподдерживаемая версия или некорректная ревизия правил.");
        if (r.BlockedDomains is null || r.AllowedDomains is null || r.HideSelectors is null || r.AggressiveSelectors is null)
            throw new FormatException("Списки правил не могут быть null.");
        foreach (var list in new[] { r.BlockedDomains, r.AllowedDomains, r.HideSelectors, r.AggressiveSelectors })
            if (list.Count > 500) throw new FormatException("Не более 500 элементов в списке.");
        foreach (var d in r.BlockedDomains.Concat(r.AllowedDomains))
            if (d is null || d.Length > 253 || !Regex.IsMatch(d, @"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant))
                throw new FormatException("Домены должны быть без протокола, пути и wildcard, в нижнем регистре.");
        foreach (var s in r.HideSelectors.Concat(r.AggressiveSelectors))
            if (string.IsNullOrWhiteSpace(s) || s.Length > 500 || s.IndexOfAny(['{', '}', ';', '@', '\\', '\n', '\r']) >= 0 || s.Contains("/*"))
                throw new FormatException("Разрешены только CSS-селекторы, не CSS-декларации и не JavaScript.");
        return r;
    }

    public bool ShouldBlock(string url, bool enabled)
    {
        if (!enabled || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        if (NavigationPolicy.IsInternal(url)) return false; // Login and mail endpoints cannot be accidentally denied.
        var host = u.IdnHost;
        return !AllowedDomains.Any(d => NavigationPolicy.HostMatches(host, d))
            && BlockedDomains.Any(d => NavigationPolicy.HostMatches(host, d));
    }
}

public static class CosmeticScript
{
    // Declarative rules only. No remote executable JS; no access to messages or credentials.
    public static string Create(FilterRules rules, bool enabled, bool aggressive)
    {
        var selectors = enabled ? rules.HideSelectors.Concat(aggressive ? rules.AggressiveSelectors : []).ToArray() : [];
        return """
        (() => {
          if (window.top !== window || location.origin !== 'https://e.mail.ru') return;
          const selectors = __SELECTORS__;
          const id = 'mailtrim-cosmetic-style';
          const apply = () => {
            if (!document.documentElement) return;
            let style = document.getElementById(id);
            if (!style) { style = document.createElement('style'); style.id = id; document.documentElement.appendChild(style); }
            style.textContent = selectors.filter(s => { try { document.querySelector(s); return true; } catch { return false; } })
              .map(s => s + ' { display: none !important; }').join('\n');
          };
          apply();
          document.addEventListener('DOMContentLoaded', apply, {once:true});
          // CSS automatically covers newly added SPA nodes. Reattach only when the site removes the style.
          if (document.documentElement) new MutationObserver(() => {
            if (!document.getElementById(id)) apply();
          }).observe(document.documentElement, {childList:true});
        })();
        """.Replace("__SELECTORS__", JsonSerializer.Serialize(selectors));
    }
}
