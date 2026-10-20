import { test } from 'node:test';
import assert from 'node:assert/strict';
import { routes } from '../../../web/src/routes.js';

test('route paths are unique', () => {
  const paths = routes.map((r) => r.path);
  assert.equal(new Set(paths).size, paths.length);
});

test('every route lazily loads a screen module exposing mount()', async () => {
  for (const route of routes) {
    const screen = await route.load();
    assert.equal(typeof screen.mount, 'function', `${route.path} → ${route.module}`);
  }
});
