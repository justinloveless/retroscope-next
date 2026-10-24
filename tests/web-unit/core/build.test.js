import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { cp, mkdir, mkdtemp, readdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';
import * as esbuild from 'esbuild';
import { bannedSinksPlugin, budgetName, buildWeb, checkBudgets, findBannedSinks, measureChunks, rewriteVendor, scanBuiltScripts } from '../../../web/build.mjs';

const web = fileURLToPath(new URL('../../../web', import.meta.url));
const repo = fileURLToPath(new URL('../../..', import.meta.url));

let work;
let built;
let output;

// A copy of web/ with the repository's node_modules beside it, to plant files without touching web/.
async function copyWeb(name) {
  const copy = join(work, name);
  await cp(web, join(copy, 'web'), { recursive: true, filter: (src) => !src.includes(join(web, 'dist')) });
  await symlink(join(repo, 'node_modules'), join(copy, 'node_modules'), 'dir');
  return join(copy, 'web');
}

before(async () => {
  work = await mkdtemp(join(tmpdir(), 'rs-web-build-'));
  output = [];
  built = await buildWeb({ dist: join(work, 'dist'), log: (line) => output.push(line) });
});

after(async () => {
  await rm(work, { recursive: true, force: true });
});

test('every compressible file gets .br and .gz siblings that decompress to the original', async () => {
  const dist = join(work, 'dist');
  const js = Object.entries(built.manifest.files).filter(([path]) => path.endsWith('.js') || path.endsWith('.css'));
  assert.ok(js.length > 0);
  for (const [path, info] of js) {
    const raw = await readFile(join(dist, path));
    assert.equal(info.size, raw.length, path);
    assert.match(info.etag, /^"[\w-]{10}"$/);
    if (info.br !== null) {
      assert.deepEqual(brotliDecompressSync(await readFile(join(dist, `${path}.br`))), raw, `${path}.br`);
    }
    if (info.gz !== null) {
      assert.deepEqual(gunzipSync(await readFile(join(dist, `${path}.gz`))), raw, `${path}.gz`);
    }
  }
  const css = built.manifest.css[0];
  assert.ok(existsSync(join(dist, `${css}.br`)), 'kit css is precompressed');
  assert.ok(existsSync(join(dist, `${built.manifest.entry[0]}.gz`)), 'entry is precompressed');
});

test('manifest.json lists the entry, kit CSS and a chunk list for every route', async () => {
  const manifest = JSON.parse(await readFile(join(work, 'dist', 'manifest.json'), 'utf8'));
  assert.match(manifest.entry[0], /^\/assets\/entry-[A-Z0-9]+\.js$/);
  assert.match(manifest.css[0], /^\/assets\/kit-[A-Z0-9]+\.css$/);
  const { routes } = await import('../../../web/src/routes.js');
  for (const route of routes) {
    assert.ok(manifest.routes[route.path]?.length > 0, route.path);
    for (const file of manifest.routes[route.path]) {
      assert.ok(manifest.files[file], `${file} is listed in files`);
    }
  }
});

test('stylesheets are their own entries: kit for every page, screen styles per route', async () => {
  const manifest = built.manifest;
  assert.deepEqual(manifest.css, [manifest.styles['kit.css']]);
  assert.equal(manifest.styles['tokens.css'], undefined, 'partials are bundled into kit.css');
  for (const list of Object.values(manifest.routeCss)) {
    for (const file of list) {
      assert.ok(Object.values(manifest.styles).includes(file));
    }
  }
});

test('a stylesheet imported from a script fails the build', async () => {
  const copy = await copyWeb('css-import');
  await writeFile(join(copy, 'src', 'stray.css'), '.x{color:red}\n');
  const landing = join(copy, 'src', 'screens', 'landing.js');
  await writeFile(landing, `import '../stray.css';\n${await readFile(landing, 'utf8')}`);
  const { buildWeb: buildCopy } = await import(pathToFileURL(join(copy, 'build.mjs')).href);
  await assert.rejects(buildCopy({ log: () => {} }), /stylesheets imported from scripts/);
});

test('csp.json carries the inline theme script and its sha256', async () => {
  const csp = JSON.parse(await readFile(join(work, 'dist', 'csp.json'), 'utf8'));
  const digest = createHash('sha256').update(csp.themeScript).digest('base64');
  assert.deepEqual(csp.scriptHashes, [`sha256-${digest}`]);
  assert.match(csp.themeScript, /dataset\.theme/);
  assert.doesNotMatch(csp.themeScript, /\n/);
});

test('the build prints the chunk sizes against the budgets', () => {
  assert.ok(output.some((line) => line.includes('chunk sizes (brotli)') && line.includes('entry')));
  assert.ok(built.sizes.some((s) => s.name === 'css'));
  assert.ok(built.sizes.some((s) => s.name === 'poker'));
});

test('a chunk over budget warns; more than 10 % over fails the build', async () => {
  const entry = built.sizes.find((s) => s.name === 'entry').bytes;
  const warnFile = join(work, 'warn.json');
  await writeFile(warnFile, JSON.stringify({ chunks: { entry: Math.ceil(entry / 1.05) } }));
  const lines = [];
  const warned = await buildWeb({ dist: join(work, 'dist-warn'), budgetsFile: warnFile, log: (l) => lines.push(l) });
  assert.equal(warned.warnings.length, 1);
  assert.ok(lines.some((l) => l.startsWith('web: warning: over budget: entry')));

  const failFile = join(work, 'fail.json');
  await writeFile(failFile, JSON.stringify({ chunks: { entry: Math.floor(entry / 1.2) } }));
  await assert.rejects(
    buildWeb({ dist: join(work, 'dist-fail'), budgetsFile: failFile, log: () => {} }),
    /more than 10 % over the budgets\.json baseline:\n {2}entry: /,
  );
});

test('the root files are copied to dist and listed under public, the web manifest naming hashed icons', async () => {
  const dist = join(work, 'dist');
  const listed = built.manifest.public;
  assert.deepEqual(Object.keys(listed).sort(), ['/favicon.ico', '/manifest.webmanifest', '/robots.txt']);
  for (const [path, info] of Object.entries(listed)) {
    const raw = await readFile(join(dist, path));
    assert.equal(info.size, raw.length, path);
    assert.match(info.etag, /^"[\w-]{10}"$/);
  }
  assert.equal(listed['/favicon.ico'].br, null, 'an .ico is not precompressed');
  const webManifest = JSON.parse(await readFile(join(dist, 'manifest.webmanifest'), 'utf8'));
  assert.ok(webManifest.icons.length > 0);
  for (const icon of webManifest.icons) {
    assert.match(icon.src, /^\/assets\/static\//);
    assert.ok(built.manifest.files[icon.src], `${icon.src} is served`);
  }
  assert.ok(built.manifest.assets['logo.svg']);
  assert.ok(!Object.keys(built.manifest.assets).some((name) => name.startsWith('public/')), 'root files are not hashed assets');
});

test('measureChunks charges a lazy chunk only for what loading it adds', () => {
  const out = (entryPoint, statics = [], lazies = [], inputs = {}) => ({
    entryPoint,
    inputs,
    imports: [
      ...statics.map((path) => ({ path, kind: 'import-statement' })),
      ...lazies.map((path) => ({ path, kind: 'dynamic-import' })),
    ],
  });
  const outputs = {
    'entry.js': out('src/entry.js', ['core.js'], ['retro.js', 'team.js', 'mentions.js', 'admin.js', 'a.js', 'b.js']),
    'core.js': out(undefined),
    'retro.js': out('src/retro/index.js', ['parse.js', 'core.js'], ['editor.js']),
    'editor.js': out('src/markup/editor.js', ['parse.js']),
    'team.js': out('src/team/index.js', ['parse.js']),
    'parse.js': out(undefined, [], [], { 'src/markup/render.js': { bytesInOutput: 10 }, 'src/markup/parse.js': { bytesInOutput: 90 } }),
    'mentions.js': out('src/screens/mentions.js', ['frame.js']),
    'admin.js': out('src/screens/admin.js', ['frame.js']),
    'frame.js': out(undefined, [], [], { 'src/screens/frame.js': { bytesInOutput: 50 } }),
    'a.js': out('src/a.js', ['a-code.js']),
    'b.js': out('src/b.js', ['a-code.js']),
    'a-code.js': out(undefined, [], [], { 'src/a.js': { bytesInOutput: 40 } }),
  };
  const bytes = { 'entry.js': 1000, 'core.js': 500, 'retro.js': 300, 'editor.js': 200, 'team.js': 100, 'parse.js': 400, 'mentions.js': 60, 'admin.js': 70, 'frame.js': 80, 'a.js': 1, 'b.js': 2, 'a-code.js': 30 };

  const sizes = Object.fromEntries(measureChunks(outputs, 'entry.js', (f) => bytes[f]).map((s) => [s.name, s.bytes]));

  assert.deepEqual(sizes, {
    entry: 1500,
    retro: 300,
    editor: 200,
    team: 100,
    mentions: 60,
    admin: 70,
    a: 31,
    b: 2,
    'shared:parse': 400,
    'shared:frame': 80,
  });
});

test('checkBudgets thresholds', () => {
  const budgets = { a: 1000, b: 1000, c: 1000 };
  const result = checkBudgets([
    { name: 'a', bytes: 1000 },
    { name: 'b', bytes: 1100 },
    { name: 'c', bytes: 1101 },
    { name: 'new', bytes: 99999 },
  ], budgets);
  assert.deepEqual(result.warnings.map((w) => w.split(':')[0]), ['b']);
  assert.deepEqual(result.failures.map((w) => w.split(':')[0]), ['c']);
});

test('budget names follow the module: directory for index.js, the drawer alias', () => {
  assert.equal(budgetName('src/poker/index.js'), 'poker');
  assert.equal(budgetName('src/screens/landing.js'), 'landing');
  assert.equal(budgetName('src/poker/jira-browse.js'), 'jira-browse');
  assert.equal(budgetName('src/poker/issue-drawer.js'), 'jira-drawer');
  assert.equal(budgetName('src/markup/editor.js'), 'editor');
});

test('findBannedSinks reports each sink with its line', () => {
  const found = findBannedSinks('a\nel.innerHTML = x;\nel.outerHTML\nel.insertAdjacentHTML("x", y)\ndocument . write(z)\ndocument.writeln(1)\nel.innerHTMLish');
  assert.deepEqual(found, [
    { sink: 'innerHTML', line: 2 },
    { sink: 'outerHTML', line: 3 },
    { sink: 'insertAdjacentHTML', line: 4 },
    { sink: 'document.write', line: 5 },
    { sink: 'document.writeln', line: 6 },
  ]);
});

test('a planted innerHTML fails the bundle', async () => {
  const dir = join(work, 'planted');
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, 'entry.js'), "import { paint } from './paint.js';\npaint(document.body);\n");
  await writeFile(join(dir, 'paint.js'), "export function paint(el) {\n  el['x'] = 1;\n  el.innerHTML = '<b>hi</b>';\n}\n");
  await assert.rejects(
    esbuild.build({
      absWorkingDir: dir,
      entryPoints: ['entry.js'],
      bundle: true,
      write: false,
      logLevel: 'silent',
      plugins: [bannedSinksPlugin()],
    }),
    (error) => {
      assert.equal(error.errors.length, 1);
      assert.match(error.errors[0].text, /banned DOM sink "innerHTML"/);
      assert.equal(error.errors[0].location.line, 3);
      assert.match(error.errors[0].location.file, /paint\.js$/);
      return true;
    },
  );
});

