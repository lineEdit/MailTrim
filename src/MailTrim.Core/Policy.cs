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
    public List<string> BlockedMailPathPrefixes { get; set; } = [];
    public bool RemoveLabeledAds { get; set; } = true;

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static FilterRules Parse(string json)
    {
        if (json.Length > 256_000) throw new FormatException("Файл правил слишком большой (максимум 256 КБ).");
        var r = JsonSerializer.Deserialize<FilterRules>(json, Json) ?? throw new FormatException("Пустые правила.");
        if (r.SchemaVersion != 1 || string.IsNullOrWhiteSpace(r.Revision) || r.Revision.Length > 80)
            throw new FormatException("Неподдерживаемая версия или некорректная ревизия правил.");
        if (r.BlockedDomains is null || r.AllowedDomains is null || r.HideSelectors is null || r.AggressiveSelectors is null || r.BlockedMailPathPrefixes is null)
            throw new FormatException("Списки правил не могут быть null.");
        foreach (var list in new[] { r.BlockedDomains, r.AllowedDomains, r.HideSelectors, r.AggressiveSelectors, r.BlockedMailPathPrefixes })
            if (list.Count > 500) throw new FormatException("Не более 500 элементов в списке.");
        foreach (var d in r.BlockedDomains.Concat(r.AllowedDomains))
            if (d is null || d.Length > 253 || !Regex.IsMatch(d, @"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant))
                throw new FormatException("Домены должны быть без протокола, пути и wildcard, в нижнем регистре.");
        foreach (var s in r.HideSelectors.Concat(r.AggressiveSelectors))
            if (string.IsNullOrWhiteSpace(s) || s.Length > 500 || s.IndexOfAny(['{', '}', ';', '@', '\\', '\n', '\r']) >= 0 || s.Contains("/*"))
                throw new FormatException("Разрешены только CSS-селекторы, не CSS-декларации и не JavaScript.");
        foreach (var path in r.BlockedMailPathPrefixes)
            if (path is null || path.Length > 200 || !Regex.IsMatch(path, @"^/api-proxy/[a-z0-9_-]+/$", RegexOptions.CultureInvariant))
                throw new FormatException("Рекламные пути должны иметь вид /api-proxy/имя/; общие API почты не блокируются.");
        return r;
    }

    public bool ShouldBlock(string url, bool enabled)
    {
        if (!enabled || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        var host = u.IdnHost;
        if (AllowedDomains.Any(d => NavigationPolicy.HostMatches(host, d))) return false;
        if (NavigationPolicy.IsMail(url) && BlockedMailPathPrefixes.Any(p => u.AbsolutePath.StartsWith(p, StringComparison.Ordinal))) return true;
        if (NavigationPolicy.IsInternal(url)) return false; // Only explicit ad proxy paths can override the mail host protection.
        return BlockedDomains.Any(d => NavigationPolicy.HostMatches(host, d));
    }
}

public static class CosmeticScript
{
    // Declarative rules only. No remote executable JS or native/network bridge.
    public static string Create(FilterRules rules, bool enabled, bool aggressive)
    {
        var selectors = enabled ? rules.HideSelectors.Concat(aggressive ? rules.AggressiveSelectors : []).ToArray() : [];
        return """
        (() => {
          if (window.top !== window || location.origin !== 'https://e.mail.ru') return;
          window.__mailtrimCleanup?.();
          const selectors = __SELECTORS__;
          const labeledAds = __LABELED__;
          const id = 'mailtrim-cosmetic-style';
          const protectedContent = '.letter-body, .letter__body, .letter-body__body, .compose-app, .compose, [contenteditable="true"], [role="textbox"], textarea, input';
          const marked = new Set();
          let observer, timer;
          const isAdLink = a => {
            try {
              const u = new URL(a.getAttribute('href'), location.href);
              return ['ad.mail.ru','r.mail.ru','rs.mail.ru','t.mail.ru'].includes(u.hostname)
                || (u.origin === location.origin && u.pathname.startsWith('/api-proxy/rb-mimic/'));
            } catch { return false; }
          };
          const hasMailLink = node => [node, ...node.querySelectorAll('a[href]')].some(a => {
            if (!a.matches('a[href]')) return false;
            try { const u = new URL(a.getAttribute('href'), location.href); return u.origin === location.origin && !isAdLink(a); }
            catch { return false; }
          });
          const hideLabeledAds = () => {
            const hide = node => { node.setAttribute('data-mailtrim-ad', 'true'); marked.add(node); };
            // Current Mail.ru wraps both ad cards and the no-ads offer in a narrow, hashed column.
            // Hide its outer layout box, not just its creatives, so the mail list can expand.
            for (const offer of document.querySelectorAll('.noads-button')) {
              if (offer.closest(protectedContent)) continue;
              let column = null;
              for (let node = offer, depth = 0; node && depth < 10; node = node.parentElement, depth++) {
                if (node.matches('body, main, #app-canvas, [role="main"]') || hasMailLink(node)) break;
                const r = node.getBoundingClientRect();
                if (r.width > 380 || r.left < innerWidth * .55) break;
                if (r.width >= 100 && r.height >= 60) column = node;
              }
              if (column) hide(column);
            }
            // Labels are DIVs with nested fragments, and ad clicks are JS handlers, not anchors.
            // Read rendered text only from small labels, outside mail bodies and real message links.
            for (const label of document.querySelectorAll('div, span, small, [aria-label="Реклама"], [aria-label="Advertisement"]')) {
              if (label.closest(protectedContent) || label.closest('[data-mailtrim-ad]')) continue;
              const link = label.closest('a[href]');
              if (link && hasMailLink(link)) continue;
              const size = label.getBoundingClientRect();
              if (size.height <= 0 || size.height > 32 || size.width > 300) continue;
              const text = (label.innerText || '').replace(/[\u200b-\u200d\ufeff]/g, '').trim();
              if (!/^(?:реклама|advertisement|sponsored)(?:\s*\d{1,2}\+)?$/iu.test(text)) continue;
              let node = label.parentElement, card = null;
              for (let depth = 0; node && depth < 16; depth++, node = node.parentElement) {
                if (node.matches('body, main, #app-canvas, [role="main"], [role="list"], .llct, .letter-list') || node.closest(protectedContent)) break;
                if (hasMailLink(node) || node.querySelector('input, textarea, button, [role="toolbar"], [role="checkbox"], [contenteditable="true"]')) break;
                const r = node.getBoundingClientRect();
                if (r.height > 500) break;
                if (r.height >= 40 && r.width >= 120) card = node;
              }
              if (card) hide(card);
            }
          };
          const apply = () => {
            if (!document.documentElement) return;
            let style = document.getElementById(id);
            if (!style) { style = document.createElement('style'); style.id = id; document.documentElement.appendChild(style); }
            style.textContent = selectors.filter(s => { try { document.querySelector(s); return true; } catch { return false; } })
              .map(s => s + ' { display: none !important; }').join('\n')
              + (labeledAds ? '\n[data-mailtrim-ad="true"] { display: none !important; }' : '');
          };
          const scan = () => {
            timer = null;
            if (!document.getElementById(id)) apply();
            // Virtualized rows may be reused for real messages: undo our markers before reevaluating.
            for (const node of marked) node.removeAttribute('data-mailtrim-ad');
            marked.clear();
            if (labeledAds) hideLabeledAds();
          };
          const start = () => {
            apply(); scan();
            observer = new MutationObserver(() => { if (!timer) timer = setTimeout(scan, 100); });
            observer.observe(document.documentElement, {childList:true, subtree:true, characterData:true});
          };
          window.__mailtrimCleanup = () => {
            observer?.disconnect(); clearTimeout(timer);
            document.removeEventListener('DOMContentLoaded', start);
            for (const node of marked) node.removeAttribute('data-mailtrim-ad');
            document.getElementById(id)?.remove();
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, {once:true});
          else start();
        })();
        """.Replace("__SELECTORS__", JsonSerializer.Serialize(selectors)).Replace("__LABELED__", enabled && rules.RemoveLabeledAds ? "true" : "false");
    }
}
