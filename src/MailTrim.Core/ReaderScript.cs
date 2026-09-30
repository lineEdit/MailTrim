namespace MailTrim.Core;

// Only rendered official-page DOM is read. No private mail API or credentials.
public static class ReaderScript
{
    public const string List = """
    (() => {
      if(location.origin!=='https://e.mail.ru') return [];
      return [...document.querySelectorAll('a.js-letter-list-item')].slice(0,100).map(row=>{
        const atoms=[...row.querySelectorAll('*')].filter(e=>!e.closest('button,svg,aside') && e.getBoundingClientRect().height>0)
          .map(e=>({e,t:[...e.childNodes].filter(n=>n.nodeType===3).map(n=>n.textContent).join('').trim()})).filter(x=>x.t);
        return {url:row.href,sender:atoms[0]?.t||'',subject:atoms[1]?.t||'(без темы)',
          preview:atoms.slice(2,-1).map(x=>x.t).join(' ').slice(0,300),date:atoms.at(-1)?.t||''};
      }).filter(x=>new URL(x.url).origin===location.origin);
    })()
    """;

    public const string Body = """
    (() => {
      if(location.origin!=='https://e.mail.ru') return null;
      const root=document.querySelector('.letter-body__body-content');
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

public sealed record ReaderLetter(string Url, string Sender, string Subject, string Preview, string Date);
public sealed record ReaderBlock(string Text, string Image);
