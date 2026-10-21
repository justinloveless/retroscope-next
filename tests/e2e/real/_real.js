// Shared setup for the full e2e matrix against deploy/compose.test.yml (DESIGN §j). Builds on smoke/_stack.js (the
// https base URL, a fresh client address per context, Mailpit) and adds: several signed-in people per test, each in
// its own context and failing the test on any CSP violation, page error or dialog; seeded accounts; SQL against the
// stack's database; the FakeOidc route; and a lock that lets one spec restart the app while nothing else runs.
import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { BASE, MAILPIT, PASSWORD, clientIp, expect, json, mailedToken, test as stack } from '../smoke/_stack.js';

export { BASE, MAILPIT, PASSWORD, clientIp, expect, json, mailedToken };

const run = promisify(execFile);
const ROOT = fileURLToPath(new URL('../../../', import.meta.url));
const COMPOSE = ['compose', '-f', `${ROOT}deploy/compose.test.yml`];

export const OIDC = `http://localhost:${process.env.RS_TEST_OIDC_PORT ?? 18446}`;
// The app's view of FakeOidc (compose.test.yml); a browser cannot resolve it, so contexts route it to OIDC.
const OIDC_IN_STACK = 'http://fakeoidc:8081';
export const ENTRA_TENANT = '7c1e5d2a-0b7e-4c55-9d1e-3f4a8b6c2e10';

// bcrypt cost 4 of PASSWORD. Seeded accounts sign in through the real login route, and at cost 10 the HashGate (one
// hash at a time, 2 s queue) would answer 503 to a dozen parallel sign-ins on the stack's 0.6 vCPU.
const SEED_HASH = '$2a$04$6x8JWnT3vccNDWa3GtzOROA19eccXMuJ/ZQs3MfUCjIfmf6S6BKRS';

/** Runs `statement` with psql in the stack's db container; returns rows as arrays of strings. */
export async function sql(statement) {
  const { stdout } = await run('docker', [...COMPOSE, 'exec', '-T', 'db', 'psql', '-U', 'retroscope_owner', '-d', 'retroscope',
    '-v', 'ON_ERROR_STOP=1', '-At', '-F', '\t', '-c', statement]);
  return stdout.split('\n').filter((line) => line.length > 0).map((line) => line.split('\t'));
}

export const compose = (...args) => run('docker', [...COMPOSE, ...args], { timeout: 180_000 });

const literal = (value) => `'${String(value).replaceAll("'", "''")}'`;

/**
 * Inserts accounts straight into the database: signing up through the route sends a verification mail per person,
 * and the global email budget (200/h) would run out within one run of the matrix. The auth specs sign up for real.
 */
export async function seedUsers(names, { verified = true, ordered = false } = {}) {
  // `ordered` makes the ids sort in the order of `names` (seats sort by principal id), for the visual baselines.
  const id = (i) => (ordered ? `${i.toString(16)}${randomUUID().slice(1)}` : randomUUID());
  const users = names.map((name, i) => ({ id: id(i), email: `e2e-${randomUUID()}@example.test`, name }));
  await sql(`
    insert into principals (id, kind) values ${users.map((u) => `(${literal(u.id)}, 'user')`).join(', ')};
    insert into users (id, email, email_verified_at, password_hash, full_name) values ${users.map((u) =>
    `(${literal(u.id)}, ${literal(u.email)}, ${verified ? 'now()' : 'null'}, ${literal(SEED_HASH)}, ${literal(u.name)})`).join(', ')};`);
  return users;
}

export async function signIn(request, user) {
  const response = await request.post('/api/auth/login', json({ email: user.email, password: PASSWORD }));
  expect(response.status(), await response.text()).toBe(200);
}

/** The options a project's device gives the default context, for contexts a test opens itself. */
function deviceOptions(project) {
  const keys = ['viewport', 'screen', 'userAgent', 'deviceScaleFactor', 'isMobile', 'hasTouch', 'locale', 'timezoneId'];
  return Object.fromEntries(keys.filter((key) => project.use[key] !== undefined).map((key) => [key, project.use[key]]));
}

/**
 * Signs the context's next OIDC flow in as `claims`. The app redirects the browser to FakeOidc at an address only the
 * stack resolves, and Playwright routes no request that a redirect started, so the start request itself is routed:
 * the spec relays it, asks the published FakeOidc port to authorize (which answers with the callback URL), and hands
 * the browser the flow cookie plus a page that goes on to that callback. WebKit cannot be fulfilled with a 3xx.
 */
export async function routeOidc(context, claims) {
  const encoded = Buffer.from(JSON.stringify(claims)).toString('base64url');
  await context.route('**/auth/oidc/*/start*', async (route) => {
    const start = await route.fetch({ maxRedirects: 0 });
    expect(start.status()).toBe(302);
    const authorize = new URL(start.headers().location.replace(OIDC_IN_STACK, OIDC));
    authorize.searchParams.set('fake_claims', encoded);
    const granted = await context.request.get(authorize.toString(), { maxRedirects: 0 });
    expect(granted.status(), await granted.text()).toBe(302);
    const callback = granted.headers().location.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
    await route.fulfill({
      status: 200,
      headers: { 'content-type': 'text/html', 'cache-control': 'no-store', 'set-cookie': start.headers()['set-cookie'] },
      body: `<!doctype html><html><head><meta http-equiv="refresh" content="0;url=${callback}"></head></html>`,
    });
  });
}

