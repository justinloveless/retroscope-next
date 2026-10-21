import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { themeScript } from '../../../web/build.mjs';
import { resolveTheme } from '../../../web/src/core/theme.js';

function runBoot(code, { boot, stored, systemDark = false, storageThrows = false }) {
  const html = { dataset: {} };
  const context = {
    document: {
      documentElement: html,
      getElementById: (id) => (id === 'boot' && boot !== undefined ? { textContent: boot } : null),
    },
    localStorage: {
      getItem: (key) => {
        if (storageThrows) {
          throw new Error('denied');
        }
        return key === 'rs.theme' ? stored ?? null : null;
      },
    },
    matchMedia: () => ({ matches: systemDark }),
  };
  runInNewContext(code, context);
  return html.dataset.theme;
}

const cases = [
  [{ boot: '{"theme":"dark"}', stored: 'light' }, 'dark'],
  [{ boot: '{"theme":"light"}', systemDark: true }, 'light'],
  [{ boot: '{"theme":"system"}', stored: 'light', systemDark: true }, 'dark'],
  [{ boot: '{"me":null}', stored: 'dark' }, 'dark'],
  [{ stored: 'light', systemDark: true }, 'light'],
  [{ stored: 'bogus', systemDark: true }, 'dark'],
  [{ boot: 'not json', storageThrows: true }, 'light'],
];

test('the pre-paint script resolves boot JSON, then storage, then the system scheme', async () => {
  const source = await readFile(new URL('../../../web/src/core/theme-boot.js', import.meta.url), 'utf8');
  const { text } = await themeScript();
  for (const [input, expected] of cases) {
    assert.equal(runBoot(source, input), expected, JSON.stringify(input));
    assert.equal(runBoot(text, input), expected, `minified: ${JSON.stringify(input)}`);
  }
});

test('resolveTheme agrees with the pre-paint script for explicit and system preferences', () => {
  const original = globalThis.matchMedia;
  try {
    globalThis.matchMedia = () => ({ matches: true });
    assert.equal(resolveTheme('system'), 'dark');
    assert.equal(resolveTheme('light'), 'light');
    globalThis.matchMedia = () => ({ matches: false });
    assert.equal(resolveTheme('system'), 'light');
    assert.equal(resolveTheme('dark'), 'dark');
  } finally {
    globalThis.matchMedia = original;
  }
});
