// The development loop behind `./build.sh quick`: maps changed paths to the narrowest tests that cover them and runs
// only those. The full suite belongs to `./build.sh verify`, which the pre-push hook runs.
//
//   node tools/quick.mjs [--plan] [--base <ref>] [path ...]
//
// With no paths it uses every file that differs from the merge base with the upstream branch (or origin/main),
// including uncommitted and untracked files. --plan prints what would run and exits.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const PROJECTS = {
  core: 'tests/Retroscope.Core.Tests',
  integration: 'tests/Retroscope.Integration.Tests',
  importer: 'tests/Retroscope.Importer.Tests',
};

// Categories whose classes never start the Testcontainers Postgres.
const DOCKER_FREE = { core: 'all', integration: new Set(['Deploy', 'Scaffold']), importer: new Set(['Scaffold']) };

const TEST_FOLDERS = {
  core: { Adf: 'Adf', Authz: 'Authz', Markup: 'Markup', Poker: 'Poker', Protocol: 'Protocol', Retro: 'Retro',
    Scaffold: 'Scaffold', Security: 'Security', Sprints: 'Sprints', Team: 'Team', Text: 'Text' },
  integration: { Actors: 'Actors', Admin: 'Admin', Auth: 'AuthCore', AuthCore: 'AuthCore', Blobs: 'Blobs',
    Deploy: 'Deploy', Email: 'Email', Hosting: 'Hosting', Jira: 'JiraClient', JiraHttp: 'JiraHttp',
    Leadership: 'Leader', Oidc: 'Oidc', PokerRoom: 'PokerRoom', RetroRooms: 'RetroRoom', RoomHttp: 'RoomHttp',
    Scaffold: 'Scaffold', Schema: 'Schema', TeamHttp: 'TeamHttp', TeamRoom: 'TeamRoom', Transport: 'Transport' },
  importer: { EndToEnd: 'EndToEnd', Identity: 'Identity', Infrastructure: 'Source', Poker: 'PokerMap',
    Retro: 'RetroMap', Scaffold: 'Scaffold' },
};

const WEB_NET = ['outbox', 'room', 'socket', 'supersede', 'fake-server'].map((n) => `tests/web-unit/net/${n}.test.js`);
const SHORTCUTS = ['poker', 'retro'].map((n) => `tests/web-unit/${n}/keyboard.test.js`);
const WEB_CORE_DOM = ['a11y', 'h', 'list', 'router', 'ui'].map((n) => `tests/web-unit/core/${n}.test.js`);
const POKER_LAYOUT = ['geometry', 'avatar-corner', 'column', 'mirror', 'print'].map((n) => `tests/web-unit/poker/${n}.test.js`);
const RICH = ['adf', 'block-marks', 'clipboard', 'commands', 'editor-plugins', 'input-rules', 'rules', 'schema', 'tokens'].map((n) => `tests/web-unit/rich/${n}.test.js`);
const FAKE_SERVER = ['tests/web-fake-server/self.test.js', 'tests/web-fake-server/demo/demo.test.js', 'tests/web-unit/net/fake-server.test.js'];
const SIM = true;

