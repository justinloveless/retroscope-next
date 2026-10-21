// Serves a one-editor page from memory through page.route, so the editor specs need neither the
// built app nor the fake server. The page carries the production CSP: scripts from 'self' only and
// Trusted Types with no policies, so any HTML sink in the editor throws.

import { fileURLToPath } from 'node:url';
import { buildSync } from 'esbuild';
import { expect } from '@playwright/test';

const ORIGIN = 'https://editor.test';
const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; "
  + "require-trusted-types-for 'script'; trusted-types 'none'";

let bundle = null;

function harnessBundle() {
  bundle ??= buildSync({
    entryPoints: [fileURLToPath(new URL('./_harness-entry.js', import.meta.url))],
    bundle: true,
    format: 'iife',
    target: 'es2022',
    write: false,
  }).outputFiles[0].text;
  return bundle;
}

const PAGE = `<!doctype html><html><head><meta charset="utf-8"><title>editor</title>
<link rel="stylesheet" href="/editor.css"></head>
<body><main id="root"></main><script src="/harness.js"></script></body></html>`;

const STYLES = `.rs-editor { position: relative; font: 16px/1.5 sans-serif; }
.rs-editor-input { min-height: 3em; padding: 4px; border: 1px solid #888; }
.rs-editor-input p { margin: 0 0 .5em; }
.rs-editor-mentions { position: absolute; margin: 0; padding: 0; list-style: none; background: #fff; border: 1px solid #888; }
.rs-editor-mentions [aria-selected=true] { background: #ddf; }
.mention { color: #22c; }`;

/** Opens the page and mounts an editor; expectClean() then asserts no CSP violation happened. */
export async function openEditor(page, options = {}) {
  await page.route(`${ORIGIN}/**`, (route) => {
    const path = new URL(route.request().url()).pathname;
    const files = {
      '/': ['text/html', PAGE],
      '/harness.js': ['text/javascript', harnessBundle()],
      '/editor.css': ['text/css', STYLES],
    };
    const file = files[path];
    if (!file) {
      return route.fulfill({ status: 404, body: '' });
    }
    return route.fulfill({ status: 200, contentType: file[0], headers: { 'content-security-policy': CSP }, body: file[1] });
  });
  await page.goto(`${ORIGIN}/`);
  await page.evaluate(() => {
    window.violations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.violations.push(e.violatedDirective));
  });
  await page.evaluate((o) => window.mountEditor(o), options);
  await expect(page.locator('.rs-editor-input')).toBeFocused();
}

export const value = (page) => page.evaluate(() => window.editor.getValue());
export const selection = (page) => page.evaluate(() => window.editor.selection());
export const select = (page, start, end = start) => page.evaluate(([s, e]) => window.editor.select(s, e), [start, end]);

export async function expectValue(page, expected) {
  await expect.poll(() => value(page)).toBe(expected);
}

export async function expectClean(page) {
  expect(await page.evaluate(() => window.violations)).toEqual([]);
}
