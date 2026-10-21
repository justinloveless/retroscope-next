// The HTML shell, built the way U10's decision 3 tells the real one (Server/Hosting/Shell, U11) to:
// kit and route stylesheets, modulepreloads for the entry and the route chunk, the boot JSON with `<`
// escaped, the theme script inlined byte for byte after #boot, then the entry module.
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { match } from '../../web/src/core/router.js';
import { routes } from '../../web/src/routes.js';

export async function loadDist(dist) {
  let manifest;
  let csp;
  try {
    manifest = JSON.parse(await readFile(join(dist, 'manifest.json'), 'utf8'));
    csp = JSON.parse(await readFile(join(dist, 'csp.json'), 'utf8'));
  } catch (error) {
    throw new Error(`web-fake-server: ${dist} has no manifest.json/csp.json; run ./build.sh web first (${error.message})`);
  }
  return { dir: dist, manifest, csp };
}

export function shellRoute(pathname) {
  return routes.find((route) => match(route.path, pathname) !== null)?.path ?? null;
}

export function bootJson(boot) {
  return JSON.stringify(boot).replace(/</g, '\\u003c');
}

const escapeAttr = (value) => value.replace(/&/g, '&amp;').replace(/"/g, '&quot;');

// The same head links Server/Hosting/Shell writes, each only when the build ships its file.
const THEME_COLOR = '#111827';

function iconLinks(manifest) {
  const has = (path) => Object.hasOwn(manifest.public ?? {}, path);
  const logo = manifest.assets?.['logo.svg'];
  return [
    has('/favicon.ico') ? '<link rel="icon" href="/favicon.ico" sizes="48x48">' : null,
    logo ? `<link rel="icon" href="${escapeAttr(logo)}" type="image/svg+xml">` : null,
    has('/manifest.webmanifest') ? '<link rel="manifest" href="/manifest.webmanifest">' : null,
  ].filter(Boolean);
}

export function renderShell({ manifest, csp }, route, boot) {
  const [entry, ...entryImports] = manifest.entry;
  const preload = [...new Set([...entryImports, ...(manifest.routes[route] ?? [])])].filter((f) => f !== entry);
  const styles = [...manifest.css, ...(manifest.routeCss[route] ?? [])];
  return [
    '<!doctype html>',
    '<html lang="en">',
    '<head>',
    '<meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    `<meta name="theme-color" content="${THEME_COLOR}">`,
    '<title>Retroscope</title>',
    ...iconLinks(manifest),
    ...styles.map((href) => `<link rel="stylesheet" href="${escapeAttr(href)}">`),
    ...preload.map((href) => `<link rel="modulepreload" href="${escapeAttr(href)}">`),
    `<script type="application/json" id="boot">${bootJson(boot)}</script>`,
    `<script>${csp.themeScript}</script>`,
    `<script type="module" src="${escapeAttr(entry)}"></script>`,
    '</head>',
    '<body>',
    '<div id="app"></div>',
    '</body>',
    '</html>',
    '',
  ].join('\n');
}

// The §e header set; connect-src names the fake's own ws:// origin because it serves plain HTTP.
export function securityHeaders({ csp }, host) {
  const scripts = ["'self'", ...csp.scriptHashes.map((h) => `'${h}'`)].join(' ');
  return {
    'Content-Security-Policy': [
      "default-src 'none'",
      `script-src ${scripts}`,
      "style-src 'self'",
      "img-src 'self' data: blob:",
      "font-src 'self'",
      `connect-src 'self' ws://${host}`,
      "media-src 'self'",
      "manifest-src 'self'",
      "form-action 'self'",
      "frame-ancestors 'none'",
      "base-uri 'none'",
      "object-src 'none'",
      "require-trusted-types-for 'script'",
      "trusted-types 'none'",
    ].join('; '),
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'same-origin',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
    'Cross-Origin-Opener-Policy': 'same-origin',
  };
}