test('node web/build.mjs exits non-zero when a source file uses a banned sink', async () => {
  const copy = await copyWeb('planted-cli');
  const planted = join(copy, 'src', 'screens', 'landing.js');
  await writeFile(planted, `${await readFile(planted, 'utf8')}\nexport function unsafe(el, html) {\n  el.insertAdjacentHTML('beforeend', html);\n}\n`);
  const run = spawnSync(process.execPath, [join(copy, 'build.mjs')], { encoding: 'utf8' });
  assert.equal(run.status, 1, run.stdout + run.stderr);
  assert.match(run.stderr, /banned DOM sink "insertAdjacentHTML"/);
  assert.equal(existsSync(join(copy, 'dist', 'manifest.json')), false);
});

// Scans files, not the bundle graph, so a module that no entry reaches yet is still covered.
test('no file under web/src uses a banned sink', async () => {
  const src = join(web, 'src');
  const files = (await readdir(src, { recursive: true, withFileTypes: true }))
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath ?? entry.path, entry.name));
  assert.ok(files.some((file) => file.endsWith(join('core', 'router.js'))));

  const found = [];
  for (const file of files) {
    for (const { sink, line } of findBannedSinks(await readFile(file, 'utf8'))) {
      found.push(`${relative(web, file)}:${line} ${sink}`);
    }
  }
  assert.deepEqual(found, []);
});

