import { managementStatus } from './management-status.js';
import { masonry } from './masonry.js';
import { gallery } from './gallery.js';
import { actions } from './actions.js';
import { navigation } from './navigation.js';
document.documentElement.classList.add('js');
let cleanup = () => {};
function enhance() {
 cleanup = masonry();
 for (const dropdown of document.querySelectorAll('.dropdown')) {
  dropdown.addEventListener('toggle', () => { if (dropdown.open) for (const other of document.querySelectorAll('.dropdown[open]')) if (other !== dropdown) other.open = false; });
 }
}
enhance(); const closeGallery = gallery(); actions();
navigation(enhance, () => { closeGallery(); cleanup(); });
document.addEventListener('click', e => { for (const menu of document.querySelectorAll('.dropdown[open]')) if (!menu.contains(e.target)) menu.open = false; });
document.addEventListener('keydown', e => { if (e.key === 'Escape') for (const menu of document.querySelectorAll('.dropdown[open]')) menu.open = false; });
managementStatus();
