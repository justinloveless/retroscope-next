// Front-end build (DESIGN §g.7): esbuild bundle with route chunks, content-hashed assets, brotli and
// gzip siblings, manifest.json, csp.json (theme-script hash), size budgets and the banned-sink check.
// Runs with nothing but Node built-ins and esbuild, because the image's web stage installs only those.
import { createHash } from 'node:crypto';
import { realpathSync } from 'node:fs';
import { copyFile, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, extname, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { brotliCompress, constants as zlib, gzip } from 'node:zlib';
import * as esbuild from 'esbuild';

const root = dirname(fileURLToPath(import.meta.url));
const brotli = promisify(brotliCompress);
const gz = promisify(gzip);

const COMPRESSIBLE = new Set(['.js', '.css', '.json', '.svg', '.html', '.txt', '.map', '.webmanifest']);
const ASSET_LOADERS = { '.png': 'file', '.webp': 'file', '.svg': 'file', '.woff2': 'file', '.mp3': 'file' };
const FAIL_OVER_BUDGET = 1.10;

// Stylesheets are entry points of their own (esbuild folds CSS imported from lazy chunks into the
// entry's CSS, which would put every screen's styles on every page). kit.css @imports the partials.
const STYLE_PARTIALS = new Set(['tokens.css', 'base.css']);
const ROUTE_STYLES = {
  'poker/index.js': ['neotro.css', 'jira.css'],
  'retro/index.js': ['retro.css'],
  'team/index.js': ['team.css'],
};

// A dynamic-import chunk takes its budget name from its module: the directory for index.js, otherwise
// the file name. §g.7 names the issue-drawer chunk after what it holds.
const BUDGET_ALIASES = { 'issue-drawer': 'jira-drawer' };

const BANNED_SINKS = /\b(?:innerHTML|outerHTML|insertAdjacentHTML)\b|\bdocument\s*\.\s*write(?:ln)?\b/g;

export function findBannedSinks(source) {
  const found = [];
  for (const m of source.matchAll(BANNED_SINKS)) {
    const line = source.slice(0, m.index).split('\n').length;
    found.push({ sink: m[0].replace(/\s+/g, ''), line });
  }
  return found;
}

// Fails the build on any banned DOM sink in a bundled script (§g.3); Trusted Types blocks them at
// runtime too, but the build catches them before a browser does.
export function bannedSinksPlugin() {
  return {
    name: 'banned-sinks',
    setup(build) {
      build.onLoad({ filter: /\.[cm]?js$/ }, async (args) => {
        const contents = await readFile(args.path, 'utf8');
        const found = findBannedSinks(contents);
        if (found.length > 0) {
          return {
            errors: found.map(({ sink, line }) => ({
              text: `banned DOM sink "${sink}" (use core/h.js and textContent)`,
              location: { file: args.path, line, column: 0 },
            })),
          };
        }
        return { contents, loader: 'js' };
      });
    },
  };
}

function shortHash(bytes) {
  return createHash('sha256').update(bytes).digest('base64url').slice(0, 10);
}

async function listFiles(dir) {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true, recursive: true });
  } catch (error) {
    if (error.code === 'ENOENT') {
      return [];
    }
    throw error;
  }
  return entries.filter((e) => e.isFile()).map((e) => join(e.parentPath ?? e.path, e.name)).sort();
}

// web/assets/public/* is served from the site root under fixed names (favicon.ico, robots.txt, …).
const PUBLIC_DIR = 'public';

// Copies web/assets/** to dist/assets/ under content-hashed names; returns logical name → public URL.
async function copyStaticAssets(dist) {
  const map = {};
  const source = join(root, 'assets');
  for (const file of await listFiles(source)) {
    const name = relative(source, file).split(sep).join('/');
    if (name.startsWith(`${PUBLIC_DIR}/`)) {
      continue;
    }
    const ext = extname(name);
    const hashed = `${name.slice(0, -ext.length)}-${shortHash(await readFile(file))}${ext}`;
    const target = join(dist, 'assets', 'static', hashed);
    await mkdir(dirname(target), { recursive: true });
    await copyFile(file, target);
    map[name] = `/assets/static/${hashed}`;
  }
  return map;
}

