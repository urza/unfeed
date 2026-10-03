// Replace only the read-only status region; forms and expanded details stay untouched.
export function managementStatus() {
 if (!document.querySelector('[data-status-refresh]')) return;
 const reveal = () => document.querySelectorAll('.status-auto-note').forEach(note => note.hidden = false);
 reveal();
 let pending = false;
 setInterval(async () => {
  if (document.hidden || pending) return;
  pending = true;
  try {
   const response = await fetch(location.href, {cache: 'no-store', signal: AbortSignal.timeout(10000)});
   if (!response.ok) return;
   const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
   const fresh = doc.querySelector('[data-status-refresh]');
   const current = document.querySelector('[data-status-refresh]');
   if (fresh && current && !current.contains(document.activeElement)) { current.replaceWith(fresh); reveal(); }
  } catch { /* Keep the last successful timestamp visible when disconnected. */ }
  finally { pending = false; }
 }, 15000);
}
