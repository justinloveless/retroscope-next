// Shared setup for specs against deploy/compose.test.yml: Traefik's self-signed https front, Mailpit, the stack's
// database (psql in its db container), and a fresh client address per test so the server's per-IP limits (5 signups
// an hour) never couple tests together. The stack trusts X-Test-Client-Ip from its proxy the way production trusts
// CF-Connecting-IP behind Cloudflare.
import { execFile } from 'node:child_process';
import { randomInt, randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { expect, test as base } from '@playwright/test';

export const BASE = process.env.RS_E2E_BASE_URL?.startsWith('https:')
  ? process.env.RS_E2E_BASE_URL
  : `https://localhost:${process.env.RS_TEST_HTTPS_PORT ?? 18443}`;
export const MAILPIT = `http://localhost:${process.env.RS_TEST_MAILPIT_PORT ?? 18025}`;
export const PASSWORD = 'correct horse battery staple';

export const clientIp = () => `10.${randomInt(256)}.${randomInt(256)}.${randomInt(1, 255)}`;

const run = promisify(execFile);
const ROOT = fileURLToPath(new URL('../../../', import.meta.url));
const COMPOSE = ['compose', '-f', `${ROOT}deploy/compose.test.yml`];

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

export const test = base.extend({
  baseURL: async ({}, use) => use(BASE),
  ignoreHTTPSErrors: async ({}, use) => use(true),
  extraHTTPHeaders: async ({}, use) => use({ 'X-Test-Client-Ip': clientIp() }),
  // Any CSP violation or uncaught page error fails the test (§j security rule).
  page: async ({ page }, use) => {
    const problems = [];
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) => {
        console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`);
      });
    });
    page.on('console', (message) => {
      if (message.type() === 'error' && message.text().startsWith('CSP violation')) problems.push(message.text());
    });
    page.on('pageerror', (error) => problems.push(`page error: ${error.message}`));
    await use(page);
    expect(problems).toEqual([]);
  },
});

export { expect };

const json = (data) => ({ data, headers: { 'X-RS': '1', Origin: BASE } });

/** Signs a new user up and in on the context behind `api` (a page's request shares its cookies). */
export async function signUp(api, name) {
  const email = `e2e-${randomUUID()}@example.test`;
  const signup = await api.post('/api/auth/signup', json({ email, password: PASSWORD, fullName: name }));
  expect(signup.status()).toBe(202);
  const login = await api.post('/api/auth/login', json({ email, password: PASSWORD }));
  expect(login.status()).toBe(200);
  return { email, name };
}

/** The token of the newest mail to `email` whose link path is `path` (e.g. /auth/verify), from Mailpit's API. */
export async function mailedToken(api, email, path) {
  const pattern = new RegExp(`${path}#t=([A-Za-z0-9_-]+)`);
  let token = null;
  await expect.poll(async () => {
    const list = await (await api.get(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json();
    for (const summary of list.messages ?? []) {
      const message = await (await api.get(`${MAILPIT}/api/v1/message/${summary.ID}`)).json();
      token = pattern.exec(`${message.Text ?? ''} ${message.HTML ?? ''}`)?.[1] ?? token;
    }
    return token;
  }, { timeout: 20_000 }).not.toBeNull();
  return token;
}

/** Signs up and verifies through the mailed link, so the account may create teams. */
export async function verifiedUser(api, name) {
  const user = await signUp(api, name);
  const token = await mailedToken(api, user.email, '/auth/verify');
  const verify = await api.post('/api/auth/verify/consume', json({ token }));
  expect(verify.ok()).toBe(true);
  return user;
}

export { json };

/** A link invite to `teamId`, made by one of its admins signed in on `admin`; returns the token in the link's fragment. */
export async function inviteLink(admin, teamId) {
  const invite = await admin.post(`/api/teams/${teamId}/invites`, json({ kind: 'link', expiresInDays: 1 }));
  expect(invite.status(), await invite.text()).toBe(201);
  return new URL((await invite.json()).url).hash.replace('#t=', '');
}

/**
 * Adds `members` (each a signed-in request) to `team` through a link invite, the only way into a team (UX18). The
 * token is kept on `team.invite`, so later members join through the same link.
 */
export async function addMembers(admin, team, members) {
  team.invite ??= await inviteLink(admin, team.id);
  for (const member of members) {
    const accepted = await member.post('/api/invites/accept', json({ token: team.invite }));
    expect(accepted.ok(), await accepted.text()).toBe(true);
  }
}

/** A team owned by `owner` (signed in on `request`), with `members` joined through a link invite. */
export async function createTeam(owner, members = [], name = `Team ${randomUUID().slice(0, 8)}`) {
  const created = await owner.post('/api/teams', json({ name }));
  expect(created.status(), await created.text()).toBe(201);
  const team = { ...(await created.json()), name };
  if (members.length > 0) {
    await addMembers(owner, team, members);
  }
  return team;
}

/** A poker session of `teamId`, made by a member signed in on `request`. Every session belongs to a team (UX18). */
export async function createPoker(request, teamId) {
  const response = await request.post(`/api/teams/${teamId}/poker-sessions`, json({}));
  expect(response.ok(), await response.text()).toBe(true);
  return response.json();
}
