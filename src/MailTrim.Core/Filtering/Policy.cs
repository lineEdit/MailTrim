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
          const compactLayout = __ENABLED__;
          const id = 'mailtrim-cosmetic-style';
          const protectedContent = '.letter-body, .letter__body, .letter-body__body, .compose-app, .compose, [contenteditable="true"], [role="textbox"], textarea, input';
          const protectedSlot = protectedContent.split(',').flatMap(s => [s.trim(), s.trim() + ' *']).join(',');
          const marked = new Set();
          const topSlots = new Set();
          const topHosts = new Map(); let nextHost = 0;
          const workingTop = protectedContent + ',.thread,.letter-list__react,.llct,.letter-list,[role="toolbar"],[role="checkbox"],[role="dialog"],[role="alert"],form,h1,h2,a[href^="/"]:not([href^="//"]):not([href^="/api-proxy/"]),a[href^="https://e.mail.ru/"]:not([href*="/api-proxy/"])';
          let topCSS = '';
          const rememberTopSlot = slot => {
            const host = slot.parentElement; if (!host) return;
            for (const parent of topHosts.keys()) if (!parent.isConnected) topHosts.delete(parent);
            let entry = topHosts.get(host);
            if (!entry) { if (topHosts.size >= 16) return; entry = { id: String(++nextHost), slots: new Set() }; topHosts.set(host, entry); }
            host.setAttribute('data-mailtrim-top-host', entry.id);
            entry.slots.add([...host.children].indexOf(slot)+1);
            topCSS = [...topHosts].flatMap(([parent, state]) => [...state.slots].map(index =>
              `[data-mailtrim-top-host="${state.id}"] > :nth-child(${index}):not(${protectedSlot}):not(${workingTop}):not(:has(${workingTop})) { display: none !important; }`)).join('\n');
            apply(); // Reserve zero height before a replacement creative gets its first paint.
          };
          const promos = new Set();
          const promoIslands = new Map();
          const storeTitle = /^Актуальная\s*версия\s*Почты\s*в\s*RuStore$/iu;
          const promoText = node => (node.textContent || '').replace(/[\u200b-\u200d\ufeff]/g, '').trim();
          const closeLabel = text => /^(?:закрыть|close|×|✕|x)$/iu.test(text.trim());
          const overlayProtected = protectedContent + ',.thread,.letter-list__react,.llct,.letter-list,.js-letter-list-item,[role="toolbar"],[role="alert"],form,nav';
          const hasStoreTitle = node => [node,...node.querySelectorAll('h1,h2,h3,div,span')].some(e => storeTitle.test(promoText(e)));
          // Once positively identified, an empty shell is still the same promo. Release
          // it as soon as the site reuses the island for meaningful working content.
          const restorePromoIslands = () => {
            for (const [root, state] of promoIslands) {
              const text = promoText(root);
              if (!root.isConnected || root.matches(overlayProtected) || root.querySelector(overlayProtected)
                  || [...root.querySelectorAll('[role="dialog"]')].some(dialog => !state.dialogs.has(dialog))
                  || (!hasStoreTitle(root) && text && !closeLabel(text))) { promoIslands.delete(root); continue; }
              root.setAttribute('data-mailtrim-promo','true'); promos.add(root);
            }
          };
          const attemptedClose = new WeakSet();
          const expanded = new Set();
          const widened = new Set();
          const stretched = new Set();
          const watched = new WeakSet();
          let observer, rootObserver, resizeObserver, timer, revealFrame, revealTimeout;
          const initialGuard = compactLayout && document.readyState === 'loading';
          // Mail.ru's hashed content wrapper can retain a bottom ad reservation.
          // Only expand a sole visible child aligned with a viewport-height parent.
          const fitMailArea = () => {
            for (const node of widened) node.removeAttribute('data-mailtrim-wide');
            widened.clear();
            for (const node of expanded) node.removeAttribute('data-mailtrim-fill');
            expanded.clear();
            for (const node of stretched) { node.removeAttribute('data-mailtrim-viewport'); node.style.removeProperty('--mailtrim-top'); }
            stretched.clear();
            if (!compactLayout) return;
            const stretch = (box, top) => {
              box.style.setProperty('--mailtrim-top', top + 'px');
              box.setAttribute('data-mailtrim-viewport', 'true'); stretched.add(box);
            };
            for (const root of document.querySelectorAll('.thread, .letter-list__react')) {
              if (root.closest(protectedContent)) continue;
              // Expand a centered mail layout only when its parent fills the window.
              for (let node = root.parentElement, depth = 0; node && depth < 12; node = node.parentElement, depth++) {
                if (node.matches('body,html')) break;
                const r = node.getBoundingClientRect(), p = node.parentElement?.getBoundingClientRect();
                if (!p || node.querySelector('.compose-app,.compose,[contenteditable="true"],textarea,input[type="password"]')) continue;
                const gap = r.left - p.left;
                if (p.width < innerWidth * .95 || r.width < innerWidth * .65 || r.height < innerHeight * .5 || gap < 32
                    || Math.abs(gap - (p.right-r.right)) > 4) continue;
                const folder = [...node.querySelectorAll('a[href]')].some(a => { try { const u=new URL(a.href); return u.origin===location.origin && /^\/inbox\/?$/.test(u.pathname); } catch { return false; } });
                if (!folder) continue;
                node.setAttribute('data-mailtrim-wide','true'); widened.add(node); break;
              }
              // The virtualized list keeps the old banner height in its inner boxes,
              // while its outer root already has the full (and overflowing) page height.
              if (root.matches('.letter-list__react')) {
                const list = root.querySelector('.ReactVirtualized__List');
                if (list && !watched.has(list)) { resizeObserver?.observe(list); watched.add(list); }
                const lr = list?.getBoundingClientRect(), pr = root.parentElement?.getBoundingClientRect();
                let boundary = lr?.bottom;
                for (let box = list?.parentElement; box && box !== root; box = box.parentElement) {
                  const br = box.getBoundingClientRect();
                  if (br.height > 100 && boundary !== undefined) boundary = Math.min(boundary, br.bottom);
                }
                if (lr && pr && lr.height > 100 && Math.abs(pr.bottom - innerHeight) < 3
                    && innerHeight - boundary >= 32 && innerHeight - boundary <= 220) {
                  const boxes = [];
                  for (let box = list; box && box !== root.parentElement; box = box.parentElement) {
                    const br = box.getBoundingClientRect();
                    if (br.height >= 100 && (box === root || box === list || Math.abs(br.bottom - boundary) < 2)) boxes.push([box, br.top]);
                  }
                  for (const [box, top] of boxes) stretch(box, top);
                  continue;
                }
              }
              for (let node = root.parentElement, depth = 0; node && depth < 12; node = node.parentElement, depth++) {
                const parent = node.parentElement;
                if (!parent || node.matches('.application, body, html')) break;
                const r = node.getBoundingClientRect(), p = parent.getBoundingClientRect();
                const gap = p.bottom - r.bottom;
                if (r.height < 100 || gap < 32 || gap > 220 || Math.abs(p.bottom - innerHeight) > 3
                    || Math.abs(r.top - p.top) > 1 || Math.abs(r.width - p.width) > 2) continue;
                if ([...parent.children].some(s => s !== node && s.getBoundingClientRect().height > 0)) continue;
                const ps = getComputedStyle(parent);
                if (parseFloat(ps.paddingTop) || parseFloat(ps.paddingBottom)) continue;
                // Inner scroll boxes also have fixed heights computed by the site.
                // Extend only boxes ending at the old reservation boundary, never the message body.
                const boxes = [];
                for (let box = root.parentElement; box && box !== node; box = box.parentElement) boxes.push(box);
                if (root.matches('.letter-list__react')) boxes.push(...root.querySelectorAll('.ReactVirtualized__List'));
                for (const box of boxes) {
                  const br = box.getBoundingClientRect();
                  if (br.height < 100 || Math.abs(br.bottom - r.bottom) > 2 || br.top < r.top) continue;
                  stretch(box, br.top);
                }
                node.setAttribute('data-mailtrim-fill', 'true'); expanded.add(node);
                break;
              }
            }
          };
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
          const hideTopPreloader = () => {
            const workingContent = node => node.closest(protectedContent) || node.querySelector(protectedContent)
              || node.querySelector('.thread,.letter-list__react,.llct,.letter-list,[role="toolbar"],[role="checkbox"],[role="dialog"],[role="alert"],form,h1,h2') || hasMailLink(node);
            // The recording shows two empty creative rectangles with thin text bars,
            // before the list toolbar. There is no ad label yet and classes are hashed.
            // Learn only this textless shape outside mail; retain its slot through loading.
            for (const slot of topSlots) {
              if (!slot.isConnected || workingContent(slot)) { slot.removeAttribute('data-mailtrim-top-ad'); topSlots.delete(slot); }
              else slot.setAttribute('data-mailtrim-top-ad','true');
            }
            const gray = node => {
              const c = getComputedStyle(node).backgroundColor.match(/^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$/);
              if (!c || (c[4] !== undefined && +c[4] < .95)) return false;
              const rgb = c.slice(1,4).map(Number);
              return Math.min(...rgb) >= 180 && Math.max(...rgb) < 250 && Math.max(...rgb) - Math.min(...rgb) <= 15;
            };
            for (const list of document.querySelectorAll('.letter-list__react,.thread')) {
              if (list.closest(protectedContent)) continue;
              const lr = list.getBoundingClientRect();
              if (lr.width < 400 || lr.height <= 0) continue;
              for (let branch = list, depth = 0; branch?.parentElement && depth < 8; branch = branch.parentElement, depth++) {
                if (branch.matches('body,html,#app-canvas')) break;
                for (const slot of branch.parentElement.children) {
                  if (slot === branch || topSlots.has(slot) || workingContent(slot) || slot.textContent.trim()
                      || slot.querySelector('button,a,img,svg,canvas,iframe,video,[role="button"]')) continue;
                  const r = slot.getBoundingClientRect();
                  if (r.height < 48 || r.height > 180 || r.width < lr.width * .75 || r.width > lr.width + 4
                      || Math.abs(r.left-lr.left) > 12 || r.top < 32 || r.bottom > lr.top + 2 || lr.top-r.bottom > 100) continue;
                  const shapes = [...slot.querySelectorAll('div,span')].filter(gray).map(n => n.getBoundingClientRect());
                  const cards = shapes.filter(b => b.width >= 70 && b.width <= 350 && b.height >= 40 && b.height <= 140);
                  const bars = shapes.filter(b => b.width >= 100 && b.height >= 1 && b.height <= 12);
                  const pair = cards.some((a,i) => cards.slice(i+1).some(b =>
                    b.left-a.right >= 100 && Math.abs(a.top-b.top) <= 4 && Math.abs(a.width-b.width) <= 4 && Math.abs(a.height-b.height) <= 4
                    && [a,b].every(card => bars.some(bar => bar.left >= card.right && bar.top >= card.top && bar.bottom <= card.bottom))));
                  const single = cards.length === 1 && bars.some(bar => bar.left >= cards[0].right && bar.top >= cards[0].top && bar.bottom <= cards[0].bottom);
                  if (!pair && !single) continue;
                  slot.setAttribute('data-mailtrim-top-ad','true'); topSlots.add(slot); rememberTopSlot(slot);
                }
              }
            }
          };
          const hideLabeledAds = () => {
            const hide = node => { node.setAttribute('data-mailtrim-ad', 'true'); marked.add(node); };
            // Current Mail.ru wraps both ad cards and the no-ads offer in a narrow, hashed column.
            // Hide its outer layout box, not just its creatives, so the mail list can expand.
            for (const offer of document.querySelectorAll('.noads-button')) {
              if (offer.closest(protectedContent)) continue;
              let column = null;
              for (let node = offer, depth = 0; node && depth < 10; node = node.parentElement, depth++) {
                if (node.matches('body, main, #app-canvas, [role="main"]') || node.querySelector(protectedContent) || hasMailLink(node)) break;
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
                if (node.matches('body, main, #app-canvas, [role="main"], [role="list"], .llct, .letter-list') || node.closest(protectedContent) || node.querySelector(protectedContent)) break;
                if (hasMailLink(node) || node.querySelector('input, textarea, button, [role="toolbar"], [role="checkbox"], [contenteditable="true"]')) break;
                const r = node.getBoundingClientRect();
                if (r.height > 500) break;
                if (r.height >= 40 && r.width >= 120) card = node;
              }
              if (card) hide(card);
            }
          };
          const dismissStorePromo = () => {
            const decoration = node => {
              if (node.matches(overlayProtected + ',[role="dialog"]') || node.querySelector(overlayProtected + ',[role="dialog"]')) return false;
              const text = promoText(node);
              if (text && !closeLabel(text)) return false;
              const controls = [...node.querySelectorAll('button,[role="button"],a')];
              if (node.matches('button,[role="button"],a')) controls.push(node);
              return controls.every(control => closeLabel(control.getAttribute('aria-label') || control.getAttribute('title') || promoText(control))
                || !promoText(control) && !!control.querySelector('svg') && !control.matches('a'));
            };
            for (const heading of document.querySelectorAll('h1,h2,h3,div,span')) {
              if (heading.closest(protectedContent) || !storeTitle.test(promoText(heading))) continue;
              for (let panel = heading.parentElement, depth = 0; panel && depth < 10; panel = panel.parentElement, depth++) {
                if (panel.matches('body,main,#app-canvas,[role="main"]') || panel.querySelector(overlayProtected + ',[role="dialog"]')) break;
                const text = promoText(panel).replace(/\s+/g, ' ');
                const r = panel.getBoundingClientRect();
                if (text.length > 1500 || r.width > 650 || r.height > 850) break;
                if (!/Установите\s*или\s*обновите\s*приложение/iu.test(text) || !/Android/iu.test(text) || r.width < 200 || r.height < 24) continue;
                let root = panel;
                // Close controls and a blank shade are decoration, not extra dialog
                // content. Include every isolated wrapper so a white frame cannot remain.
                for (let node = panel.parentElement, n = 0; node && n < 10; node = node.parentElement, n++) {
                  if (node.matches('body,main,#app-canvas,[role="main"]') || node.querySelector(overlayProtected)) break;
                  const siblings = [...node.children].filter(child => !child.contains(root));
                  const ownText = [...node.childNodes].filter(child => child.nodeType === 3).map(child => child.textContent).join('').trim();
                  if (ownText && !closeLabel(ownText) || !siblings.every(decoration)) break;
                  root = node;
                }
                let frame = panel;
                for (let node = panel.parentElement; node && root.contains(node); node = node.parentElement) {
                  const b = node.getBoundingClientRect();
                  if (b.width >= 200 && b.width <= 650 && b.height >= 24 && b.height <= 850) frame = node;
                  if (node === root) break;
                }
                const bounds = frame.getBoundingClientRect();
                const close = [...new Set([...root.querySelectorAll('button,[role="button"],[aria-label],[title],svg')].map(node =>
                  node.matches('svg') ? node.closest('button,[role="button"],[aria-label],[title]') || node.parentElement : node))].filter(button => {
                  if (!(button instanceof HTMLElement)) return false;
                  const label = (button.getAttribute('aria-label') || button.getAttribute('title') || promoText(button)).trim();
                  const b = button.getBoundingClientRect();
                  // Locate the corner against the visible panel, not a full-screen shade.
                  const corner = b.width > 0 && b.width <= 48 && b.height <= 48
                    && b.top < bounds.top + 70 && b.right > bounds.right - 70
                    && b.left < bounds.right + 48 && b.bottom > bounds.top - 48;
                  return corner && (closeLabel(label) || (!label && button.querySelector('svg')));
                });
                if (!promoIslands.has(root)) promoIslands.set(root, { dialogs: new Set(root.querySelectorAll('[role="dialog"]')) });
                if (close.length === 1 && !attemptedClose.has(root)) { attemptedClose.add(root); close[0].click(); }
                if (root.isConnected) { root.setAttribute('data-mailtrim-promo','true'); promos.add(root); }
                break;
              }
            }
          };
          const apply = () => {
            if (!document.documentElement) return;
            let style = document.getElementById(id);
            if (!style) { style = document.createElement('style'); style.id = id; document.documentElement.appendChild(style); }
            style.textContent = selectors.filter(s => { try { document.querySelector(s); return true; } catch { return false; } })
              .map(s => s + ' { display: none !important; }').join('\n')
              + (initialGuard ? '\nhtml[data-mailtrim-pending="true"] { opacity: 0 !important; }' : '')
              + (labeledAds ? '\n[data-mailtrim-ad="true"] { display: none !important; }' : '')
              + (labeledAds ? '\n[data-mailtrim-top-ad="true"] { display: none !important; }\n' + topCSS : '')
              + (labeledAds ? `\n.js-banner-wrapper-container:not(${protectedSlot}):not(:has(${protectedContent})) { display: none !important; }` : '')
              + (compactLayout ? '\n[data-mailtrim-promo="true"] { display: none !important; }' : '')
              + (compactLayout ? '\n[data-mailtrim-wide="true"] { width: 100% !important; max-width: none !important; margin-inline: 0 !important; box-sizing: border-box !important; }\n[data-mailtrim-fill="true"] { height: 100% !important; }\n[data-mailtrim-viewport="true"] { height: calc(100vh - var(--mailtrim-top)) !important; }\n.thread > .thread__footer:not(.letter-body *, .letter__body *, .compose *, .compose-app *, [contenteditable="true"] *):not(:has(button, input, textarea, [contenteditable="true"])) { display: none !important; }' : '');
          };
          const cleanAds = () => {
            if (!document.getElementById(id)) apply();
            // Virtualized rows may be reused for real messages: undo our markers before reevaluating.
            for (const node of marked) node.removeAttribute('data-mailtrim-ad');
            marked.clear();
            for (const node of promos) node.removeAttribute('data-mailtrim-promo');
            promos.clear();
            if (compactLayout) restorePromoIslands();
            if (labeledAds) { hideTopPreloader(); hideLabeledAds(); }
            if (compactLayout) dismissStorePromo();
          };
          const scan = () => {
            timer = null;
            cleanAds();
            fitMailArea();
          };
          const schedule = () => {
            // Restore early CSS immediately; evaluate newly inserted ads before the next paint.
            if (document.documentElement && !document.getElementById(id)) apply();
            // DOM changes delivered during an animation frame cannot wait for another
            // animation frame: the browser would paint the new ad in between.
            cleanAds();
            if (timer == null) timer = requestAnimationFrame(scan);
          };
          const changed = records => {
            const withoutFill = value => (value || '').replace(/--mailtrim-top\s*:[^;]*/g,'').replace(/[;\s]/g,'');
            const relevant = records.some(record => {
              const node = record.target.nodeType === Node.ELEMENT_NODE ? record.target : record.target.parentElement;
              if (node?.closest('#' + id)) return false;
              // Our stylesheet and layout variables must not create an observer loop.
              if (record.type === 'childList' && !record.removedNodes.length && record.addedNodes.length
                  && [...record.addedNodes].every(n => n.nodeType === Node.ELEMENT_NODE && n.id === id)) return false;
              if (record.type === 'attributes' && record.attributeName === 'style'
                  && withoutFill(record.oldValue) === withoutFill(node.getAttribute('style'))) return false;
              return true;
            });
            if (relevant) schedule();
          };
          const reveal = () => {
            try { scan(); }
            finally { document.documentElement?.removeAttribute('data-mailtrim-pending'); clearTimeout(revealTimeout); }
          };
          const ready = () => { scan(); revealFrame = requestAnimationFrame(reveal); };
          const start = () => {
            if (!document.documentElement || observer) return;
            rootObserver?.disconnect();
            if (initialGuard) {
              document.documentElement.setAttribute('data-mailtrim-pending', 'true');
              // Fail open on slow scripts or throttled background frames; never strand login.
              revealTimeout = setTimeout(reveal, 1500);
              document.addEventListener('DOMContentLoaded', ready, {once:true});
            }
            resizeObserver = new ResizeObserver(schedule);
            apply(); scan();
            observer = new MutationObserver(changed);
            observer.observe(document.documentElement, {childList:true, subtree:true, characterData:true,
              attributes:true, attributeOldValue:true, attributeFilter:['class','style','href','aria-label','title','role','hidden']});
            window.addEventListener('resize', schedule);
            document.addEventListener('load', schedule, true);
          };
          window.__mailtrimCleanup = () => {
            observer?.disconnect(); rootObserver?.disconnect(); resizeObserver?.disconnect(); cancelAnimationFrame(timer); cancelAnimationFrame(revealFrame); clearTimeout(revealTimeout);
            document.removeEventListener('DOMContentLoaded', ready);
            document.documentElement?.removeAttribute('data-mailtrim-pending');
            window.removeEventListener('resize', schedule);
            document.removeEventListener('load', schedule, true);
            for (const host of topHosts.keys()) host.removeAttribute('data-mailtrim-top-host'); topHosts.clear();
            for (const node of marked) node.removeAttribute('data-mailtrim-ad');
            for (const node of topSlots) node.removeAttribute('data-mailtrim-top-ad');
            for (const node of promos) node.removeAttribute('data-mailtrim-promo');
            for (const node of widened) node.removeAttribute('data-mailtrim-wide');
            for (const node of expanded) node.removeAttribute('data-mailtrim-fill');
            for (const node of stretched) { node.removeAttribute('data-mailtrim-viewport'); node.style.removeProperty('--mailtrim-top'); }
            promoIslands.clear();
            document.getElementById(id)?.remove();
          };
          // WebView2 injects this before page scripts. Attach CSS as soon as the root exists.
          if (document.documentElement) start();
          else { rootObserver = new MutationObserver(start); rootObserver.observe(document, {childList:true}); }
        })();
        """.Replace("__SELECTORS__", JsonSerializer.Serialize(selectors)).Replace("__LABELED__", enabled && rules.RemoveLabeledAds ? "true" : "false").Replace("__ENABLED__", enabled ? "true" : "false");
    }
}
