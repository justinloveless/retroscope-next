// Pre-paint theme script. build.mjs minifies it into csp.json ({themeScript, scriptHashes}); the shell
// inlines that exact text in <head> after the #boot element, so the CSP hash matches. Keep it in step
// with resolveTheme() in theme.js.
(function () {
  var pref;
  try {
    pref = JSON.parse(document.getElementById('boot').textContent).theme;
  } catch (e) {}
  if (pref !== 'light' && pref !== 'dark' && pref !== 'system') {
    try {
      pref = localStorage.getItem('rs.theme');
    } catch (e) {}
  }
  if (pref !== 'light' && pref !== 'dark') {
    pref = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  document.documentElement.dataset.theme = pref;
})();