// Copies web/assets/public/* to the root of dist. The web manifest names its icons by logical asset
// name; they are rewritten to the hashed URLs. Returns the copied files.
async function copyPublicFiles(dist, staticAssets) {
  const source = join(root, 'assets', PUBLIC_DIR);
  const copied = [];
  for (const file of await listFiles(source)) {
    const name = relative(source, file).split(sep).join('/');
    const target = join(dist, name);
    if (name === 'manifest.webmanifest') {
      const manifest = JSON.parse(await readFile(file, 'utf8'));
      manifest.icons = (manifest.icons ?? []).map((icon) => {
        if (!staticAssets[icon.src]) {
          throw new Error(`manifest.webmanifest names an icon that is not in web/assets: ${icon.src}`);
        }
        return { ...icon, src: staticAssets[icon.src] };
      });
      await writeFile(target, JSON.stringify(manifest) + '\n');
    } else {
      await copyFile(file, target);
    }
    copied.push(target);
  }
  return copied;
}

// The pre-paint theme script, minified exactly as the shell will inline it, and its CSP source.
export async function themeScript() {
  const source = await readFile(join(root, 'src/core/theme-boot.js'), 'utf8');
  const { code } = await esbuild.transform(source, { minify: true, target: 'es2017', legalComments: 'none' });
  const text = code.trim();
  return { text, hash: `sha256-${createHash('sha256').update(text).digest('base64')}` };
}

async function precompress(file) {
  const bytes = await readFile(file);
  const entry = { size: bytes.length, etag: `"${shortHash(bytes)}"`, br: null, gz: null };
  if (!COMPRESSIBLE.has(extname(file))) {
    return entry;
  }
  const [br, gzipped] = await Promise.all([
    brotli(bytes, {
      params: {
        [zlib.BROTLI_PARAM_QUALITY]: 11,
        [zlib.BROTLI_PARAM_MODE]: zlib.BROTLI_MODE_TEXT,
        [zlib.BROTLI_PARAM_SIZE_HINT]: bytes.length,
      },
    }),
    gz(bytes, { level: 9 }),
  ]);
  if (br.length < bytes.length) {
    await writeFile(`${file}.br`, br);
    entry.br = br.length;
  }
  if (gzipped.length < bytes.length) {
    await writeFile(`${file}.gz`, gzipped);
    entry.gz = gzipped.length;
  }
  return entry;
}

export function budgetName(entryPoint) {
  const file = basename(entryPoint, extname(entryPoint));
  const name = file === 'index' ? basename(dirname(entryPoint)) : file;
  return BUDGET_ALIASES[name] ?? name;
}

/**
 * sizes: [{name, bytes}] (brotli). budgets: {name: bytes}. Over budget warns; more than 10 % over
 * fails. Chunks without a baseline are reported but not judged.
 */
export function checkBudgets(sizes, budgets) {
  const warnings = [];
  const failures = [];
  for (const { name, bytes } of sizes) {
    const budget = budgets[name];
    if (budget === undefined || bytes <= budget) {
      continue;
    }
    const over = `${name}: ${bytes} B brotli, budget ${budget} B (+${(((bytes / budget) - 1) * 100).toFixed(1)} %)`;
    if (bytes > budget * FAIL_OVER_BUDGET) {
      failures.push(over);
    } else {
      warnings.push(over);
    }
  }
  return { warnings, failures };
}

/**
 * Brotli bytes per chunk from esbuild's metafile outputs. `entry` is the entry chunk with its static
 * imports. A lazily imported chunk counts only what loading it adds: its static closure minus what is
 * already loaded wherever it can be imported from (the entry, and the closure of every importer), and
 * minus chunks that other lazy chunks share. Shared chunks are reported once each as `shared:<module>`,
 * named after their largest input, and have no baseline.
 */
