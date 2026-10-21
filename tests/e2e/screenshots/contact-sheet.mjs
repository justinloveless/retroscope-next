// Writes artifacts/screenshots/index.html: one row per route and state, light and dark side by side,
// desktop then iPhone. Usage: node contact-sheet.mjs <screenshots dir>
import { readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
const NAME = /^(.+)-(light|dark)-(desktop|mobile)\.png$/;
const rows = new Map();
for (const file of readdirSync(dir).sort()) {
  const match = NAME.exec(file);
  if (match) {
    const [, key, theme, viewport] = match;
    if (!rows.has(key)) {
      rows.set(key, {});
    }
    rows.get(key)[`${theme}-${viewport}`] = file;
  }
}

const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
const cell = (file, label) => (file
  ? `<figure><a href="${escape(file)}"><img src="${escape(file)}" alt="${escape(label)}" loading="lazy"></a><figcaption>${escape(label)}</figcaption></figure>`
  : '<figure class="missing"><figcaption>not captured</figcaption></figure>');

const sections = [...rows].map(([key, shots]) => `
<section id="${escape(key)}">
  <h2>${escape(key)}</h2>
  <div class="row">
    ${['light-desktop', 'dark-desktop', 'light-mobile', 'dark-mobile'].map((k) => cell(shots[k], k.replace('-', ' · '))).join('\n    ')}
  </div>
</section>`).join('\n');

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Retroscope screenshots</title>
<style>
  body { font: 14px/1.4 system-ui, sans-serif; margin: 0; padding: 1.5rem; background: #f4f4f6; color: #1d1d22; }
  h1 { margin: 0 0 0.25rem; }
  nav { columns: 4 14rem; margin: 1rem 0 2rem; }
  nav a { display: block; color: #4338ca; }
  section { background: #fff; border-radius: 10px; padding: 1rem; margin-bottom: 1.25rem; box-shadow: 0 1px 2px #0001; }
  h2 { font-size: 1rem; margin: 0 0 0.75rem; font-family: ui-monospace, monospace; }
  .row { display: grid; grid-template-columns: 3fr 3fr 1fr 1fr; gap: 0.75rem; align-items: start; }
  figure { margin: 0; }
  img { width: 100%; border: 1px solid #ddd; border-radius: 6px; display: block; }
  figcaption { color: #666; font-size: 12px; margin-top: 0.25rem; }
  .missing { border: 1px dashed #ccc; border-radius: 6px; min-height: 4rem; display: grid; place-items: center; }
</style>
</head>
<body>
<h1>Retroscope screenshots</h1>
<p>${rows.size} routes and states, captured ${escape(new Date().toISOString())} from the demo preview.</p>
<nav>${[...rows.keys()].map((key) => `<a href="#${escape(key)}">${escape(key)}</a>`).join('')}</nav>
${sections}
</body>
</html>
`;
writeFileSync(join(dir, 'index.html'), html);
console.log(`contact sheet: ${join(dir, 'index.html')} (${rows.size} rows)`);