const PM_VIEW = join(repo, 'node_modules', 'prosemirror-view', 'dist', 'index.js');

// A bundle of one file planted as node_modules/prosemirror-view/dist/index.js, through the sink plugin.
async function bundlePlantedView(name, source) {
  const dir = join(work, name);
  await mkdir(join(dir, 'node_modules', 'prosemirror-view', 'dist'), { recursive: true });
  await writeFile(join(dir, 'node_modules', 'prosemirror-view', 'dist', 'index.js'), source);
  await writeFile(join(dir, 'node_modules', 'prosemirror-view', 'package.json'), '{"name":"prosemirror-view","type":"module","module":"dist/index.js"}\n');
  await writeFile(join(dir, 'entry.js'), "import * as view from 'prosemirror-view';\nconsole.log(view);\n");
  return esbuild.build({
    absWorkingDir: dir,
    entryPoints: ['entry.js'],
    bundle: true,
    write: false,
    logLevel: 'silent',
    external: ['prosemirror-model', 'prosemirror-state', 'prosemirror-transform'],
    plugins: [bannedSinksPlugin()],
  });
}

test('the reviewed prosemirror-view loses every banned sink and its HTML parser', async () => {
  const source = await readFile(PM_VIEW, 'utf8');
  assert.ok(findBannedSinks(source).length > 0, 'the published file has sinks to remove');
  const rewritten = rewriteVendor(PM_VIEW, source);
  assert.deepEqual(findBannedSinks(rewritten), []);
  assert.match(rewritten, /function readHTML\(html\) \{\n {4}throw new Error\("HTML clipboard parsing is disabled/);
  assert.doesNotMatch(rewritten, /=\s*maybeWrapTrusted\(/, 'nothing calls the Trusted Types wrapper');
});

test('a modified prosemirror-view fails the build until the pin is reviewed', async () => {
  const source = await readFile(PM_VIEW, 'utf8');
  await assert.rejects(bundlePlantedView('pm-modified', `${source}\n// one more line\n`), (error) => {
    assert.equal(error.errors.length, 1);
    assert.match(error.errors[0].text, /prosemirror-view\/dist\/index\.js is not the reviewed file \(sha256 [0-9a-f]{64}, pinned 3cc9270e/);
    return true;
  });
  await bundlePlantedView('pm-reviewed', source);
});

test('a rewrite that no longer matches exactly once fails instead of shipping the file unchanged', () => {
  const source = 'a\nb\na\n';
  const sha256 = createHash('sha256').update(source).digest('hex');
  const rewrites = (from) => ({ 'x/index.js': { sha256, edits: [{ from, with: '' }] } });
  assert.throws(() => rewriteVendor('/p/node_modules/x/index.js', source, rewrites('a\n')), /does not match exactly once/);
  assert.throws(() => rewriteVendor('/p/node_modules/x/index.js', source, rewrites('c\n')), /does not match exactly once/);
  assert.equal(rewriteVendor('/p/node_modules/x/index.js', source, rewrites('b\n')), 'a\na\n');
  assert.equal(rewriteVendor('/p/src/x/index.js', source, rewrites('b\n')), source, 'only dependency files are rewritten');
});

test('no built chunk holds a banned sink, and the rich editor chunk carries the rewritten view', async () => {
  const dist = join(work, 'dist');
  const scripts = Object.keys(built.manifest.files).filter((path) => path.endsWith('.js'));
  let rewritten = 0;
  for (const path of scripts) {
    const code = await readFile(join(dist, path), 'utf8');
    assert.deepEqual(findBannedSinks(code), [], path);
    assert.doesNotMatch(code, /innerHTML/, path);
    rewritten += code.includes('HTML clipboard parsing is disabled') ? 1 : 0;
  }
  assert.equal(rewritten, 1, 'prosemirror-view is bundled once, rewritten');
});

test('the rich editor is a lazy chunk with its own budgets, inside them', async () => {
  const budgets = JSON.parse(await readFile(join(web, 'budgets.json'), 'utf8')).chunks;
  for (const name of ['rich-editor', 'css:rich-editor']) {
    const size = built.sizes.find((s) => s.name === name);
    assert.ok(size, `${name} is measured`);
    assert.ok(budgets[name] > 0, `${name} has a budget`);
    assert.ok(size.bytes <= budgets[name], `${name}: ${size.bytes} B within ${budgets[name]} B`);
  }
  assert.ok(!built.manifest.entry.some((file) => file.includes('rich-editor')), 'not in the entry');
  const drawer = built.sizes.find((s) => s.name === 'jira-drawer');
  assert.ok(drawer.bytes < 22528, 'the drawer does not absorb the editor');
});

test('the sprint report is a lazy chunk with its own budget, inside it, loaded neither with the entry nor with the board', async () => {
  const budgets = JSON.parse(await readFile(join(web, 'budgets.json'), 'utf8')).chunks;
  const size = built.sizes.find((s) => s.name === 'sprint-report');
  assert.ok(size, 'sprint-report is measured');
  assert.ok(budgets['sprint-report'] > 0, 'sprint-report has a budget');
  assert.ok(size.bytes <= budgets['sprint-report'], `sprint-report: ${size.bytes} B within ${budgets['sprint-report']} B`);
  assert.ok(!built.manifest.entry.some((file) => file.includes('sprint-report')), 'not in the entry');
  assert.ok(!built.manifest.routes['/retro/:code'].some((file) => file.includes('sprint-report')), 'not loaded with the board');
});

test('a sink in a built script fails the output scan, which buildWeb runs on every emitted script', async () => {
  const dir = join(work, 'planted-output');
  await mkdir(join(dir, 'chunks'), { recursive: true });
  await writeFile(join(dir, 'chunks', 'ok.js'), 'export const a = 1;\n');
  await writeFile(join(dir, 'chunks', 'bad.js'), 'export function f(x, y) {\n  x.innerHTML = y;\n}\n');
  await writeFile(join(dir, 'chunks', 'style.css'), '.innerHTML {}\n');
  await scanBuiltScripts(dir, ['chunks/ok.js', 'chunks/style.css']);
  await assert.rejects(scanBuiltScripts(dir, ['chunks/ok.js', 'chunks/bad.js']), /banned DOM sink "innerHTML" in the built chunks\/bad\.js/);
  const source = await readFile(join(web, 'build.mjs'), 'utf8');
  assert.match(source, /await scanBuiltScripts\(root, Object\.keys\(outputs\)\);/);
});
