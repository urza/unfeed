export function masonry() {
 const dispose = [];
 for (const grid of document.querySelectorAll('[data-masonry]')) {
  const units = [...grid.children]; let count = 0, width = 0, timer, mediaChanged = false;
  function layout() {
   const currentWidth = grid.clientWidth, n = Math.max(1, Math.floor((currentWidth + 18) / 358));
   if (n === count && currentWidth === width && !mediaChanged) return;
   mediaChanged = false;
   const top = units.map(el => ({ el, rect: el.getBoundingClientRect() })).filter(x => x.rect.bottom > 100).sort((a,b) => a.rect.top-b.rect.top)[0];
   count = n; width = currentWidth;
   const columns = Array.from({length:n}, () => { const col = document.createElement('div'); col.className = 'feed-column'; return col; });
   // Stage at the final column width, then read every height together. Never alternate
   // offsetHeight reads with per-card DOM writes (which forced a layout for every card).
   units.forEach((unit, i) => columns[i % n].append(unit));
   grid.replaceChildren(...columns); grid.style.gridTemplateColumns = `repeat(${n},minmax(0,1fr))`; grid.classList.add('enhanced');
   const heights = units.map(unit => unit.getBoundingClientRect().height);
   const totals = Array(n).fill(0), fragments = columns.map(() => document.createDocumentFragment());
   units.forEach((unit, i) => {
    let shortest = 0;
    for (let c = 1; c < n; c++) if (totals[c] < totals[shortest]) shortest = c;
    fragments[shortest].append(unit); totals[shortest] += heights[i] + 18;
   });
   columns.forEach((column, i) => column.replaceChildren(fragments[i]));
   if (top) window.scrollBy(0, top.el.getBoundingClientRect().top - top.rect.top);
  }
  const observer = new ResizeObserver(() => { clearTimeout(timer); timer = setTimeout(layout,80); }); observer.observe(grid);
  const onLoad = e => { if (e.target.matches('img') && (!e.target.hasAttribute('width') || !e.target.hasAttribute('height'))) { mediaChanged = true; clearTimeout(timer); timer = setTimeout(layout,80); } };
  grid.addEventListener('load', onLoad, true);
  layout();
  dispose.push(() => { observer.disconnect(); clearTimeout(timer); grid.removeEventListener('load', onLoad, true); });
 }
 return () => dispose.forEach(cleanup => cleanup());
}
