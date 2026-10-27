namespace MailTrim.Core;

// Cloud has its own file interface: never apply mail layout selectors here.
public static class CloudCosmeticScript
{
    public static string Create(bool enabled) => """
    (() => {
      if (window.top !== window || location.origin !== 'https://cloud.mail.ru') return;
      window.__mailtrimCloudCleanup?.();
      if (!__ENABLED__) return;
      const attribute = 'data-mailtrim-cloud-promo', styleId = 'mailtrim-cloud-cosmetic-style';
      const protectedContent = 'form,input,textarea,select,[contenteditable="true"],[role="textbox"],[role="document"],[role="grid"],[role="row"],[role="table"],[role="tree"],iframe,canvas,video,audio,.viewer,.preview,[data-testid*="preview"],[data-testid*="file-row"]';
      const roots = new Map(), attemptedClose = new WeakSet();
      let observer, scanning = false;
      const text = node => (node.textContent || '').replace(/[\u200b-\u200d\ufeff]/g,'').replace(/\s+/g,' ').trim();
      const closeLabel = value => /^(?:закрыть|close|×|✕|x)$/iu.test(value.trim());
      const protectedNode = node => node.matches(protectedContent) || !!node.closest(protectedContent) || !!node.querySelector(protectedContent);
      const actionable = node => node.matches('a,button,[role="button"]') || node.hasAttribute('onclick') || getComputedStyle(node).cursor === 'pointer';
      const offer = node => {
        const value = text(node);
        if (value.length > 5000) return false;
        const controls = [...node.querySelectorAll('button,a,[role="button"],div,span')];
        const action = pattern => controls.some(control => pattern.test(text(control)) && actionable(control));
        // Product/news invitations often have neither a price nor an ERID label.
        // Require both promotional copy and its matching call to action.
        const invitation = /подписывайтесь|подпишитесь|присоединяйтесь/iu.test(value)
          || /наш\s+канал|официальн[а-я]*\s+канал/iu.test(value) && /следите|будьте в курсе|новост|обновлен|акци/iu.test(value);
        const channel = invitation
          && /канал|сообщество/iu.test(value) && /\bMAX\b|макс|телеграм|telegram|вконтакте|\bVK\b|новост|обновлен|акци/iu.test(value);
        if (channel && action(/^(?:Подписаться|Подписаться на канал|Присоединиться|Открыть канал|Перейти в канал)$/iu)) return true;
        const app = /(?:установите|скачайте|обновите|попробуйте|мобильное).{0,90}приложен/iu.test(value)
          && /Облако|Почт[аыу]|Mail|RuStore/iu.test(value);
        if (app && action(/^(?:Установить|Скачать|Обновить|Попробовать)(?: приложение| Облако| Почту| в RuStore)?$|^(?:Подробнее|Узнать больше)$/iu)) return true;
        const disclosed = /erid\s*[:=]\s*[a-z0-9]/iu.test(value) && /реклам|акци[яиюй]/iu.test(value);
        if (disclosed && action(/^(?:Играть|Участвовать|Узнать больше|Подробнее|Перейти|Купить|Попробовать(?: Облако)?|Подписаться)$/iu)) return true;
        if (!/(?:терабайт|гигабайт|\d\s*[тг]б\b|памят[ьи]|мест[оа]|тариф|подписк|хранилищ|облак)/iu.test(value)) return false;
        const priced = /(?:\d[\d\s.,]*\s*(?:₽|руб)|скидк|выгодн.{0,20}цен)/iu.test(value) && /(?:месяц|в год|первый год|тариф|подписк)/iu.test(value);
        if (!disclosed && !priced) return false;
        return action(/^(?:Попробовать(?: Облако| за .+)?|Подключить(?: Облако| тариф| подписку)?|Увеличить место|Выбрать тариф|Получить скидку)$/iu);
      };
      const overlay = node => {
        const r = node.getBoundingClientRect();
        if (r.width < 200 || r.height < 64 || r.width > innerWidth * .98 || r.height > innerHeight * .98) return false;
        for (let p = node, depth = 0; p && depth < 8 && p !== document.body; p = p.parentElement, depth++) {
          if (p.matches('[role="dialog"],[aria-modal="true"]') || ['fixed','absolute'].includes(getComputedStyle(p).position)) return true;
        }
        return false;
      };
      const shade = node => {
        if (text(node) || protectedNode(node) || node.querySelector('button,a,[role="button"],[role="dialog"]')) return false;
        const r = node.getBoundingClientRect(), s = getComputedStyle(node);
        if (!['fixed','absolute'].includes(s.position) || r.width < innerWidth * .7 || r.height < innerHeight * .7) return false;
        const color = s.backgroundColor.match(/[\d.]+/g)?.map(Number);
        return !!color && color.length >= 3 && (color.length < 4 || color[3] > .02);
      };
      const decoration = node => {
        if (protectedNode(node) || node.matches('[role="dialog"]') || node.querySelector('[role="dialog"]')) return false;
        const value = text(node);
        if (value && !closeLabel(value)) return false;
        const controls = [...node.querySelectorAll('button,a,[role="button"]')];
        if (node.matches('button,a,[role="button"]')) controls.push(node);
        return controls.every(control => {
          const label = (control.getAttribute('aria-label') || control.getAttribute('title') || text(control)).trim();
          const r = control.getBoundingClientRect();
          return !control.matches('a') && r.width <= 64 && r.height <= 64
            && (closeLabel(label) || !label && !!control.querySelector('svg'));
        });
      };
      const restore = (root, state) => { root.removeAttribute(attribute); for (const node of state.shades) node.removeAttribute(attribute); roots.delete(root); };
      const hide = node => { if (node.getAttribute(attribute) !== 'true') node.setAttribute(attribute,'true'); };
      const scan = () => {
        if (scanning) return;
        scanning = true;
        try {
          if (!document.getElementById(styleId) && document.documentElement) {
            const style = document.createElement('style'); style.id = styleId;
            style.textContent = '[' + attribute + '="true"]{display:none!important}';
            (document.head || document.documentElement).appendChild(style);
          }
          for (const [root,state] of roots) {
            const value = text(root);
            const newDialog = [...root.querySelectorAll('[role="dialog"]')].some(dialog => !state.dialogs.has(dialog));
            if (!root.isConnected || protectedNode(root) || newDialog || value && !closeLabel(value) && !offer(root)) { restore(root,state); continue; }
            hide(root);
            for (const node of state.shades) {
              if (!node.isConnected || protectedNode(node) || text(node) || node.querySelector('button,a,[role="button"],[role="dialog"]')) {
                node.removeAttribute(attribute); state.shades.delete(node);
              } else hide(node);
            }
          }
          // A SPA can clone a node with our marker. A new node must earn its own
          // classification instead of inheriting display:none from the old offer.
          const owned = new Set([...roots].flatMap(([root,state]) => [root,...state.shades]));
          for (const node of document.querySelectorAll('[' + attribute + ']')) if (!owned.has(node)) node.removeAttribute(attribute);
          const walker = document.createTreeWalker(document.body || document, NodeFilter.SHOW_TEXT);
          const checked = new Set(); let leaf;
          while ((leaf = walker.nextNode())) {
            if (!/erid|реклам|терабайт|гигабайт|попробовать|подключить|увеличить место|выбрать тариф|подпис|канал|приложен/iu.test(leaf.nodeValue || '')) continue;
            for (let panel = leaf.parentElement, depth = 0; panel && depth < 10 && !panel.matches('body,html,main,[role="main"]'); panel = panel.parentElement, depth++) {
              if (checked.has(panel)) continue; checked.add(panel);
              if (protectedNode(panel)) break;
              if (roots.has(panel) || !offer(panel) || !overlay(panel)) continue;
              let root = panel;
              // Include decorative artwork and close controls around this one
              // offer, even when the site puts its copy in a separate inner box.
              for (let parent = root.parentElement, level = 0; parent && level < 8 && !parent.matches('body,html,main,[role="main"]'); parent = root.parentElement, level++) {
                if (protectedNode(parent)) break;
                const extra = [...parent.children].filter(child => child !== root);
                if (extra.some(child => !shade(child) && !decoration(child))) break;
                root = parent;
              }
              if (roots.has(root)) break;
              const close = [...root.querySelectorAll('button,[role="button"]')].find(control => {
                const label = (control.getAttribute('aria-label') || control.getAttribute('title') || text(control)).trim();
                const r = control.getBoundingClientRect();
                return closeLabel(label) && r.width > 0 && r.width <= 64 && r.height > 0 && r.height <= 64;
              });
              const shades = new Set();
              for (const sibling of root.parentElement?.children || []) if (sibling !== root && shade(sibling)) shades.add(sibling);
              roots.set(root, { shades, dialogs: new Set(root.querySelectorAll('[role="dialog"]')) });
              hide(root); for (const node of shades) hide(node);
              if (close && !attemptedClose.has(close)) { attemptedClose.add(close); try { close.click(); } catch {} }
              break;
            }
          }
        } finally { scanning = false; }
      };
      observer = new MutationObserver(scan);
      observer.observe(document, {subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['class','style','role','aria-modal','aria-label','hidden']});
      window.__mailtrimCloudCleanup = () => {
        observer.disconnect();
        for (const [root,state] of roots) restore(root,state);
        for (const node of document.querySelectorAll('[' + attribute + ']')) node.removeAttribute(attribute);
        document.getElementById(styleId)?.remove();
      };
      scan();
    })();
    """.Replace("__ENABLED__", enabled ? "true" : "false");
}
