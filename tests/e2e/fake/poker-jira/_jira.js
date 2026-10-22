// Shared setup for the poker Jira specs: a team poker session (the poker-table setup), the team's Jira
// configuration, U48's fake Jira proxy, a browse route that implements the filters this unit sends, and
// a scripted server for the round commands the panel and the drawer send.
import { expect } from '../_harness/fixtures.js';
import { ME, R1, ROOM, round, setupPoker, snapshot } from '../poker-table/_poker.js';

export const TEAM = snapshot().teamId;
export const OTHER_TEAM = '0a000000-0000-4000-8000-0000000000b9';
export const SITE = 'fake-team.atlassian.net';

const SPRINT_STATES = { board: ['active', 'future'], open: ['active', 'future'], all: null };

/** The browse filters as DESIGN §f's JQL applies them: status category, points, sprint scope, text. */
function browse(jira, query) {
  const text = (query.get('q') ?? '').trim().toLowerCase();
  const status = query.get('status') ?? 'not-done';
  const points = query.get('points') ?? 'any';
  const states = SPRINT_STATES[query.get('scope') ?? 'board'];
  const keep = (issue) => issue.issueType !== 'Epic'
    && (!text || issue.key.toLowerCase().includes(text) || issue.summary.toLowerCase().includes(text))
    && (status === 'all' || (status === 'not-done' ? issue.statusCategory !== 'done' : issue.statusCategory === status))
    && (points === 'any' || (points === 'unpointed' ? issue.points == null : issue.points === Number(points)));
  const row = (issue) => ({
    key: issue.key,
    summary: issue.summary,
    issueType: issue.issueType,
    status: issue.status,
    statusCategory: issue.statusCategory,
    priority: issue.priority ?? null,
    points: issue.points ?? null,
    parentKey: issue.parentKey ?? null,
    parentSummary: issue.parentKey ? jira.issues.get(issue.parentKey)?.summary ?? null : null,
    roundState: null,
  });
  const all = [...jira.issues.values()].filter(keep);
  const buckets = jira.sprints
    .filter((s) => !states || states.includes(s.state))
    .map((s) => ({ id: String(s.id), kind: 'sprint', name: s.name, state: s.state, startDate: s.startDate, issues: all.filter((i) => i.sprintId === s.id).map(row) }));
  buckets.push({ id: 'backlog', kind: 'backlog', name: 'Backlog', state: null, startDate: null, issues: all.filter((i) => i.sprintId == null).map(row) });
  return { buckets, partial: false };
}

/**
 * A team poker session with Jira. Returns {jira, rounds, browseQueries}: the fake Jira control, the
 * rounds the scripted server holds, and every browse query string the page sent.
 */
