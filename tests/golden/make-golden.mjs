// Golden vectors from the original TypeScript (DESIGN §j). Runs verbatim copies of the original pure
// functions (tests/golden/src/*.ts) on seeded random inputs and writes one JSON file per function.
//
//   node tests/golden/make-golden.mjs            regenerate the JSON files
//   node tests/golden/make-golden.mjs --verify   check span hashes and that regenerating is byte-identical
//
// The original repository is located with --orig <dir> or RETROSCOPE_ORIG. When it is present, each
// span is also hashed from the original at the pinned commit.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const root = dirname(fileURLToPath(import.meta.url));
const commit = '7f48a79';

const usage = 'usage: node tests/golden/make-golden.mjs [--verify] [--orig <dir>]';
const args = process.argv.slice(2);
const verify = args.includes('--verify');
const origArg = args.indexOf('--orig');
if (origArg >= 0 && (args[origArg + 1] === undefined || args[origArg + 1].startsWith('--'))) {
  console.error(`--orig needs a directory\n${usage}`);
  process.exit(2);
}
const origDir = origArg >= 0 ? args[origArg + 1] : process.env.RETROSCOPE_ORIG;

const sha256 = (text) => createHash('sha256').update(text).digest('hex');

function parseSpans(file) {
  const lines = readFileSync(file, 'utf8').split('\n');
  const spans = [];
  for (let i = 0; i < lines.length; i++) {
    const header = /^\/\/ golden-span (\S+) (\d+)-(\d+) (\w+) sha256:([0-9a-f]{64})$/.exec(lines[i]);
    if (!header) {
      continue;
    }
    const [, path, start, end, spanCommit, hash] = header;
    const length = Number(end) - Number(start) + 1;
    const body = lines.slice(i + 1, i + 1 + length);
    if (lines[i + 1 + length] !== '// golden-span-end') {
      throw new Error(`${relative(root, file)}: span ${path}:${start}-${end} is not ${length} lines long`);
    }
    spans.push({ path, start: Number(start), end: Number(end), commit: spanCommit, hash, text: body.join('\n') + '\n' });
    i += length + 1;
  }
  return spans;
}

function originalSpan(span) {
  const source = execFileSync('git', ['-C', origDir, 'show', `${span.commit}:${span.path}`], { encoding: 'utf8' });
  return source.split('\n').slice(span.start - 1, span.end).join('\n') + '\n';
}

function checkSpans(file) {
  const spans = parseSpans(file);
  if (spans.length === 0) {
    throw new Error(`${relative(root, file)}: no golden spans`);
  }
  const failures = [];
  for (const span of spans) {
    const name = `${relative(root, file)} ← ${span.path}:${span.start}-${span.end}`;
    if (span.commit !== commit) {
      failures.push(`${name}: recorded commit ${span.commit}, expected ${commit}`);
    }
    if (sha256(span.text) !== span.hash) {
      failures.push(`${name}: copy no longer matches its recorded sha256`);
    }
    if (origDir && sha256(originalSpan(span)) !== span.hash) {
      failures.push(`${name}: original at ${span.commit} does not match the recorded sha256`);
    }
  }
  return { spans, failures };
}

async function load(file) {
  const result = await esbuild.build({
    entryPoints: [file],
    bundle: true,
    format: 'esm',
    platform: 'node',
    target: 'es2022',
    write: false,
    logLevel: 'warning',
  });
  const code = result.outputFiles[0].text;
  return import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
}

// mulberry32: small, seedable and identical on every Node version.
function random(seed) {
  let state = seed >>> 0;
  const next = () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const int = (min, max) => min + Math.floor(next() * (max - min + 1));
  const pick = (items) => items[int(0, items.length - 1)];
  const chance = (p) => next() < p;
  return { int, pick, chance };
}

const roundTrip = (value) => JSON.parse(JSON.stringify(value));

function isoTimestamp(rng, base = Date.UTC(2026, 0, 5, 9)) {
  const ms = base + rng.int(0, 40) * 86_400_000 + rng.pick([0, 0, 3_600_000, 1_800_123]);
  const iso = new Date(ms).toISOString();
  if (!rng.chance(0.3)) {
    return iso;
  }
  // Same instant written with an explicit offset, as Postgres and Jira do.
  const hours = rng.pick([-3, 0, 1, 10]);
  const shifted = new Date(ms + hours * 3_600_000).toISOString().slice(0, 23);
  const sign = hours < 0 ? '-' : '+';
  return `${shifted}${sign}${String(Math.abs(hours)).padStart(2, '0')}:00`;
}

