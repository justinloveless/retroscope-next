// The web dependency pin (UX11), the npm side of DependencyRuleTests: every package the front end may ship or
// build with is listed here at an exact version. Adding, removing or bumping one fails this test, and the change
// needs the owner's agreement (tenet 2). A prosemirror-view bump also needs a review of web/build.mjs's rewrite.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const repo = fileURLToPath(new URL('../../../', import.meta.url));
const json = (path) => JSON.parse(readFileSync(`${repo}${path}`, 'utf8'));

// Bundled into the rich-editor chunk (the editor UX11 approved). All MIT, by Marijn Haverbeke and others.
const RUNTIME = {
  orderedmap: '2.1.1',
  'prosemirror-commands': '1.7.2',
  'prosemirror-dropcursor': '1.8.4',
  'prosemirror-gapcursor': '1.4.1',
  'prosemirror-history': '1.5.1',
  'prosemirror-inputrules': '1.5.1',
  'prosemirror-keymap': '1.2.3',
  'prosemirror-model': '1.25.12',
  'prosemirror-schema-list': '1.5.1',
  'prosemirror-state': '1.4.4',
  'prosemirror-tables': '1.8.5',
  'prosemirror-transform': '1.12.2',
  'prosemirror-view': '1.42.6',
  'rope-sequence': '1.3.4',
  'w3c-keyname': '2.2.8',
};
const WEB_DEV = { esbuild: '0.28.2' };
const ROOT_DEV = { '@playwright/test': '1.64.1', 'axe-core': '4.14.0' };
// Pulled in by the dev dependencies above; never bundled.
const TRANSITIVE_DEV = { playwright: '1.64.1', 'playwright-core': '1.64.1' };
const ESBUILD_PLATFORMS = /^@esbuild\/[a-z0-9-]+$/;

const EXACT = /^\d+\.\d+\.\d+$/;

test('web/package.json pins exactly the approved runtime and build dependencies', () => {
  const web = json('web/package.json');
  assert.deepEqual(web.dependencies, RUNTIME);
  assert.deepEqual(web.devDependencies, WEB_DEV);
  assert.equal(web.peerDependencies, undefined);
  assert.equal(web.optionalDependencies, undefined);
});

test('the root package.json has test tooling only', () => {
  const root = json('package.json');
  assert.equal(root.dependencies, undefined);
  assert.deepEqual(root.devDependencies, ROOT_DEV);
});

test('every pinned version is exact', () => {
  for (const [name, version] of Object.entries({ ...RUNTIME, ...WEB_DEV, ...ROOT_DEV })) {
    assert.match(version, EXACT, name);
  }
});

test('the lockfile holds no package outside the lists, and runtime packages at the pinned versions', () => {
  const { packages } = json('package-lock.json');
  const runtime = {};
  for (const [path, entry] of Object.entries(packages)) {
    if (path === '' || path === 'web' || entry.link) {
      continue;
    }
    assert.match(path, /^node_modules\/(@[^/]+\/)?[^/]+$/, `${path}: no nested copies`);
    const name = path.slice('node_modules/'.length);
    if (entry.dev) {
      const pinned = { ...WEB_DEV, ...ROOT_DEV, ...TRANSITIVE_DEV }[name] ?? (ESBUILD_PLATFORMS.test(name) ? WEB_DEV.esbuild : undefined);
      assert.equal(entry.version, pinned, `${name} is not an approved dev package`);
      continue;
    }
    assert.match(entry.resolved ?? '', /^https:\/\/registry\.npmjs\.org\//, `${name} comes from the npm registry`);
    assert.match(entry.integrity ?? '', /^sha512-/, `${name} has an integrity hash`);
    runtime[name] = entry.version;
  }
  assert.deepEqual(runtime, RUNTIME);
});
