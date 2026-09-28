export function masonry() {
 for (const grid of document.querySelectorAll('[data-masonry]')) {
  const units = [...grid.children]; let columns = [], count = 0, width = 0, timer, mediaChanged = false;
  function anchor() { return units.map(el => ({ el, rect: el.getBoundingClientRect() })).filter(x => x.rect.bottom > 100).sort((a,b) => a.rect.top-b.rect.top)[0]; }
  function layout() {
   const currentWidth = grid.clientWidth, n = Math.max(1, Math.floor((currentWidth + 18) / 358));
   if (n === count && currentWidth === width && !mediaChanged) return;
   mediaChanged = false;
   const top = anchor(); count = n; width = currentWidth;
   columns = Array.from({length:n}, () => { const col = document.createElement('div'); col.className = 'feed-column'; return col; });
   grid.replaceChildren(...columns); grid.style.gridTemplateColumns = `repeat(${n},minmax(0,1fr))`; grid.classList.add('enhanced');
   for (const unit of units) columns.reduce((a,b) => a.offsetHeight <= b.offsetHeight ? a : b).append(unit);
   if (top) window.scrollBy(0, top.el.getBoundingClientRect().top - top.rect.top);
  }
  new ResizeObserver(() => { clearTimeout(timer); timer = setTimeout(layout,80); }).observe(grid);
  grid.addEventListener('load', e => { if (e.target.matches('img') && (!e.target.hasAttribute('width') || !e.target.hasAttribute('height'))) { mediaChanged = true; clearTimeout(timer); timer = setTimeout(layout,80); } }, true);
  layout();
  // Expansion keeps a unit in its own column. Unsized images use scroll anchoring in the existing columns.
 }
}
