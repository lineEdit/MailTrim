namespace MailTrim.Core;

// Only rendered official-page DOM is read. No private mail API or credentials.
public static class ReaderScript
{
    public const string List = """
    (() => {
      if(location.origin!=='https://e.mail.ru') return [];
      const now=new Date(), offset=-now.getTimezoneOffset();
      const captured=new Date(now.getTime()+offset*60000).toISOString().slice(0,-1)
        +(offset>=0?'+':'-')+String(Math.floor(Math.abs(offset)/60)).padStart(2,'0')+':'+String(Math.abs(offset)%60).padStart(2,'0');
      return [...document.querySelectorAll('a.js-letter-list-item')].slice(0,100).map(row=>{
        const atoms=[...row.querySelectorAll('*')].filter(e=>!e.closest('button,svg,aside') && e.getBoundingClientRect().height>0)
          .map(e=>({e,t:[...e.childNodes].filter(n=>n.nodeType===3).map(n=>n.textContent).join('').trim()})).filter(x=>x.t);
        const last=atoms.at(-1)?.e;
        const time=row.querySelector('time[datetime]');
        const epoch=row.querySelector('[data-timestamp]')?.getAttribute('data-timestamp');
        let received=null;
        if(time && /^\d{4}-\d{2}-\d{2}T/.test(time.getAttribute('datetime')||'')) {
          const value=Date.parse(time.getAttribute('datetime')); if(Number.isFinite(value))received=new Date(value).toISOString();
        } else if(/^\d{10}(?:\d{3})?$/.test(epoch||'')) {
          const value=Number(epoch)*(epoch.length===10?1000:1); if(value>0 && value<4102444800000)received=new Date(value).toISOString();
        }
        let hint='';
        for(let e=last,n=0;e && e!==row && n<4;e=e.parentElement,n++) { if(e.getAttribute('title')) { hint=e.getAttribute('title').slice(0,150); break; } }
        return {url:row.href,sender:atoms[0]?.t||'',subject:atoms[1]?.t||'(без темы)',
          preview:atoms.slice(2,-1).map(x=>x.t).join(' ').slice(0,300),date:atoms.at(-1)?.t||'',receivedAt:received,capturedAt:captured,dateHint:hint};
      }).filter(x=>new URL(x.url).origin===location.origin);
    })()
    """;

    public const string Body = """
    (() => {
      if(location.origin!=='https://e.mail.ru') return null;
      const root=document.querySelector('.letter-body__body-content') || document.querySelector('.letter-body__body');
      if(!root) return null;
      const blocks=[]; let text='';
      const flush=()=>{if(text.trim()) blocks.push({text:text.trim().slice(0,20000),image:''});text='';};
      const walk=node=>{
        if(blocks.length>=300) return;
        if(node.nodeType===3){text+=node.textContent;return;}
        if(node.nodeType!==1 || node.matches('script,style,iframe,object,embed,svg,form,input,button,textarea'))return;
        if(node.hidden || getComputedStyle(node).display==='none')return;
        if(node.tagName==='IMG'){
          flush();const u=node.currentSrc||node.src;
          if(/^https:\/\//i.test(u) && !(node.width>0&&node.width<=2) && !(node.height>0&&node.height<=2))blocks.push({text:node.alt||'Изображение',image:u});return;
        }
        if(node.tagName==='BR')text+='\n';
        const block=/^(P|DIV|TR|LI|H[1-6]|BLOCKQUOTE|TABLE)$/.test(node.tagName);
        if(block)flush();for(const child of node.childNodes)walk(child);if(block)flush();
      };
      walk(root);flush();return blocks;
    })()
    """;
}

public sealed record ReaderLetter(string Url, string Sender, string Subject, string Preview, string Date,
    DateTimeOffset? ReceivedAt = null, DateTimeOffset? CapturedAt = null, string DateHint = "")
{
    [System.Text.Json.Serialization.JsonIgnore] public string DateLabel => ReaderChronology.DateKey(this) is null ? Date + " · дата не уточнена" : Date;
}
public sealed record ReaderBlock(string Text, string Image);

