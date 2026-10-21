// Front-end build (skeleton from U0; U10 adds precompression, csp.json hashes, budgets and the innerHTML ban).
import { rm, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';
import { routes } from './src/routes.js';

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, 'dist');
const assets = join(dist, 'assets');

await rm(dist, { recursive: true, force: true });
await mkdir(assets, { recursive: true });

const result = await esbuild.build({
  absWorkingDir: root,
  entryPoints: ['src/entry.js'],
  bundle: true,
  splitting: true,
  format: 'esm',
  minify: true,
  target: 'es2022',
  outdir: assets,
  entryNames: '[name]-[hash]',
  chunkNames: 'chunks/[name]-[hash]',
  assetNames: '[name]-[hash]',
  publicPath: '/assets/',
  metafile: true,
  logLevel: 'warning',
});

const outputs = result.metafile.outputs;
const publicPath = (file) => '/' + relative(dist, join(root, file)).split('\\').join('/');

function outputFor(entryPoint) {
  const found = Object.entries(outputs).find(([, o]) => o.entryPoint === entryPoint);
  if (!found) {
    throw new Error(`no output chunk for ${entryPoint}`);
  }
  return found[0];
}

function withStaticImports(file, seen = new Set()) {
  if (seen.has(file)) {
    return seen;
  }
  seen.add(file);
  for (const imported of outputs[file].imports) {
    if (imported.kind === 'import-statement' && !imported.external) {
      withStaticImports(imported.path, seen);
    }
  }
  return seen;
}

function checkRouteModules() {
  const declared = new Set(routes.map((r) => './' + r.module));
  const imported = new Set(
    result.metafile.inputs['src/routes.js'].imports
      .filter((i) => i.kind === 'dynamic-import')
      .map((i) => i.original),
  );
  const mismatched = [...declared].filter((m) => !imported.has(m))
    .concat([...imported].filter((m) => !declared.has(m)));
  if (mismatched.length > 0) {
    throw new Error(`routes.js: module and import() disagree for ${mismatched.join(', ')}`);
  }
}

checkRouteModules();

const entry = outputFor('src/entry.js');
const manifest = {
  entry: [...withStaticImports(entry)].map(publicPath),
  routes: Object.fromEntries(routes.map((r) => [
    r.path,
    [...withStaticImports(outputFor('src/' + r.module))].map(publicPath),
  ])),
};

await writeFile(join(dist, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
await writeFile(join(dist, 'csp.json'), JSON.stringify({ scriptHashes: [] }, null, 2) + '\n');
console.log(`web: ${Object.keys(outputs).length} files, manifest.json written`);
