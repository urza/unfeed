export function actions() {
 const saved = sessionStorage.getItem('feed-scroll'); if (saved) { sessionStorage.removeItem('feed-scroll'); requestAnimationFrame(() => window.scrollTo(0, Number(saved))); }
 const reload = () => { sessionStorage.setItem('feed-scroll',String(window.scrollY)); location.reload(); };
 document.addEventListener('submit', async e => {
  const form = e.target; for(const menu of document.querySelectorAll('.dropdown[open]'))menu.open=false;
  if(!form.dataset.action)return;
  e.preventDefault(); const button=form.querySelector('button');button.disabled=true;
  try {
   const response=await fetch(form.action,{method:'POST',body:new FormData(form),credentials:'same-origin'});
   if(!response.ok){reload();return;}
   if(form.dataset.action==='thumb'){const count=button.querySelector('[data-count]');count.textContent=String((Number(count.textContent)||0)+1);button.classList.add('voted');button.disabled=false;}
   else if(form.dataset.action==='unhide')reload();
   else {
    button.dataset.state='pending';button.title='Sending the heart';const end=Date.now()+90000;
    while(Date.now()<end){await new Promise(r=>setTimeout(r,3000));try{const res=await fetch(form.action,{credentials:'same-origin'});if(!res.ok)throw new Error();const state=await res.json();button.dataset.state=state.state;if(state.state==='sent'){button.textContent='❤️';button.title='Heart sent';break;}if(state.state==='failed'){button.disabled=false;button.title=state.error||'Heart failed';break;}}catch{await new Promise(r=>setTimeout(r,5000));}}
   }
  }catch{button.disabled=false;button.title='Request failed; try again';}
 });
}