// A shared/exclusive lock over the whole stack, in a directory every worker sees. Each test holds it shared; a test
// that restarts the app takes it exclusively, so no other test sees the outage. Entries name their worker's pid, so
// the ones a killed run left behind are ignored.
const LOCK = `${ROOT}artifacts/e2e/stack-lock`;
const EXCLUSIVE = `${LOCK}/exclusive`;
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const alive = (pid) => {
  try {
    process.kill(Number(pid), 0);
    return true;
  } catch {
    return false;
  }
};

async function exclusiveHeld() {
  const holder = await readFile(EXCLUSIVE, 'utf8').catch(() => null);
  if (holder !== null && !alive(holder)) {
    await rm(EXCLUSIVE, { force: true });
    return false;
  }
  return holder !== null;
}

async function holdShared() {
  const me = `${LOCK}/shared/${process.pid}-${randomUUID()}`;
  await mkdir(`${LOCK}/shared`, { recursive: true });
  for (;;) {
    while (await exclusiveHeld()) {
      await sleep(250);
    }
    await writeFile(me, '');
    if (!(await exclusiveHeld())) {
      return () => rm(me, { force: true });
    }
    await rm(me, { force: true });
  }
}

async function holdExclusive(releaseShared) {
  await releaseShared();
  for (;;) {
    await exclusiveHeld();
    try {
      await writeFile(EXCLUSIVE, String(process.pid), { flag: 'wx' });
      break;
    } catch {
      await sleep(250);
    }
  }
  const others = async () => (await readdir(`${LOCK}/shared`)).filter((name) => alive(name.split('-')[0]));
  while ((await others()).length > 0) {
    await sleep(250);
  }
  return () => rm(EXCLUSIVE, { force: true });
}

export const test = stack.extend({
  stackLock: [async ({}, use) => {
    let release = await holdShared();
    await use({
      exclusive: async () => {
        release = await holdExclusive(release);
      },
    });
    await release();
  }, { auto: true }],

  /**
   * open(user?, options?) → {context, page, user}: a new context (the project's device, a fresh client address),
   * signed in as `user` when given. Its pages fail the test on CSP violations, page errors and dialogs.
   */
  open: async ({ browser, baseURL }, use, testInfo) => {
    const contexts = [];
    const problems = [];
    const open = async (user = null, options = {}) => {
      const context = await browser.newContext({
        ...deviceOptions(testInfo.project),
        baseURL,
        ignoreHTTPSErrors: true,
        extraHTTPHeaders: { 'X-Test-Client-Ip': clientIp() },
        ...options,
      });
      contexts.push(context);
      await watch(context, problems);
      if (user) {
        await signIn(context.request, user);
      }
      return { context, page: await context.newPage(), user };
    };
    await use(open);
    await Promise.all(contexts.map((context) => context.close()));
    expect(problems).toEqual([]);
  },
});

/** Records CSP violations (§j security rule), uncaught page errors and any dialog (the XSS corpus must open none). */
export async function watch(context, problems) {
  await context.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`);
    });
  });
  context.on('console', (message) => {
    if (message.type() === 'error' && message.text().startsWith('CSP violation')) {
      problems.push(message.text());
    }
  });
  context.on('weberror', (error) => problems.push(`page error: ${error.error().message}`));
  context.on('dialog', async (dialog) => {
    problems.push(`dialog: ${dialog.type()} ${dialog.message()}`);
    await dialog.dismiss().catch(() => {});
  });
}

/** A team owned by `owner` (signed in on `request`), with `members` joined through a link invite. */
export async function createTeam(owner, members = [], name = `Team ${randomUUID().slice(0, 8)}`) {
  const created = await owner.post('/api/teams', json({ name }));
  expect(created.status(), await created.text()).toBe(201);
  const team = { ...(await created.json()), name };
  if (members.length > 0) {
    const invite = await owner.post(`/api/teams/${team.id}/invites`, json({ kind: 'link', expiresInDays: 1 }));
    expect(invite.status(), await invite.text()).toBe(201);
    const token = new URL((await invite.json()).url).hash.replace('#t=', '');
    for (const member of members) {
      const accepted = await member.post('/api/invites/accept', json({ token }));
      expect(accepted.ok(), await accepted.text()).toBe(true);
    }
  }
  return team;
}

export async function connectJira(request, teamId, { projectKey = 'ACME', boardId = 1 } = {}) {
  const response = await request.put(`/api/teams/${teamId}/jira`, json({
    siteHost: 'acme.atlassian.net',
    email: 'bot@acme.test',
    token: 'ATATT-fake-token-1234',
    projectKey,
    boardId,
  }));
  expect(response.ok(), await response.text()).toBe(true);
}

export async function createPoker(request, teamId = null) {
  const response = teamId
    ? await request.post(`/api/teams/${teamId}/poker-sessions`, json({}))
    : await request.post('/api/poker', json({}));
  expect(response.ok(), await response.text()).toBe(true);
  return response.json();
}

export async function createBoard(request, teamId = null, body = {}) {
  const response = teamId
    ? await request.post(`/api/teams/${teamId}/boards`, json({ title: 'Sprint retro', ...body }))
    : await request.post('/api/retro', json({ title: 'Quick retro', ...body }));
  expect(response.ok(), await response.text()).toBe(true);
  return response.json();
}
