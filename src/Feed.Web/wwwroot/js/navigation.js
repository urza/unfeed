// Fetch server-rendered HTML; ordinary links remain functional without JavaScript.
// Keep this limited to the feed and person pages, which have no management refresh timers.
export function navigation(enhance, cleanup) {
 const eligible = url => url.origin === location.origin && (url.pathname === '/' || /^\/people\/\d+$/.test(url.pathname));
 if (!eligible(new URL(location.href))) return;
 let controller, sequence = 0, navigating = false, scrollFrame;
 const state = y => ({...history.state, feedNavigation: true, feedScroll: y});
 history.replaceState(state(window.scrollY), '', location.href);
 history.scrollRestoration = 'manual';
 window.addEventListener('scroll', () => {
  if (navigating || scrollFrame) return;
  scrollFrame = requestAnimationFrame(() => { scrollFrame = null; if (!navigating) history.replaceState(state(window.scrollY), '', location.href); });
 }, {passive: true});
 async function visit(url, pop = false, scroll = 0) {
  const ticket = ++sequence; controller?.abort(); controller = new AbortController();
  const request = controller;
  navigating = true;
  document.querySelector('main.page')?.setAttribute('aria-busy', 'true');
  const timeout = setTimeout(() => request.abort(), 15000);
  try {
   const response = await fetch(url, {signal: request.signal, credentials: 'same-origin', cache: 'no-store'});
   if (!response.ok || !response.headers.get('content-type')?.includes('text/html')) throw new Error('Navigation failed');
   const finalUrl = new URL(response.url);
   const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
   if (ticket !== sequence) return;
   const fresh = doc.querySelector('main.page'), current = document.querySelector('main.page');
   // A deployment may have changed CSS/modules. Load it normally instead of mixing versions.
   if (!eligible(finalUrl) || !fresh || !current || doc.querySelector('link[rel=stylesheet]')?.getAttribute('href') !== document.querySelector('link[rel=stylesheet]')?.getAttribute('href')) throw new Error('Full navigation required');
   cleanup();
   current.replaceWith(fresh);
   document.title = doc.title;
   const background = doc.querySelector('.background');
   if (background) document.querySelector('.background')?.setAttribute('style', background.getAttribute('style') || '');
   if (!pop) history.pushState({feedNavigation: true, feedScroll: 0}, '', finalUrl);
   else if (finalUrl.href !== location.href) history.replaceState(state(scroll), '', finalUrl);
   enhance();
   fresh.tabIndex = -1; fresh.focus({preventScroll: true});
   window.scrollTo(0, pop ? scroll : 0);
   history.replaceState(state(window.scrollY), '', location.href);
  } catch {
   if (ticket === sequence) { if (pop) location.replace(url); else location.assign(url); }
  } finally {
   clearTimeout(timeout);
   if (ticket === sequence) { navigating = false; document.querySelector('main.page')?.removeAttribute('aria-busy'); }
  }
 }
 document.addEventListener('click', e => {
  const link = e.target.closest('a[href]');
  if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || !link || link.target || link.hasAttribute('download') || link.closest('[data-gallery]')) return;
  const url = new URL(link.href);
  if (!eligible(url) || url.hash) return;
  e.preventDefault();
  history.replaceState(state(window.scrollY), '', location.href);
  visit(url.href, url.href === location.href, window.scrollY);
 });
 window.addEventListener('popstate', e => {
  if (!eligible(new URL(location.href)) || !e.state?.feedNavigation) { location.reload(); return; }
  visit(location.href, true, e.state.feedScroll || 0);
 });
}
