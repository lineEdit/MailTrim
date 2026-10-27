namespace MailTrim.Core;

// Runs in CosmeticScript's closure, after the synchronous cleaner is available.
public static class RenderGateScript
{
    public const string Code = """
    const renderGate = (() => {
      const doc = String(performance.timeOrigin)+'-'+Math.random().toString(36).slice(2);
      let revision=0, pending=false, disposed=false, timer, check, deadline, started=0, resumeAfter=0, quietUntil=0, probeAt=0, probeShape='';
      const sensitive = () => [...document.querySelectorAll('input[type="password"],input[autocomplete="one-time-code"],iframe[src*="id.vk."],iframe[src*="login.vk."],iframe[src*="auth.mail.ru"],iframe[src*="account.mail.ru"],.compose,.compose-app,[contenteditable="true"],textarea')].some(e=>{
        const b=e.getBoundingClientRect();return b.width>0 && b.height>0 && b.right>0 && b.bottom>0 && b.left<innerWidth && b.top<innerHeight;
      });
      const state = () => { progress(); return {version:1,doc,revision,ready:!pending && document.readyState!=='loading',sensitive:sensitive()}; };
      window.__mailtrimPresentation = state;
      const geometry = () => [...document.querySelectorAll('.letter-list__react,.llct,.letter-list,.thread,.compose-app')].slice(0,8)
        .map(e=>{const b=e.getBoundingClientRect();return [b.x,b.y,b.width,b.height].map(Math.round).join(',');}).join(';');
      const commit = bounded => {
        clearTimeout(timer);clearTimeout(check);clearTimeout(deadline);
        pending=false;if(bounded)resumeAfter=performance.now()+160;
        document.documentElement?.removeAttribute('data-mailtrim-render-pending');
        if(initialGuard){document.documentElement?.removeAttribute('data-mailtrim-pending');clearTimeout(revealTimeout);cancelAnimationFrame(revealFrame);}
      };
      // Hidden browser timers can be throttled. Host polling advances preparation
      // against a monotonic clock as well, without waiting for a hidden animation frame.
      const progress = () => {
        if(!pending || disposed || document.readyState==='loading')return;
        const now=performance.now();
        if(now-started>=2400){scan();commit(true);return;}
        if(now<quietUntil)return;
        if(!probeAt){scan();probeShape=geometry();probeAt=now+60;return;}
        if(now<probeAt)return;
        scan();const shape=geometry();
        if(shape===probeShape)commit(false);else {probeShape=shape;probeAt=now+60;}
      };
      const settle = () => {
        if(disposed)return;
        if(document.readyState==='loading'){timer=setTimeout(settle,100);return;}
        scan(); const before=geometry();
        check=setTimeout(()=>{
          if(disposed)return;
          scan();
          if(before!==geometry() && performance.now()-started<2400){timer=setTimeout(settle,120);return;}
          commit(false);
        },60);
      };
      const begin = (explicit=true) => {
        if(!compactLayout || disposed)return;
        if(!explicit && performance.now()<resumeAfter)return;
        if(!pending){
          revision++;started=performance.now();pending=true;
          const publishCurrent=()=>{
            if(disposed)return;
            if(document.readyState==='loading'){deadline=setTimeout(publishCurrent,100);return;}
            // A busy page must not postpone publication forever. Always clean the
            // current DOM before this bounded commit, then allow one host polling turn.
            try{scan();commit(true);}catch{}
          };
          deadline=setTimeout(publishCurrent,2400);
        }
        document.documentElement?.setAttribute('data-mailtrim-render-pending','true');
        quietUntil=performance.now()+180;probeAt=0;
        clearTimeout(timer);clearTimeout(check);
        timer=setTimeout(settle,performance.now()-started>=2400?0:180);
      };
      const important = '.letter-list__react,.llct,.letter-list,.thread,[role="dialog"],[aria-modal="true"],.js-banner-wrapper-container,[data-ad-slot],[data-mailtrim-top-ad]';
      const transition = raw => {
        const node=raw?.nodeType===1?raw:raw?.parentElement;
        if(!node || node.closest(protectedContent) || node.closest('#'+id)
            || node.closest('[data-mailtrim-ad],[data-mailtrim-top-ad],[data-mailtrim-promo]'))return false;
        if(node.matches(important) || node.querySelector(important) || node.closest('[role="dialog"],[aria-modal="true"]'))return true;
        for(let e=node,n=0;e && n<5 && !e.matches('body,html');e=e.parentElement,n++){
          const b=e.getBoundingClientRect();
          if(getComputedStyle(e).position==='fixed' && b.width>=180 && b.height>=100)return true;
        }
        return false;
      };
      const mutations = records => {
        if(records.some(r=>r.type==='childList'
          ? [...r.addedNodes].some(transition) || (r.removedNodes.length && transition(r.target))
          : transition(r.target)))begin(false);
      };
      const click = event => {
        const node=event.target instanceof Element?event.target:null;
        if(!node || node.closest(protectedContent))return;
        const link=node.closest('a[href]');
        if(link){try{const u=new URL(link.href,location.href);if(u.origin===location.origin && !u.pathname.startsWith('/api-proxy/'))begin();}catch{}}
      };
      const wrappers=[];
      if(compactLayout){
        for(const name of ['pushState','replaceState']){
          const original=history[name];
          const wrapper=function(...args){begin();return original.apply(this,args);};
          history[name]=wrapper;wrappers.push({name,original,wrapper});
        }
        document.addEventListener('click',click,true);
        window.addEventListener('popstate',begin);window.addEventListener('hashchange',begin);
      }
      const dispose=()=>{
        disposed=true;clearTimeout(timer);clearTimeout(check);clearTimeout(deadline);
        document.removeEventListener('click',click,true);
        window.removeEventListener('popstate',begin);window.removeEventListener('hashchange',begin);
        for(const w of wrappers)if(history[w.name]===w.wrapper)history[w.name]=w.original;
        if(window.__mailtrimPresentation===state)delete window.__mailtrimPresentation;
        document.documentElement?.removeAttribute('data-mailtrim-render-pending');
      };
      return {begin,mutations,dispose};
    })();
    """;
}
