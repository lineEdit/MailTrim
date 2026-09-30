namespace MailTrim.Core;

public sealed record MailboxStatus(string State, int? Unread, DateTimeOffset? CheckedAt)
{
    public string Description => State + (CheckedAt is { } time ? $" · {time.LocalDateTime:HH:mm}" : "")
        + (Unread is { } count ? $" · Непрочитанных во входящих: {count}" : " · Счётчик неизвестен");
}

public static class MailboxStatusScript
{
    public const string Snapshot = """
    (()=>{
      if(location.origin!=='https://e.mail.ru') return {ready:false,unread:null};
      const inbox=[...document.querySelectorAll('a[href]')].find(a=>new URL(a.href).origin===location.origin && /^\/inbox\/?$/.test(new URL(a.href).pathname) && !a.matches('.js-letter-list-item'));
      if(!inbox)return {ready:false,unread:null};
      const explicit=inbox.getAttribute('data-unread-count');
      if(explicit!==null && /^\d+$/.test(explicit))return {ready:true,unread:Number(explicit)};
      const label=inbox.getAttribute('aria-label')||inbox.getAttribute('title')||'';
      const labeled=label.match(/(\d+)\s*непрочитан/i)||label.match(/непрочитан[^\d]*?(\d+)/i);
      if(labeled)return {ready:true,unread:Number(labeled[1])};
      const leaves=[...inbox.querySelectorAll('span,div')].filter(e=>e.children.length===0).map(e=>e.textContent.trim());
      const numbers=leaves.filter(t=>/^\d+$/.test(t));
      return {ready:true,unread:numbers.length===1?Number(numbers[0]):null};
    })()
    """;
    public sealed record SnapshotResult(bool Ready, int? Unread);
}