// Names are Title Case words, digit runs without leading zeros, spaces and dots only. On that alphabet
// ICU's numeric collation (localeCompare) and an ordinal compare with numeric digit runs agree, so the
// vectors are stable across ICU versions and checkable by Core/Text/NaturalCompare (no ICU).
const sprintWords = ['Sprint', 'Team Sprint', 'Platform Sprint', 'Sprint Alpha', 'Release', 'Hotfix'];
const authorNames = ['Ana', 'Ana Lima', 'Anabel', 'Bruno Costa', 'Carla', 'Diego Souza', 'Anonymous', 'Zoe'];

function pokerCase(rng, mod) {
  const deck = [-1, 0, 1, 2, 3, 5, 8, 13, 21];
  const selections = [];
  const count = rng.int(0, 10);
  for (let i = 0; i < count; i++) {
    const points = rng.pick(deck);
    const selection = { points };
    if (rng.chance(0.3)) {
      const index = deck.indexOf(points);
      selection.betweenHighPoints = rng.pick([
        deck[Math.min(index + 1, deck.length - 1)],
        deck[Math.min(index + 1, deck.length - 1)],
        deck[Math.max(index - 1, 1)],
        points,
        rng.pick(deck.slice(1)),
        null,
      ]);
    }
    selections.push(selection);
  }
  const input = roundTrip(selections);
  return { selections: input, expected: mod.getWinningVoteFromSelections(input) };
}

function sprintCase(rng, mod) {
  const sprints = [];
  const sprintCount = rng.int(0, 5);
  while (sprints.length < sprintCount) {
    let name = rng.pick(sprintWords);
    if (rng.chance(0.85)) {
      name += ` ${rng.int(1, 120)}`;
      if (rng.chance(0.1)) {
        name += `.${rng.int(1, 12)}`;
      }
    }
    if (!sprints.some((s) => s.name === name)) {
      const date = rng.pick([
        () => isoTimestamp(rng),
        () => isoTimestamp(rng),
        () => isoTimestamp(rng),
        () => sprints.length > 0 ? rng.pick(sprints).date : isoTimestamp(rng),
        () => rng.pick(['not a date', 'TBD', '2026-13-40T00:00:00.000Z']),
        () => null,
        () => '',
      ])();
      sprints.push({ name, date });
    }
  }

  const issues = [];
  const count = rng.int(0, 12);
  for (let i = 0; i < count; i++) {
    const issue = { key: `DUN-${100 + i}` };
    if (sprints.length === 0 || rng.chance(0.25)) {
      const backlog = rng.pick([null, '', 'Backlog', undefined]);
      if (backlog !== undefined) {
        issue.sprint = backlog;
      }
      if (rng.chance(0.2)) {
        issue.sprintStartDate = isoTimestamp(rng);
      }
    } else {
      const sprint = rng.pick(sprints);
      issue.sprint = sprint.name;
      const date = rng.pick([sprint.date, sprint.date, sprint.date, null, '', undefined, isoTimestamp(rng)]);
      if (date !== undefined) {
        issue.sprintStartDate = date;
      }
    }
    issues.push(issue);
  }

  const input = roundTrip(issues);
  const result = mod.buildJiraBrowseIssuesBySprint(input);
  return {
    issues: input,
    expected: {
      backlog: result.backlog.map((i) => i.key),
      sprintBuckets: result.sprintBuckets.map((b) => ({ name: b.name, issues: b.issues.map((i) => i.key) })),
    },
  };
}

function retroCase(rng, mod) {
  const sortKeys = ['votes-desc', 'votes-asc', 'time-asc', 'time-desc', 'author-asc', 'author-desc'];
  const timestamps = Array.from({ length: rng.int(1, 4) }, () => isoTimestamp(rng));
  const items = [];
  const count = rng.int(0, 10);
  for (let i = 0; i < count; i++) {
    items.push({
      id: `c${i + 1}`,
      author: rng.pick([...authorNames, '', null]),
      votes: rng.int(0, 4),
      created_at: rng.chance(0.2) ? isoTimestamp(rng) : rng.pick(timestamps),
    });
  }
  const input = roundTrip(items);
  const expected = Object.fromEntries(sortKeys.map((key) => [key, mod.sortItems(input, key).map((i) => i.id)]));
  return { items: input, expected };
}