export function setupJira(fake, {
  viewer = ME,
  role = 'member',
  membersCanEdit = true,
  rounds: initial = [round(R1, 1, { ticketKey: 'ABC-1' })],
  snap = {},
  overlay,
  fixture,
} = {}) {
  const rounds = initial.map((r) => ({ ...r }));
  setupPoker(fake, { viewer, snap: snapshot({ rounds, currentRoundId: rounds[0].id, ...snap }), ...(overlay ? { overlay } : {}) });
  fake.route('GET', `/api/teams/${TEAM}`, {
    body: {
      id: TEAM,
      name: 'Checkout team',
      description: '',
      role,
      jira: { configured: true, siteHost: SITE, projectKey: 'RS', boardId: 7, ticketPrefix: 'RS', status: 'ok', membersCanEdit },
      counts: { boards: 0, members: 4 },
    },
  });
  const jira = fake.jira({ teamId: TEAM, ...(fixture ? { fixture } : {}) });
  const browseQueries = [];
  fake.route('GET', '/api/teams/:teamId/jira/browse', ({ params, query }) => {
    browseQueries.push(Object.fromEntries(query));
    return params.teamId === TEAM ? { body: browse(jira, query) } : { status: 409, body: { code: 'jira_not_configured', status: 409 } };
  });

  let number = Math.max(...rounds.map((r) => r.number));
  const info = (r) => ({ ...r });
  const add = (roundId, ticketKey, extra = {}) => {
    const created = round(roundId, ++number, { ticketKey, ...extra });
    rounds.push(created);
    return created;
  };

  fake.onCommand('poker.round.create', (ctx) => {
    const { roundId, ticketKey, pending } = ctx.frame.d;
    const key = /^\d+$/.test(ticketKey) ? `RS-${ticketKey}` : ticketKey.toUpperCase();
    if (pending) {
      const created = add(roundId, key, { pending: true, pendingOwner: viewer.pid });
      ctx.commit([
        { k: 'poker.round.created', d: { round: info(created) } },
        { k: 'poker.pointer', d: { roundId, cause: 'create', nav: false } },
        { k: 'poker.spotlight', d: { holder: viewer.pid, roundId } },
      ]);
      return;
    }
    const created = add(roundId, key);
    ctx.commit([{ k: 'poker.round.created', d: { round: info(created) } }, { k: 'poker.pointer', d: { roundId, cause: 'create', nav: true } }]);
  });
  fake.onCommand('poker.round.createMany', (ctx) => {
    const created = ctx.frame.d.rounds.map((r) => add(r.roundId, r.ticketKey));
    ctx.commit([
      ...created.map((r) => ({ k: 'poker.round.created', d: { round: info(r) } })),
      { k: 'poker.pointer', d: { roundId: created.at(-1).id, cause: 'createMany', nav: true } },
    ]);
  });
  fake.onCommand('poker.round.commitPending', (ctx) => {
    const target = rounds.find((r) => r.id === ctx.frame.d.roundId);
    Object.assign(target, { pending: false, pendingOwner: undefined, version: target.version + 1 });
    delete target.pendingOwner;
    ctx.commit([{ k: 'poker.round.updated', d: { round: info(target) } }, { k: 'poker.pointer', d: { roundId: target.id, cause: 'commit', nav: true } }]);
  });
  fake.onCommand('poker.round.discardPending', (ctx) => {
    const at = rounds.findIndex((r) => r.id === ctx.frame.d.roundId);
    if (at < 0 || !rounds[at].pending) {
      ctx.nack('invalid', { msg: 'The round was already committed.' });
      return;
    }
    rounds.splice(at, 1);
    ctx.commit([{ k: 'poker.round.deleted', d: { roundId: ctx.frame.d.roundId } }]);
  });
  fake.onCommand('poker.jira.submitPoints', (ctx) => {
    const target = rounds.find((r) => r.id === ctx.frame.d.roundId);
    ctx.commit([{ k: 'poker.jira.points', d: { roundId: target.id, key: target.ticketKey, points: ctx.frame.d.points, status: 'pending', by: viewer.pid } }]);
  });
  return { jira, rounds, browseQueries };
}

/** Phones get the panels as sheets (neotro.css hides the side panels below 768 px). */
export const isPhone = (page) => page.viewportSize().width < 768;

/** Opens the poker page and waits until the Jira feature has read the team's configuration. */
export async function openTable(page, path) {
  await page.goto(path ?? `/teams/${TEAM}/poker/NEOTRO1234`);
  await page.locator('.nt-table').waitFor();
  await expect(page.locator('.jira-toggle')).toBeVisible();
}

/** Opens the poker page and the Browse Jira panel (a sheet on phones, the left panel on desktop). */
export async function openBrowse(page, path) {
  await openTable(page, path);
  const panel = page.locator('.jira-browse');
  if (isPhone(page)) {
    await page.locator('.jira-toggle').click();
  }
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('aria-busy', 'false');
  return panel;
}

export const row = (page, key) => page.locator(`.jira-row[data-key="${key}"]`);
export const bucket = (page, name) => page.locator('.jira-bucket').filter({ has: page.locator('.jira-bucket-name', { hasText: name }) });
export const drawer = (page) => page.locator('.jira-drawer');
/** The viewed round's issue at the centre of the stage. */
export const issueView = (page) => page.locator('.jira-issue');

/** The last command of kind `k` the page sent, waiting for it to arrive. */
export async function sent(fake, k) {
  await fake.waitForFrame((f) => f.t === 'cmd' && f.k === k);
  return [...fake.received].reverse().find(({ frame }) => frame.t === 'cmd' && frame.k === k).frame;
}

export const commands = (fake, k) => fake.received.filter(({ frame }) => frame.t === 'cmd' && frame.k === k).map(({ frame }) => frame);

/** Requests the page made to the fake Jira proxy, e.g. jiraRequests(fake, 'GET', /\/issues\/RS-12$/). */
export const jiraRequests = (fake, method, path) => fake.requests.filter((r) => r.method === method && path.test(r.path));

export { ME, R1, ROOM, round };