export function measureChunks(outputs, entryFile, brotliOf) {
  const closures = new Map();
  function closure(file) {
    let seen = closures.get(file);
    if (!seen) {
      seen = new Set([file]);
      closures.set(file, seen);
      for (const imported of outputs[file].imports) {
        if (imported.kind === 'import-statement' && !imported.external) {
          for (const f of closure(imported.path)) {
            seen.add(f);
          }
        }
      }
    }
    return seen;
  }

  const lazy = Object.keys(outputs).filter((f) => outputs[f].entryPoint && f !== entryFile && f.endsWith('.js'));
  const entryFiles = closure(entryFile);
  const intersect = (sets) => (sets.length === 0
    ? new Set(entryFiles)
    : new Set([...sets[0]].filter((f) => sets.every((s) => s.has(f)))));

  // What is certainly loaded once `file` has run. A lazy chunk still being worked out (null) is not
  // loaded yet, so a shared chunk it brings in does not count as loaded before it.
  const loaded = new Map();
  function available(file) {
    if (entryFiles.has(file)) {
      return entryFiles;
    }
    if (loaded.has(file)) {
      return loaded.get(file) ?? entryFiles;
    }
    let result;
    if (outputs[file].entryPoint) {
      loaded.set(file, null);
      result = new Set([...closure(file), ...before(file)]);
    } else {
      const carriers = lazy.filter((e) => loaded.get(e) !== null && closure(e).has(file));
      result = new Set([...closure(file), ...intersect(carriers.map(available))]);
    }
    loaded.set(file, result);
    return result;
  }
  function before(file) {
    const importers = Object.keys(outputs)
      .filter((f) => outputs[f].imports.some((i) => i.kind === 'dynamic-import' && i.path === file));
    return intersect(importers.map(available));
  }

  const added = new Map(lazy.map((file) => {
    const ready = before(file);
    return [file, [...closure(file)].filter((f) => !ready.has(f))];
  }));
  const users = new Map();
  for (const files of added.values()) {
    for (const f of files) {
      users.set(f, (users.get(f) ?? 0) + 1);
    }
  }
  // A chunk belongs to the lazy chunk whose output it is, or whose entry module esbuild moved into it
  // (when a module is both imported and import()ed, its code lands in a shared chunk).
  const owner = new Map(lazy.map((file) => [file, file]));
  for (const file of lazy) {
    for (const f of added.get(file)) {
      if (!owner.has(f) && Object.hasOwn(outputs[f].inputs, outputs[file].entryPoint)) {
        owner.set(f, file);
      }
    }
  }
  const bytes = (list) => list.reduce((n, f) => n + brotliOf(f), 0);

  const sizes = [{ name: 'entry', bytes: bytes([...entryFiles]) }];
  const shared = new Set();
  for (const [file, files] of added) {
    const own = files.filter((f) => (owner.has(f) ? owner.get(f) === file : users.get(f) === 1));
    for (const f of files) {
      if (!owner.has(f) && users.get(f) > 1) {
        shared.add(f);
      }
    }
    sizes.push({ name: budgetName(outputs[file].entryPoint), bytes: bytes(own) });
  }
  for (const file of [...shared].sort()) {
    const [largest] = Object.entries(outputs[file].inputs).sort((a, b) => b[1].bytesInOutput - a[1].bytesInOutput)[0] ?? ['chunk'];
    sizes.push({ name: `shared:${basename(largest, extname(largest))}`, bytes: brotliOf(file) });
  }
  return sizes;
}

function formatTable(sizes, budgets) {
  const rows = sizes.map(({ name, bytes }) => {
    const budget = budgets[name];
    return `  ${name.padEnd(14)} ${String(bytes).padStart(7)} B${budget === undefined ? '' : ` / ${budget} B`}`;
  });
  return ['web: chunk sizes (brotli)', ...rows].join('\n');
}

const BASE_OPTIONS = {
  bundle: true,
  minify: true,
  publicPath: '/assets/',
  assetNames: '[name]-[hash]',
  entryNames: '[name]-[hash]',
  loader: ASSET_LOADERS,
  legalComments: 'none',
  metafile: true,
  logLevel: 'silent',
};

async function styleEntries() {
  const names = (await readdir(join(root, 'styles'))).filter((n) => n.endsWith('.css') && !STYLE_PARTIALS.has(n));
  return names.sort().map((n) => `styles/${n}`);
}

