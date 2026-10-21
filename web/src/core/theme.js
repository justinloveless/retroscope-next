// Theme preference: 'light' | 'dark' | 'system'. The resolved value sits on <html data-theme>, set
// before first paint by theme-boot.js and kept current here when the system scheme changes.

const KEY = 'rs.theme';
const PREFS = new Set(['light', 'dark', 'system']);
const darkQuery = () => globalThis.matchMedia?.('(prefers-color-scheme: dark)');

let preference = 'system';
let watching = null;

export function resolveTheme(pref) {
  if (pref === 'light' || pref === 'dark') {
    return pref;
  }
  return darkQuery()?.matches ? 'dark' : 'light';
}

export function storedTheme() {
  try {
    const value = localStorage.getItem(KEY);
    return PREFS.has(value) ? value : null;
  } catch {
    return null;
  }
}

function onSystemChange() {
  if (preference === 'system') {
    document.documentElement.dataset.theme = resolveTheme('system');
  }
}

export function applyTheme(pref) {
  preference = PREFS.has(pref) ? pref : 'system';
  document.documentElement.dataset.theme = resolveTheme(preference);
  try {
    localStorage.setItem(KEY, preference);
  } catch {
    // Without storage the preference still applies to this page.
  }
  if (!watching) {
    watching = darkQuery();
    watching?.addEventListener('change', onSystemChange);
  }
}

export function currentTheme() {
  return preference;
}
