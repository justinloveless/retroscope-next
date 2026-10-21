import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const script = join(root, 'tests', 'golden', 'make-golden.mjs');
const usage = 'usage: node tests/golden/make-golden.mjs [--verify] [--orig <dir>]';

for (const args of [['--orig'], ['--orig', '--verify']]) {
  test(`make-golden ${args.join(' ')} fails with a usage error`, () => {
    const result = spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8' });

    assert.equal(result.status, 2);
    assert.match(result.stderr, /--orig needs a directory/);
    assert.ok(result.stderr.includes(usage), result.stderr);
  });
}
