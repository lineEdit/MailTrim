namespace MailTrim.Core;

// Read-only adapter for semantic listings and explicit DOM file metadata.
// Verified provider schema (public desktop client, October 2026):
// https://img.imgsmail.ru/hb/cloud-common-static/cloud.mail.ru//hashes/react-39bbcb83bdfcefede3e4.bundle.js
// No provider bundle code, private responses or session data are embedded here.
// Unknown markup is never treated as an empty folder or displayed as page HTML.
public static class CloudScript
{
    public const string Snapshot = """
    (() => {
      const empty = {ready:false,authRequired:false,folderUrl:'',folderName:'',items:[],breadcrumbs:[],empty:false,partial:false,message:'Не удалось распознать список файлов.',loginUrl:''};
      const authHosts = new Set(['account.mail.ru','auth.mail.ru','oauth.mail.ru','id.mail.ru','id.vk.com','id.vk.ru','login.vk.com','login.vk.ru','oauth.vk.com','oauth.vk.ru']);
      const login = value => {try {const u=new URL(value,location.href);return u.protocol==='https:' && !u.username && !u.password && !u.port && authHosts.has(u.hostname)?u.href:'';}catch{return '';}};
      if (location.origin !== 'https://cloud.mail.ru') return {...empty,authRequired:!!login(location.href),loginUrl:login(location.href),message:login(location.href)?'Требуется официальный вход в Облако.':empty.message};
      const clean = value => (value || '').replace(/[\u200b-\u200d\ufeff]/g,'').replace(/\s+/g,' ').trim();
      const ui = value => {if(!value)return '';try {const u=new URL(value,location.href);return u.origin===location.origin && !u.username && !u.password && /^\/(?:home|trash|trashbin|recent|shared|starred|favorites|public)(?:\/|$)/.test(u.pathname)?u.origin+u.pathname:'';}catch{return '';}};
      const forbidden = '[role="dialog"],[aria-modal="true"],[data-qa-modal],[role="document"],iframe,form,[contenteditable="true"],[data-ad-slot],[data-mailtrim-cloud-promo],[data-mailtrim-ad],.viewer,.preview,[data-testid*="preview"]';
      const visible = node => {for(let p=node;p && p!==document;p=p.parentElement){if(p.hidden || p.getAttribute('aria-hidden')==='true')return false;const s=getComputedStyle(p);if(s.display==='none'||s.visibility==='hidden')return false;}return true;};
      const safe = node => !node.closest(forbidden) && visible(node);
      // Verified official Cloud desktop bundle (react-39bbcb83...): DataList emits
      // data-qa-datalist/storage; its rows expose data-qa-type/name/id. The visual
      // row href is "#", so canonical navigation is derived from the raw home ID.
      const rootSelector='[role="grid"],[role="table"],table,[data-testid="file-list"],[data-testid="files-list"],[data-view="files"],[data-qa-datalist="home"]';
      const roots = [...document.querySelectorAll(rootSelector)].filter(safe).filter(root=>
        !root.matches('table:not([role="table"])') || !!root.querySelector('[data-type="file"],[data-type="folder"],[data-kind="file"],[data-kind="folder"],[data-qa-type="file"],[data-qa-type="folder"]'));
      const loginLink = [...document.querySelectorAll('a[href]')].filter(a=>visible(a)&&/^(?:Войти|Войти в Облако|Вход|Sign in|Log in)$/iu.test(clean(a.textContent||a.getAttribute('aria-label')||a.title))).map(a=>login(a.href)).find(Boolean) || '';
      const password = [...document.querySelectorAll('input[type="password"]')].some(visible);
      if(password) return {...empty,authRequired:true,loginUrl:loginLink,message:'Требуется официальный вход в Облако.'};
      const folderUrl=ui(location.href);
      if(!folderUrl) return {...empty,authRequired:!!loginLink,loginUrl:loginLink,message:loginLink?'Требуется официальный вход в Облако.':'Открыта страница Облака, которая не является списком файлов.'};
      // Official HomePage only renders these welcome components when home root
      // isLoaded and its childs array has length zero (selector91553.fz).
      const homeRoot=/^\/home\/?$/.test(location.pathname);
      const welcome=[...document.querySelectorAll('[class*="WelcomeNewPromo__root--"],[class*="WelcomePromo__root--"]')]
        .some(node=>safe(node)&&!!node.closest('[class*="HomePage__promo--"]')&&!!node.closest('[class*="MainPage__root--"]'));
      if(!roots.length && !welcome && loginLink)return {...empty,authRequired:true,folderUrl,loginUrl:loginLink,message:'Требуется официальный вход в Облако.'};
      const items=[],seen=new Set();let skipped=0;
      for(const root of roots){
        const rows=[...root.querySelectorAll('[role="row"],tr,[data-type="file"],[data-type="folder"],[data-kind="file"],[data-kind="folder"],[data-qa-type="file"],[data-qa-type="folder"]')].filter(row=>safe(row) && row.closest(rootSelector)===root);
        for(const row of rows.slice(0,2001)){
          if(row.querySelector('[role="columnheader"],th') || row.matches('thead tr'))continue;
          const kind=clean(row.getAttribute('data-qa-type')||row.getAttribute('data-type')||row.getAttribute('data-kind')).toLowerCase();
          if(kind!=='file' && kind!=='folder'){skipped++;continue;}
          const nameCell=row.querySelector('[data-field="name"],[data-column="name"],[data-testid="file-name"]');
          const link=row.matches('a[href]')?row:row.querySelector('a[href]');
          const name=clean(row.getAttribute('data-qa-name')||row.getAttribute('data-name')||nameCell?.textContent||link?.getAttribute('title')||link?.textContent);
          const rawPath=row.getAttribute('data-path')||row.getAttribute('data-home')||'';
          // Metadata paths are object paths, not executable URLs. Prefer the actual official row link.
          const objectId=row.getAttribute('data-qa-id')||row.getAttribute('data-id')||'';
          let url=row.hasAttribute('data-qa-type') && objectId.startsWith('/') && !objectId.startsWith('//')
            ? ui('/home/'+encodeURIComponent(objectId)) : ui(link?.href || '');
          if(!url && rawPath.startsWith('/') && !rawPath.startsWith('//'))url=ui('/home'+rawPath);
          if(!name || !url || url===folderUrl || seen.has(url)){skipped++;continue;}
          seen.add(url);
          const size=clean(row.querySelector('[data-field="size"],[data-column="size"],[class*="Size__size--"],[class*="Item__size--"]')?.textContent||row.getAttribute('data-size'));
          const modified=clean(row.querySelector('[data-field="modified"],[data-column="modified"],[data-qa-id="date"],[class*="Date__root--"],time')?.textContent||row.getAttribute('data-modified'));
          items.push({name:name.slice(0,1000),url,isFolder:kind==='folder',size:size.slice(0,100),modified:modified.slice(0,150)});
          if(items.length>=2000)break;
        }
        if(items.length>=2000)break;
      }
      if(homeRoot && welcome && !items.length && !skipped)return {ready:true,authRequired:false,folderUrl,folderName:'Все файлы',items:[],breadcrumbs:[{name:'Все файлы',url:location.origin+'/home/'}],empty:true,partial:false,message:'',loginUrl:''};
      const breadcrumbs=[];
      for(const nav of document.querySelectorAll('nav[aria-label],nav ol,[aria-label="breadcrumb"],[data-testid="breadcrumbs"],[class*="Breadcrumbs__root--"]')){
        if(!safe(nav) || !/путь|breadcrumb|навигац/i.test(nav.getAttribute('aria-label')||'') && !nav.matches('[data-testid="breadcrumbs"],nav ol,[class*="Breadcrumbs__root--"]'))continue;
        for(const a of nav.querySelectorAll('a[href]')){const url=ui(a.href),name=clean(a.textContent);if(url&&name)breadcrumbs.push({name:name.slice(0,300),url});}
      }
      const heading=[...document.querySelectorAll('main h1,[role="main"] h1,h1')].find(safe);
      const activeCrumb=[...document.querySelectorAll('[class*="Breadcrumbs__item_active--"] [data-qa-name]')].find(safe);
      const folderName=clean(activeCrumb?.getAttribute('data-qa-name'))||clean(heading?.textContent)||breadcrumbs.at(-1)?.name||'Файлы';
      const emptyRoot=roots.some(root=>root.getAttribute('data-empty')==='true'||root.getAttribute('data-state')==='empty'
        || [...root.querySelectorAll('[role="status"],[data-testid="empty-state"],[class*="EmptyFolder__emptyFolderTitle--"]')].some(node=>safe(node)&&/^(?:В папке пусто|Папка пуста|Пустая папка|Нет файлов|Здесь пока нет файлов|В корзине пусто)[.!]?$/iu.test(clean(node.textContent))));
      const busy=roots.some(root=>root.getAttribute('aria-busy')==='true'||!!root.querySelector('[role="progressbar"]'));
      const ready=roots.length>0 && !busy && (items.length>0||emptyRoot&&skipped===0);
      // Cloud's own QA DataList is virtualized; its rendered rows do not prove a
      // complete folder. Semantic providers may give an explicit row count.
      const limited=items.length>0 && roots.some(root=>root.hasAttribute('data-qa-datalist')||Number(root.getAttribute('aria-rowcount'))>items.length||root.getAttribute('data-has-more')==='true');
      return {ready,authRequired:false,folderUrl,folderName,items,breadcrumbs:breadcrumbs.slice(0,40),empty:ready&&items.length===0&&emptyRoot,partial:limited||skipped>0||items.length>=2000,message:ready?'':busy?'Облако загружает список файлов.':empty.message,loginUrl:loginLink};
    })()
    """;