export async function buildWeb({ dist = join(root, 'dist'), budgetsFile = join(root, 'budgets.json'), log = console.log } = {}) {
  const { routes } = await import(pathToFileURL(join(root, 'src/routes.js')).href);
  const assetsDir = join(dist, 'assets');
  await rm(dist, { recursive: true, force: true });
  await mkdir(assetsDir, { recursive: true });

  const staticAssets = await copyStaticAssets(dist);
  const publicFiles = await copyPublicFiles(dist, staticAssets);
  const theme = await themeScript();
  const toPublic = (file) => '/' + relative(dist, join(root, file)).split(sep).join('/');

  const css = await esbuild.build({
    ...BASE_OPTIONS,
    absWorkingDir: root,
    entryPoints: await styleEntries(),
    outdir: assetsDir,
  });
  const styles = {};
  for (const [file, output] of Object.entries(css.metafile.outputs)) {
    if (output.entryPoint) {
      styles[basename(output.entryPoint)] = file;
    }
  }
  const routeStyles = Object.fromEntries(Object.entries(ROUTE_STYLES)
    .map(([module, names]) => [module, names.filter((n) => styles[n]).map((n) => toPublic(styles[n]))]));

  const js = await esbuild.build({
    ...BASE_OPTIONS,
    absWorkingDir: root,
    entryPoints: ['src/entry.js'],
    splitting: true,
    format: 'esm',
    target: 'es2022',
    outdir: assetsDir,
    chunkNames: 'chunks/[name]-[hash]',
    define: { RS_ASSETS: JSON.stringify(staticAssets), RS_STYLES: JSON.stringify(routeStyles) },
    plugins: [bannedSinksPlugin()],
  });

  const outputs = js.metafile.outputs;
  const fromJs = Object.entries(outputs).filter(([file]) => file.endsWith('.css')).map(([file]) => file);
  if (fromJs.length > 0) {
    throw new Error(`stylesheets imported from scripts (${fromJs.join(', ')}); put them in web/styles and list them in ROUTE_STYLES`);
  }

  function outputFor(entryPoint) {
    const found = Object.entries(outputs).find(([file, o]) => o.entryPoint === entryPoint && file.endsWith('.js'));
    if (!found) {
      throw new Error(`no output chunk for ${entryPoint}`);
    }
    return found[0];
  }

  function closure(file, seen = new Set()) {
    if (seen.has(file)) {
      return seen;
    }
    seen.add(file);
    for (const imported of outputs[file].imports) {
      if (imported.kind === 'import-statement' && !imported.external) {
        closure(imported.path, seen);
      }
    }
    return seen;
  }

  const declared = new Set(routes.map((r) => './' + r.module));
  const imported = new Set(js.metafile.inputs['src/routes.js'].imports
    .filter((i) => i.kind === 'dynamic-import')
    .map((i) => i.original));
  const mismatched = [...declared].filter((m) => !imported.has(m)).concat([...imported].filter((m) => !declared.has(m)));
  if (mismatched.length > 0) {
    throw new Error(`routes.js: module and import() disagree for ${mismatched.join(', ')}`);
  }

  const entryFiles = closure(outputFor('src/entry.js'));
  const served = [
    ...Object.keys(css.metafile.outputs),
    ...Object.keys(outputs),
    ...Object.values(staticAssets).map((p) => relative(root, join(dist, p))),
  ];
  const compressed = await Promise.all(served.map((file) => precompress(join(root, file))));
  const files = Object.fromEntries(served.map((file, i) => [toPublic(file), compressed[i]]));
  const rootFiles = await Promise.all(publicFiles.map(precompress));

  const manifest = {
    entry: [...entryFiles].map(toPublic),
    css: styles['kit.css'] ? [toPublic(styles['kit.css'])] : [],
    routes: Object.fromEntries(routes.map((r) => [r.path, [...closure(outputFor('src/' + r.module))].map(toPublic)])),
    routeCss: Object.fromEntries(routes.map((r) => [r.path, routeStyles[r.module] ?? []])),
    styles: Object.fromEntries(Object.entries(styles).map(([name, file]) => [name, toPublic(file)])),
    assets: staticAssets,
    files,
    public: Object.fromEntries(publicFiles.map((file, i) => ['/' + relative(dist, file).split(sep).join('/'), rootFiles[i]])),
  };
  await writeFile(join(dist, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  await writeFile(join(dist, 'csp.json'), JSON.stringify({ scriptHashes: [theme.hash], themeScript: theme.text }, null, 2) + '\n');

  const brotliOf = (file) => files[toPublic(file)].br ?? files[toPublic(file)].size;
  const sizes = measureChunks(outputs, outputFor('src/entry.js'), brotliOf);
  for (const [name, file] of Object.entries(styles)) {
    sizes.push({ name: name === 'kit.css' ? 'css' : `css:${basename(name, '.css')}`, bytes: brotliOf(file) });
  }

  const budgets = JSON.parse(await readFile(budgetsFile, 'utf8')).chunks;
  log(formatTable(sizes, budgets));
  const { warnings, failures } = checkBudgets(sizes, budgets);
  for (const warning of warnings) {
    log(`web: warning: over budget: ${warning}`);
  }
  if (failures.length > 0) {
    throw new Error(`more than 10 % over the budgets.json baseline:\n  ${failures.join('\n  ')}`);
  }
  log(`web: ${Object.keys(files).length} files, manifest.json and csp.json written`);
  return { manifest, sizes, warnings };
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await buildWeb();
  } catch (error) {
    if (error.errors?.length) {
      const messages = await esbuild.formatMessages(error.errors, { kind: 'error' });
      console.error(messages.join(''));
    } else {
      console.error(`web build failed: ${error.message}`);
    }
    process.exitCode = 1;
  }
}
