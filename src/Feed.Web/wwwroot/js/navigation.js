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
 const load = (url, signal) => fetch(url, {signal, credentials: 'same-origin', cache: 'no-store'}).then(async response => {
  if (!response.ok || !response.headers.get('content-type')?.includes('text/html')) throw new Error('Navigation failed');
  return {url: response.url, html: await response.text()};
 });
 // Pages fetched ahead of a click. A toolbar hover or a pointer press starts the request, so the
 // HTML is usually in hand when the click arrives. An entry is used once and expires quickly; any
 // form submission (thumbs, hearts, collect, mark read) drops them all, since the pages change.
 const ahead = new Map(), lifetime = 30000;
 const ready = url => { const entry = ahead.get(url); return entry && Date.now() - entry.at < lifetime ? entry : null; };
 function prefetch(url) {
  if (url === location.href || ready(url)) return;
  const control = new AbortController(); const timeout = setTimeout(() => control.abort(), 15000);
  const page = load(url, control.signal).finally(() => clearTimeout(timeout));
  page.catch(() => { if (ahead.get(url)?.page === page) ahead.delete(url); });
  ahead.set(url, {at: Date.now(), page});
 }
 document.addEventListener('submit', () => ahead.clear(), true);
 async function visit(url, pop = false, scroll = 0) {
  const ticket = ++sequence; controller?.abort(); controller = new AbortController();
  const request = controller;
  navigating = true;
  document.querySelector('main.page')?.setAttribute('aria-busy', 'true');
  const timeout = setTimeout(() => request.abort(), 15000);
  try {
   const fetched = ready(url); ahead.delete(url);
   const {url: responseUrl, html} = await (fetched ? fetched.page : load(url, request.signal));
   const finalUrl = new URL(responseUrl);
   const doc = new DOMParser().parseFromString(html, 'text/html');
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
 // The destination of a link the enhancement would handle, or null for every other link.
 function destination(target, modified = false) {
  const link = target.closest('a[href]');
  if (modified || !link || link.target || link.hasAttribute('download') || link.closest('[data-gallery]')) return null;
  const url = new URL(link.href);
  return eligible(url) && !url.hash ? {link, url} : null;
 }
 let hover;
 document.addEventListener('pointerover', e => {
  if (!e.target.closest('.toolbar')) return;
  const found = destination(e.target); if (!found) return;
  clearTimeout(hover); hover = setTimeout(() => prefetch(found.url.href), 80);
 });
 document.addEventListener('pointerout', e => { if (e.target.closest('a[href]')) clearTimeout(hover); });
 document.addEventListener('focusin', e => { const found = e.target.closest('.toolbar') && destination(e.target); if (found) prefetch(found.url.href); });
 document.addEventListener('pointerdown', e => { const found = destination(e.target, e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey); if (found) prefetch(found.url.href); });
 document.addEventListener('click', e => {
  const found = destination(e.target, e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey);
  if (!found) return;
  e.preventDefault();
  // Show the choice at once; the fetched page carries the authoritative state and replaces it.
  const group = found.link.closest('.pills, .inspection, .menu-inspection, .menu');
  if (group) {
   for (const other of group.querySelectorAll('a.active')) { other.classList.remove('active'); other.removeAttribute('aria-current'); }
   found.link.classList.add('active'); found.link.setAttribute('aria-current', 'page');
  }
  history.replaceState(state(window.scrollY), '', location.href);
  visit(found.url.href, found.url.href === location.href, window.scrollY);
 });
 window.addEventListener('popstate', e => {
  if (!eligible(new URL(location.href)) || !e.state?.feedNavigation) { location.reload(); return; }
  visit(location.href, true, e.state.feedScroll || 0);
 });
}