// Each rule adds to the plan for every path it matches. A rule marked fallback applies only when no ordinary rule
// matched that path. A path is "unrouted" when nothing matched; the pre-push pass still covers it.
// Keys: core/integration/importer (categories, or 'all'), build (projects to compile only), node (test files),
// golden, sim, web (bundle into a temp dist), note (printed advice).
const RULES = [
  [/^src\/Retroscope\.Core\/Poker\//, { core: ['Poker'], node: ['tests/web-unit/reducers.test.js'] }],
  [/^src\/Retroscope\.Core\/Retro\//, { core: ['Retro'] }],
  [/^src\/Retroscope\.Core\/Team\//, { core: ['Team', 'Authz'] }],
  // Every room, HTTP route, blob and admin check asks the policy, and the simulator subscribes through it.
  [/^src\/Retroscope\.Core\/Authz\//, {
    core: ['Authz', 'Team'], sim: SIM,
    integration: ['PokerRoom', 'RetroRoom', 'TeamRoom', 'RoomHttp', 'TeamHttp', 'JiraHttp', 'Blobs', 'Admin', 'Hosting'] }],
  [/^src\/Retroscope\.Core\/Room\//, { core: ['Poker', 'Retro', 'Team'], sim: SIM }],
  [/^src\/Retroscope\.Core\/(Protocol|Ids)\//, { core: ['Protocol'], node: ['tests/web-unit/net/supersede.test.js', ...FAKE_SERVER] }],
  [/^src\/Retroscope\.Core\/Markup\//, { core: ['Markup', 'Adf'], node: ['tests/web-unit/markup.test.js'] }],
  [/^src\/Retroscope\.Core\/Adf\//, { core: ['Adf'] }],
  [/^src\/Retroscope\.Core\/Adf\/AdfColor\.cs$/, { node: ['tests/web-unit/markup.test.js'] }],
  [/^src\/Retroscope\.Core\/Security\//, { core: ['Security'] }],
  [/^src\/Retroscope\.Core\/Text\//, { core: ['Text', 'Retro'] }],
  [/^src\/Retroscope\.Core\/Sprints\//, { core: ['Sprints'] }],
  [/^src\/Retroscope\.Core\//, { core: 'all', fallback: true }],

  [/^src\/Retroscope\.Server\/Realtime\/Transport\//, { integration: ['Transport'] }],
  [/^src\/Retroscope\.Server\/Realtime\/Actors\//, { integration: ['Actors'], sim: SIM }],
  // An admitted subscription counts as a visit to the room's team (me.lastTeamId); MeTeamsTests (TeamHttp) holds it.
  [/^src\/Retroscope\.Server\/Realtime\/Actors\/(RoomActor|ActorRuntime|ActorsModule|IRoomBinding)\.cs$/, { integration: ['TeamHttp'] }],
  [/^src\/Retroscope\.Server\/Realtime\/(Rooms|Stores)\/Poker/, { integration: ['PokerRoom'], sim: SIM }],
  [/^src\/Retroscope\.Server\/Realtime\/(Rooms|Stores)\/Retro/, { integration: ['RetroRoom'], sim: SIM }],
  [/^src\/Retroscope\.Server\/Realtime\/(Rooms|Stores)\/Team/, { integration: ['TeamRoom'], sim: SIM }],
  [/^src\/Retroscope\.Server\/Realtime\//, { integration: ['Actors', 'PokerRoom', 'RetroRoom', 'TeamRoom'], sim: SIM, fallback: true }],
  [/^src\/Retroscope\.Server\/Data\/(Leader|Db)\.cs$/, { integration: ['Leader'], sim: SIM }],
  [/^src\/Retroscope\.Server\/Data\/(Db|Migrator)\.cs$/, { integration: ['Schema', 'Deploy'] }],
  // The importer's target is migrated from db/migrations and its COPYs name the columns.
  [/^db\/migrations\//, { integration: ['Schema', 'Deploy'], importer: 'all',
    note: 'a migration can break any test that seeds rows by SQL; grep tests/ for the tables and columns it changes.' }],
  [/^src\/Retroscope\.Server\/Data\/Repos\/(Users|Sessions)Repo/, { integration: ['AuthCore'] }],
  // me.teams, me.lastTeamId and the home redirect (UsersRepo), team visits (ActivityRepo) and kudos received.
  [/^src\/Retroscope\.Server\/Data\/Repos\/(Users|Activity|Endorsements)Repo/, { integration: ['TeamHttp'] }],
  [/^src\/Retroscope\.Server\/Data\/Repos\/(Teams|Invites)Repo/, { integration: ['TeamHttp'] }],
  [/^src\/Retroscope\.Server\/Data\/Repos\/MembershipCache/, { integration: ['TeamRoom'] }],
  [/^src\/Retroscope\.Server\/Data\/Repos\/CursorRepo/, { integration: ['Leader'] }],
  [/^src\/Retroscope\.Server\/Data\/Repos\/BlobsRepo/, { integration: ['Blobs'] }],
  [/^src\/Retroscope\.Server\/Data\/Repos\//, { integration: ['RoomHttp'], fallback: true }],
  [/^src\/Retroscope\.Server\/Hosting\//, { integration: ['Hosting'] }],
  [/^src\/Retroscope\.Server\/(Auth\/Oidc|Endpoints\/OidcEndpoints)/, { integration: ['Oidc'] }],
  [/^src\/Retroscope\.Server\/(Auth\/Email\/|Jobs\/EmailWorker)/, { integration: ['Email'] }],
  [/^src\/Retroscope\.Server\/(Auth\/|Endpoints\/(Auth|Me)Endpoints)/, { integration: ['AuthCore'], fallback: true }],
  [/^src\/Retroscope\.Server\/Endpoints\/MeEndpoints/, { integration: ['AuthCore', 'TeamHttp'] }],
  [/^src\/Retroscope\.Server\/Endpoints\/(Poker|Retro|Mentions|Templates|Endorsements)Endpoints/, { integration: ['RoomHttp'] }],
  [/^src\/Retroscope\.Server\/Endpoints\/(Teams|Invites)Endpoints/, { integration: ['TeamHttp'] }],
  // RouteInventoryTests (Hosting) holds every route to a policy and a rate limit.
  [/^src\/Retroscope\.Server\/Endpoints\//, { integration: ['Hosting'] }],
  [/^src\/Retroscope\.Server\/Endpoints\/JiraEndpoints/, { integration: ['JiraHttp'] }],
  [/^src\/Retroscope\.Server\/Json\/JiraHttpJsonContext\.cs$/, { integration: ['JiraHttp'] }],
  [/^src\/Retroscope\.Server\/(Endpoints\/AdminEndpoints|Jobs\/(Retention|TeamPurge|TokenSweep)Job)/, { integration: ['Admin'] }],
  [/^src\/Retroscope\.Server\/Endpoints\/BlobEndpoints/, { integration: ['Blobs'] }],
  [/^src\/Retroscope\.Server\/Jira\/PokerJiraPort/, { integration: ['PokerRoom'] }],
  [/^src\/Retroscope\.Server\/Jira\//, { integration: ['JiraClient', 'JiraHttp'] }],
  [/^src\/Retroscope\.Server\/(Program\.cs|Json\/|.*Module\.cs$)/, {
    integration: ['Scaffold'], note: 'Program.cs, modules and JSON contexts are AOT-sensitive; the pre-push aot-smoke covers them.' }],
  [/^src\/Retroscope\.Server\//, { build: ['integration'], fallback: true }],

  [/^src\/Retroscope\.Importer\/Map\/Poker/, { importer: ['PokerMap'] }],
  [/^src\/Retroscope\.Importer\/(Map\/Retro|Html\/)/, { importer: ['RetroMap'] }],
  // The expected source schema drives preflight and every stage's table disposition.
  [/^src\/Retroscope\.Importer\/Map\/expected-columns\.json$/, { importer: ['Source', 'Identity', 'EndToEnd'] }],
  [/^src\/Retroscope\.Importer\/Map\//, { importer: ['Identity'], fallback: true }],
  [/^src\/Retroscope\.Importer\/(Source|Storage)\//, { importer: ['Source'] }],
  [/^src\/Retroscope\.Importer\/(Archive\/|Verify\/|Program\.cs)/, { importer: ['EndToEnd'] }],
  [/^src\/Retroscope\.Importer\//, { importer: 'all', fallback: true }],

  [/^src\/[^/]+\/[^/]+\.csproj$|^Directory\.Packages\.props$/, { core: ['Scaffold'], importer: ['Scaffold'], integration: ['Scaffold'] }],
  [/^(Directory\.Build\.props|global\.json|Retroscope\.slnx|build\.sh|\.github\/)/, {
    note: 'This changes how everything builds or what CI runs; only the full pass checks it: ./build.sh verify.' }],

  [/^tests\/Retroscope\.Sim\//, { sim: SIM }],
  [/^tests\/Retroscope\.Core\.Tests\/(Poker\/ViewerModel|Retro\/RetroViewerModel)\.cs$/, { core: ['Poker', 'Retro'], sim: SIM }],
  [/^tests\/Retroscope\.Integration\.Tests\/JiraHttp\/(JiraHttpHost|JiraSocket)\.cs$/, { integration: ['JiraHttp'] }],
  [/^tests\/Retroscope\.Core\.Tests\/Adf\/AdfGen\.cs$/, { core: ['Adf'] }],
  [/^tests\/Retroscope\.(Core|Integration|Importer)\.Tests\//, { fallback: true, testProject: true }],
  [/^tests\/Fakes\//, { integration: ['JiraClient', 'JiraHttp', 'Oidc', 'Email'] }],
  [/^tests\/fixtures\/markup\//, { core: ['Markup', 'Adf'], node: ['tests/web-unit/markup.test.js'] }],
  [/^tests\/fixtures\/adf\//, { core: ['Adf'], node: ['tests/web-unit/rich/adf.test.js'] }],
  [/^tests\/fixtures\/adf-schema\//, { core: ['Adf'] }],
  [/^tests\/golden\/transcripts\//, { core: ['Poker', 'Retro'], node: ['tests/web-unit/reducers.test.js', 'tests/web-fake-server/self.test.js'] }],
  [/^tests\/golden\//, { golden: true, node: ['tests/web-unit/golden-cli.test.js'], core: ['Poker', 'Retro'], fallback: true }],
  [/^tests\/authz-matrix\.csv$/, { core: ['Authz'] }],
  [/^tests\/schema\.snapshot\.sql$/, { integration: ['Schema'] }],
  [/^tests\/web-unit\/core\/_dom\.js$/, { node: WEB_CORE_DOM.concat('tests/web-unit/editor.test.js', ...SHORTCUTS) }],
  [/^tests\/web-unit\/net\/_fakes\.js$/, { node: WEB_NET }],
  [/^tests\/web-unit\/rich\/_project\.js$/, { node: RICH }],
  [/^tests\/web-unit\/team\/_ctx\.js$/, { node: ['tests/web-unit/team/boards.test.js', 'tests/web-unit/team/poker-list.test.js'] }],
  [/^tests\/web-fake-server\//, { node: FAKE_SERVER }],
  // commands.test.js opens every edit view the fake serves.
  [/^tests\/web-fake-server\/edit-views\.gen\.json$/, { node: ['tests/web-unit/rich/commands.test.js'] }],
  // WebFakeAdfTests renders the fixtures' ADF and regenerates edit-views.gen.json from their projection.
  [/^tests\/web-fake-server\/((fixtures|demo)\/jira|edit-views\.gen)\.json$/, { core: ['Adf'] }],
  // SprintReportTests holds the fixtures' sprintReport blocks to the server's property names.
  [/^tests\/web-fake-server\/(fixtures|demo)\/jira\.json$/, { integration: ['JiraHttp'] }],
  [/^tests\/e2e\/fake\//, { note: 'Run the changed spec alone: ./build.sh fake-e2e "<area>/<file>.spec.js".' }],
  [/^tests\/e2e\/(real|smoke)\//, { note: 'Run the changed spec alone against the test stack: ./build.sh e2e "<dir>/<file>.spec.js".' }],
  [/^tests\/e2e\/(screenshots\/|[^/]+$)/, { note: 'Run the specs that use it: ./build.sh fake-e2e or ./build.sh screenshots.' }],
  [/^(tests\/Retroscope\.Soak|deploy\/drills)\//, { note: 'The soak and the drills run by hand against compose.test.yml (deploy/RUNBOOK.md).' }],

  [/^web\//, { web: true }],
  [/^web\/src\/core\/router\.js$/, { node: ['tests/web-fake-server/self.test.js'] }],
  [/^web\/src\/core\/(theme|theme-boot)\.js$/, { node: ['tests/web-unit/core/theme.test.js', 'tests/web-unit/core/build.test.js'] }],
  [/^web\/src\/core\/clock\.js$/, { node: ['tests/web-unit/net/socket.test.js'] }],
  [/^web\/src\/core\/a11y\.js$/, { node: SHORTCUTS }],
  [/^web\/src\/ui\/(popover|menu)\.js$/, { node: SHORTCUTS }],
  [/^web\/src\/ui\//, { node: ['tests/web-unit/core/ui.test.js'] }],
  [/^web\/src\/markup\//, { node: ['tests/web-unit/markup.test.js', 'tests/web-unit/editor.test.js'] }],
  [/^web\/src\/markup\/(doc|parse)\.js$/, { node: ['tests/web-fake-server/self.test.js'] }],
  [/^web\/src\/markup\/doc\.js$/, { node: RICH }],
  [/^web\/src\/rich\//, { node: [...RICH, 'tests/web-unit/core/build.test.js'] }],
  // AdfSchemaWebMirrorTests and AdfColorWebMirrorTests hold the editor's allow-list and palette to Core/Adf.
  [/^web\/src\/rich\/(adf-schema|palette)\.json$/, { core: ['Adf'] }],
  // The fake's read view maps colours through the editor's palette.
  [/^web\/src\/rich\/palette\.json$/, { node: FAKE_SERVER }],
  [/^web\/src\/net\/outbox\.js$/, { node: WEB_NET }],
  [/^web\/src\/net\/socket\.js$/, { node: ['tests/web-unit/net/fake-server.test.js'] }],
  [/^web\/src\/net\/protocol\.gen\.js$/, {
    node: ['tests/web-unit/net/supersede.test.js', ...FAKE_SERVER],
    note: 'protocol.gen.js is generated from Core/Protocol/Catalog.cs; do not edit it by hand.' }],
  [/^web\/src\/poker\/model\.js$/, { node: ['tests/web-unit/reducers.test.js', 'tests/web-fake-server/demo/demo.test.js'] }],
  [/^web\/src\/poker\/(geometry|seats)\.js$/, { node: POKER_LAYOUT }],
  [/^web\/src\/poker\/drag\.js$/, { node: ['tests/web-unit/poker/drag-zones.test.js'] }],
  [/^web\/src\/poker\/issue-drawer\.js$/, { node: ['tests/web-unit/core/build.test.js'] }],
  // RouteInventoryTests (Hosting) holds routes.js to Server/Hosting ShellRoute.All.
  [/^web\/src\/routes\.js$/, {
    node: ['tests/web-unit/scaffold/routes.test.js', 'tests/web-unit/core/router.test.js', 'tests/web-fake-server/self.test.js', 'tests/web-unit/core/build.test.js'],
    integration: ['Hosting'] }],
  [/^web\/styles\/jira\.css$/, { node: ['tests/web-unit/markup.test.js'] }],
  [/^(web\/styles\/|web\/(build\.mjs|budgets\.json|package\.json)$|package-lock\.json$)/, { node: ['tests/web-unit/core/build.test.js', 'tests/web-unit/core/theme.test.js'] }],
  [/^(package\.json|package-lock\.json|web\/package\.json|THIRD_PARTY\.md)$/, { node: ['tests/web-unit/core/deps.test.js'] }],
  [/^(web\/assets\/|tools\/make-sprite\.mjs$|THIRD_PARTY\.md$)/, { node: ['tests/web-unit/assets.test.js'] }],

  [/^deploy\/(docker-compose|compose\.test)\.yml$/, { integration: ['Deploy'] }],
  [/^deploy\/Dockerfile/, { note: 'Dockerfiles are exercised only by ./build.sh aot-smoke, which the pre-push pass runs.' }],
  [/^docs\/DESIGN\.md$/, { note: 'docs/DESIGN.html is now stale; regenerate it or flag it.' }],
  [/^deploy\/COOLIFY_SETUP\.md$/, { note: 'deploy/COOLIFY_SETUP.html is now stale; regenerate it or flag it.' }],
  [/^(docs\/|deploy\/|[^/]+\.md$|\.claude\/|tools\/quick\.mjs$|tools\/git-hooks\/)/, { fallback: true }],
];

function emptyPlan() {
  return { core: new Set(), integration: new Set(), importer: new Set(), build: new Set(), node: new Set(),
    golden: false, sim: false, web: false, notes: new Set(), unrouted: [] };
}

function addCategories(plan, project, categories) {
  if (categories === 'all') plan[project].add('all');
  else for (const c of categories) plan[project].add(c);
}

// A test class runs its folder's category. A file with no Category trait is a fixture or harness that other folders
// may share (PostgresFixture, FakeAccess, ImportHarness), so it runs the whole project.
function routeTestProject(plan, path) {
  const [, name, folder] = /^tests\/Retroscope\.(Core|Integration|Importer)\.Tests\/(?:([^/]+)\/)?/.exec(path);
  const project = name.toLowerCase();
  const category = folder && TEST_FOLDERS[project][folder];
  const isTestClass = existsSync(path) && readFileSync(path, 'utf8').includes('Trait("Category"');
  addCategories(plan, project, category && isTestClass ? [category] : 'all');
}

// A web module with a same-path unit test runs it (web/src/core/fmt.js -> tests/web-unit/core/fmt.test.js).
function sameNameTest(path) {
  const m = /^web\/src\/(.+)\.js$/.exec(path);
  const test = m && `tests/web-unit/${m[1]}.test.js`;
  return test && existsSync(test) ? test : null;
}

export function planFor(paths) {
  const plan = emptyPlan();
  for (const path of paths) {
    let matched = RULES.filter(([re, r]) => !r.fallback && re.test(path)).map(([, r]) => r);
    if (matched.length === 0) {
      matched = matched.concat(RULES.filter(([re, r]) => r.fallback && re.test(path)).map(([, r]) => r));
    }
    const ownTest = /^tests\/(web-unit|web-fake-server)\/.+\.test\.js$/.test(path) ? path : sameNameTest(path);
    if (ownTest) plan.node.add(ownTest);
    if (matched.length === 0 && !ownTest) plan.unrouted.push(path);
    for (const rule of matched) {
      for (const project of Object.keys(PROJECTS)) if (rule[project]) addCategories(plan, project, rule[project]);
      for (const p of rule.build ?? []) plan.build.add(p);
      for (const f of rule.node ?? []) plan.node.add(f);
      for (const n of [rule.note].filter(Boolean)) plan.notes.add(n);
      if (rule.testProject) routeTestProject(plan, path);
      plan.golden ||= !!rule.golden;
      plan.sim ||= !!rule.sim;
      plan.web ||= !!rule.web;
    }
  }
  for (const f of [...plan.node]) if (!existsSync(f)) plan.node.delete(f);
  return plan;
}

function git(...args) {
  const r = spawnSync('git', args, { encoding: 'utf8' });
  return r.status === 0 ? r.stdout.trim() : null;
}

function changedPaths(base) {
  const ref = base ?? git('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}') ?? 'origin/main';
  const mergeBase = git('merge-base', 'HEAD', ref);
  if (!mergeBase) throw new Error(`no merge base with ${ref}; pass --base <ref> or explicit paths`);
  const tracked = git('diff', '--name-only', mergeBase) ?? '';
  const untracked = git('ls-files', '--others', '--exclude-standard') ?? '';
  return { ref, paths: [...new Set(`${tracked}\n${untracked}`.split('\n').filter(Boolean))].sort() };
}

function dockerReachable() {
  return spawnSync('docker', ['info'], { stdio: 'ignore' }).status === 0;
}

function needsDocker(project, categories) {
  if (DOCKER_FREE[project] === 'all') return false;
  return categories.has('all') || [...categories].some((c) => !DOCKER_FREE[project].has(c));
}

function steps(plan, { docker }) {
  const jobs = Number(process.env.RS_JOBS ?? 2);
  const buildLimit = jobs > 0 ? [`-maxcpucount:${jobs}`] : [];
  const testLimit = jobs > 0 ? ['--max-threads', String(jobs)] : [];
  const out = [];
  if (plan.web) {
    out.push({ label: 'web build (temporary dist, web/dist untouched)', web: true });
  }
  if (plan.node.size > 0) {
    out.push({ label: 'web unit tests', cmd: ['node', '--test', '--test-concurrency=2', ...[...plan.node].sort()] });
  }
  if (plan.golden) out.push({ label: 'golden vectors', cmd: ['node', 'tests/golden/make-golden.mjs', '--verify'] });
  for (const [project, dir] of Object.entries(PROJECTS)) {
    const categories = plan[project];
    if (categories.size === 0 && !plan.build.has(project)) continue;
    if (categories.size > 0 && needsDocker(project, categories) && !docker) {
      out.push({ label: `${dir}: skipped, Docker is not reachable (the pre-push pass runs it)`, skip: true });
      continue;
    }
    out.push({ label: `build ${dir}`, cmd: ['dotnet', 'build', dir, '--nologo', '-v', 'q', ...buildLimit] });
    if (categories.size === 0) continue;
    const filter = categories.has('all') ? [] : ['--filter', [...categories].sort().map((c) => `Category=${c}`).join('|')];
    out.push({ label: `${dir} ${categories.has('all') ? '(all)' : [...categories].sort().join(', ')}`,
      cmd: ['dotnet', 'test', '--project', dir, '--no-build', ...filter, ...testLimit] });
  }
  if (plan.sim) {
    out.push({ label: 'simulation (50 seeds; the pre-push pass runs 2000 with teeth)',
      cmd: ['dotnet', 'run', '--project', 'tests/Retroscope.Sim', '--', '--seeds', '50', '--strict', '--no-teeth'] });
  }
  return out;
}

async function buildWebToTemp() {
  const dist = mkdtempSync(join(tmpdir(), 'rs-quick-web-'));
  try {
    const { buildWeb } = await import('../web/build.mjs');
    await buildWeb({ dist, log: () => {} });
    return 0;
  } catch (e) {
    console.error(e.message ?? e);
    return 1;
  } finally {
    rmSync(dist, { recursive: true, force: true });
  }
}

async function main(argv) {
  const args = [...argv];
  const planOnly = args.includes('--plan');
  const baseAt = args.indexOf('--base');
  const base = baseAt >= 0 ? args.splice(baseAt, 2)[1] : undefined;
  const explicit = args.filter((a) => !a.startsWith('--'));
  const { ref, paths } = explicit.length > 0 ? { ref: null, paths: explicit } : changedPaths(base);

  console.log(ref ? `changes since the merge base with ${ref}: ${paths.length} file(s)` : `paths: ${paths.join(' ')}`);
  if (paths.length === 0) return 0;
  const plan = planFor(paths);
  const docker = planOnly || ['integration', 'importer'].every((p) => !needsDocker(p, plan[p])) || dockerReachable();
  const todo = steps(plan, { docker });

  for (const n of plan.notes) console.log(`note: ${n}`);
  if (plan.unrouted.length > 0) {
    console.log(`no focused test for: ${plan.unrouted.join(', ')} (the pre-push pass covers them)`);
  }
  if (todo.length === 0) {
    console.log('nothing to run');
    return 0;
  }
  if (planOnly) {
    for (const s of todo) console.log(`- ${s.label}${s.cmd ? `\n    ${s.cmd.join(' ')}` : ''}`);
    return 0;
  }

  const before = git('status', '--porcelain');
  const failed = [];
  for (const s of todo) {
    console.log(`\n==> ${s.label}`);
    if (s.skip) continue;
    const status = s.web ? await buildWebToTemp() : spawnSync(s.cmd[0], s.cmd.slice(1), { stdio: 'inherit' }).status;
    if (status !== 0) {
      failed.push(s.label);
      if (s.label.startsWith('build ')) break;
    }
  }
  const after = git('status', '--porcelain');
  if (after !== before) {
    console.log('\nthe run changed tracked or untracked files (regenerated outputs); review and commit them:');
    console.log(git('status', '--short'));
  }
  console.log(failed.length > 0 ? `\nquick: FAILED (${failed.join('; ')})` : '\nquick: passed');
  return failed.length > 0 ? 1 : 0;
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).then((code) => process.exit(code), (e) => {
    console.error(`quick: ${e.message}`);
    process.exit(2);
  });
}
