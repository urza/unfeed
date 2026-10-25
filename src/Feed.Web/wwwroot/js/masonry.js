export function masonry() {
 const dispose = [];
 for (const grid of document.querySelectorAll('[data-masonry]')) {
  const units = [...grid.children]; let count = 0, width = 0, timer, mediaChanged = false;
  function layout() {
   const currentWidth = grid.clientWidth, n = Math.max(1, Math.floor((currentWidth + 18) / 358));
   if (n === count && currentWidth === width && !mediaChanged) return;
   mediaChanged = false;
   // A re-layout keeps the reader's place. The first layout starts at the top, so it skips this
   // measurement: with 300 cards every forced layout costs tens of milliseconds.
   const top = count === 0 ? null : units.map(el => ({ el, rect: el.getBoundingClientRect() })).filter(x => x.rect.bottom > 100).sort((a,b) => a.rect.top-b.rect.top)[0];
   count = n; width = currentWidth;
   // Measure every unit at its final column width in one pass. The plain grid with n columns gives
   // them that width without moving a node; the columns are then filled while detached, so the
   // page is laid out exactly twice: once to measure, once to show. Never alternate height reads
   // with per-card DOM writes (that forced a layout for every card).
   if (grid.classList.contains('enhanced')) { grid.classList.remove('enhanced'); grid.replaceChildren(...units); }
   grid.style.gridTemplateColumns = `repeat(${n},minmax(0,1fr))`;
   const heights = units.map(unit => unit.getBoundingClientRect().height);
   const totals = Array(n).fill(0), columns = Array.from({length:n}, () => { const col = document.createElement('div'); col.className = 'feed-column'; return col; });
   units.forEach((unit, i) => {
    let shortest = 0;
    for (let c = 1; c < n; c++) if (totals[c] < totals[shortest]) shortest = c;
    columns[shortest].append(unit); totals[shortest] += heights[i] + 18;
   });
   grid.replaceChildren(...columns); grid.classList.add('enhanced');
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