    // Explicit native "More" command only: move the verified rendered list,
    // allowing Cloud's own virtualized rows to load without clicking a file.
    public const string LoadMore = """
    (() => {
      if(location.origin!=='https://cloud.mail.ru')return {moved:false};
      const forbidden='[role="dialog"],[aria-modal="true"],[data-qa-modal],[role="document"],.viewer,.preview,form';
      const roots=[...document.querySelectorAll('[data-qa-datalist="home"],[role="grid"],[role="table"],[data-testid="file-list"],[data-view="files"]')].filter(node=>!node.closest(forbidden));
      for(const root of roots){
        const candidates=[...root.querySelectorAll('[class*="VirtualList-new__list--"]'),root];
        for(let ancestor=root.parentElement,depth=0;ancestor&&depth<6;ancestor=ancestor.parentElement,depth++){
          if(ancestor.matches('[class*="ContentLoader__content--"],[class*="MainPage__rightBlockContent--"]'))candidates.push(ancestor);
        }
        const box=candidates.find(node=>node.clientHeight>32&&node.scrollHeight>node.clientHeight+2);
        if(!box)continue;
        const before=box.scrollTop,maximum=box.scrollHeight-box.clientHeight;
        box.scrollTop=Math.min(maximum,before+Math.max(64,box.clientHeight*.8));
        // Hidden WebView controllers can defer native scroll events until paint.
        // Notify the site's verified virtual list without exposing it on screen.
        const moved=box.scrollTop>before+1;
        if(moved)box.dispatchEvent(new Event('scroll',{bubbles:true}));
        return {moved};
      }
      return {moved:false};
    })()
    """;
}