function topicCase(rng, mod) {
  const epics = Array.from({ length: rng.int(1, 6) }, (_, i) => ({
    key: `DUN-${10 + i}`,
    summary: rng.pick(['Checkout revamp', 'Search', 'Onboarding', 'Billing', 'Mobile app', 'Reports']),
  }));
  const rounds = [];
  const count = rng.int(0, 12);
  for (let i = 0; i < count; i++) {
    const ticket = rng.pick([
      `DUN-${200 + i}`,
      `DUN-${200 + i}`,
      `DUN-${200 + i}`,
      `round:${i + 1}`,
      `ROUND:${i + 1}`,
      ` round:${i + 1} `,
      'round:',
      `round:${i + 1}a`,
      '',
      '  ',
      null,
    ]);
    const epic = rng.pick(epics);
    const parent = rng.pick([
      [epic.key, epic.summary],
      [epic.key, epic.summary],
      [epic.key, epic.summary],
      [epic.key, null],
      [` ${epic.key} `, epic.summary],
      [null, epic.summary],
      ['', `${epic.summary} `],
      [epic.key, rng.pick(['Renamed epic', '  '])],
      [null, null],
      ['  ', ''],
    ]);
    rounds.push({
      ticket_number: ticket,
      ticket_parent_key: parent[0],
      ticket_parent_summary: parent[1],
    });
  }
  const input = roundTrip(rounds);
  const summary = mod.getSessionTopicSummary75Percent(input);
  const toneIndex = (toneKey) => mod.TONES.indexOf(mod.parentBadgeClassName(mod.sessionTopicBadgeToneForParentClass(toneKey)));
  const expected = summary?.kind === 'badges'
    ? { kind: 'badges', topics: summary.topics.map((t) => ({ ...t, toneIndex: toneIndex(t.toneKey) })) }
    : summary;
  return { rounds: input, expected };
}

// Inputs of the last three are larger; 2,000 cases already exercise every branch many times and keep
// the checked-in JSON near 1 MB each (docs/decisions/U47.md).
const vectors = [
  { name: 'poker-scoring', seed: 52, count: 10_000, make: pokerCase },
  { name: 'sprint-buckets', seed: 16, count: 2_000, make: sprintCase },
  { name: 'retro-sort', seed: 50, count: 2_000, make: retroCase },
  { name: 'topic-summary', seed: 75, count: 2_000, make: topicCase },
];

function render(spans, seed, cases) {
  const source = spans.map((s) => `${s.path}:${s.start}-${s.end}@${s.commit}`);
  return [
    '{',
    `  "source": ${JSON.stringify(source)},`,
    `  "seed": ${seed},`,
    '  "cases": [',
    cases.map((c) => `    ${JSON.stringify(c)}`).join(',\n'),
    '  ]',
    '}',
    '',
  ].join('\n');
}

const failures = [];
for (const { name, seed, count, make } of vectors) {
  const src = join(root, 'src', `${name}.ts`);
  const out = join(root, `${name}.json`);
  const checked = checkSpans(src);
  failures.push(...checked.failures);

  const mod = await load(src);
  const rng = random(seed);
  const cases = Array.from({ length: count }, () => make(rng, mod));
  const text = render(checked.spans, seed, cases);

  if (!verify) {
    writeFileSync(out, text);
    console.log(`wrote ${relative(process.cwd(), out)} (${cases.length} cases)`);
  } else if (!existsSync(out)) {
    failures.push(`${name}.json is missing; run make-golden.mjs`);
  } else if (readFileSync(out, 'utf8') !== text) {
    failures.push(`${name}.json differs from a fresh regeneration`);
  }
}

if (!origDir) {
  console.log('original repository not given (--orig or RETROSCOPE_ORIG): spans checked against their recorded hashes only');
}
if (failures.length > 0) {
  console.error(failures.map((f) => `FAIL ${f}`).join('\n'));
  process.exit(1);
}
console.log(verify ? 'golden vectors verified' : 'golden vectors generated');
