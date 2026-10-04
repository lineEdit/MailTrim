using System.Text.Json;

namespace MailTrim.Core;

public enum ReaderAction { Reply, MarkRead, Archive, Delete }

public static class ReaderActionScript
{
    public static string Create(ReaderAction action, string url)
    {
        var labels = action switch
        {
            ReaderAction.Reply => new[] { "Ответить", "Reply" },
            ReaderAction.MarkRead => ["Отметить прочитанным", "Пометить прочитанным", "Mark as read"],
            ReaderAction.Archive => ["В архив", "Архивировать", "Archive"],
            ReaderAction.Delete => ["Удалить", "Delete"],
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return """
        (()=>{
          const expected = new URL(EXPECTED), labels = LABELS;
          if(location.origin!=='https://e.mail.ru' || expected.origin!==location.origin || expected.search!==location.search ||
             location.pathname.replace(/\/$/,'')!==expected.pathname.replace(/\/$/,'')) return 'wrong-message';
          if(!document.querySelector('.letter-body__body-content,.letter-body__body')) return 'not-ready';
          // Never use an action inside message HTML, nor a bulk-selection toolbar.
          if(document.querySelector('a.js-letter-list-item')) return 'ambiguous';
          if(IS_DELETE && !/^\/(inbox|sent|archive|spam)\/[^/]+\/?$/.test(location.pathname)) return 'manual-delete';
          const candidates=[...document.querySelectorAll('[role="toolbar"] button,[role="toolbar"] [role="button"],.portal-menu button,.portal-menu [role="button"],.letter-toolbar button,.letter-toolbar [role="button"]')]
            .filter(e=>!e.closest('.letter-body__body-content,.letter-body__body,iframe') &&
              !e.disabled && e.getAttribute('aria-disabled')!=='true' && e.getBoundingClientRect().height>0 &&
              getComputedStyle(e).visibility!=='hidden' &&
              [e.getAttribute('aria-label'),e.getAttribute('title'),e.textContent].some(t=>labels.includes((t||'').trim())));
          if(candidates.length!==1) return 'unavailable';
          candidates[0].click(); return 'clicked';
        })()
        """.Replace("EXPECTED", JsonSerializer.Serialize(url))
            .Replace("LABELS", JsonSerializer.Serialize(labels))
            .Replace("IS_DELETE", action == ReaderAction.Delete ? "true" : "false");
    }
}
