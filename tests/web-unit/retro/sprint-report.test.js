import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { FakeEvent, installDom } from '../core/_dom.js';
import { EVERYONE, FOOTNOTE, NO_POINTS, PICK_KEY, TRUNCATED, UNASSIGNED, createSprintReport, fetchReport, viewModel } from '../../../web/src/retro/sprint-report.js';

const fixture = JSON.parse(await readFile(new URL('../../web-fake-server/fixtures/jira.json', import.meta.url), 'utf8')).sprintReport;
const SITE = 'fake-team.atlassian.net';
const day = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });
const on = (iso) => day.format(Date.parse(iso));

const EMPTY_REPORT = (id) => ({ sprintId: id, goal: null, pointsKnown: true, truncated: false, groups: ['done', 'doneAfter', 'notDone'].map((outcome) => ({ outcome, count: 0, points: 0, issues: [] })) });

// As the server answers: the active sprints and the newest `count` closed ones, extended down to the reported sprint.
function bodyFor(id = fixture.default, change = (report) => report, count = 4) {
  const closed = fixture.sprints.filter((s) => s.state === 'closed');
  const shown = Math.max(count, closed.findIndex((s) => s.id === Number(id)) + 1);
  return {
    sprints: [...fixture.sprints.filter((s) => s.state !== 'closed'), ...closed.slice(0, shown)],
    hasMoreSprints: closed.length > shown,
    empty: null,
    report: change(structuredClone(fixture.reports[id] ?? EMPTY_REPORT(Number(id)))),
  };
}

const params = (url) => new URL(url, 'http://x').searchParams;

let requests;
let replies;
let storage;
let teamCount = 0;

function reply(status, body) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

beforeEach(() => {
  requests = [];
  replies = [];
  storage = new Map();
  globalThis.fetch = async (url, init) => {
    requests.push(String(url));
    const next = replies.shift() ?? ((u) => reply(200, bodyFor(params(u).get('sprint') ?? undefined, undefined, Number(params(u).get('sprints')))));
    if (init.signal?.aborted) {
      throw Object.assign(new Error('aborted'), { name: 'AbortError' });
    }
    return next(String(url));
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: { getItem: (k) => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, String(v)), removeItem: (k) => storage.delete(k) },
  });
});

afterEach(() => {
  delete globalThis.fetch;
  delete globalThis.localStorage;
});

// Every test gets its own team, so the module's report cache never carries over.
const team = () => `team-${++teamCount}`;
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

test('a closed sprint: the picker, the heading with its close date, three tiles and the groups in order', () => {
  const model = viewModel(bodyFor(100), { siteHost: SITE });
  assert.equal(model.empty, null);
  assert.deepEqual(model.picker, [
    { value: '101', label: 'Sprint 9 (active)' },
    { value: '100', label: 'Sprint 8' },
    { value: '99', label: 'Sprint 7' },
    { value: '98', label: 'Sprint 6' },
    { value: '97', label: 'Sprint 5' },
  ]);
  assert.equal(model.selected, '100');
  assert.deepEqual(model.heading, {
    name: 'Sprint 8',
    goal: 'Take card payments end to end',
    dates: `${on('2026-09-28T09:00:00.000Z')} – ${on('2026-10-12T09:00:00.000Z')} · closed ${on('2026-10-12T10:30:00.000Z')}`,
  });
  assert.deepEqual(model.tiles, [
    { outcome: 'done', label: 'Done', count: 3, points: '5 pts' },
    { outcome: 'doneAfter', label: 'Done after close', count: 1, points: '5 pts' },
    { outcome: 'notDone', label: 'Not done', count: 3, points: '8 pts' },
  ]);
  assert.deepEqual(model.groups.map((g) => [g.outcome, g.title, g.rows.map((r) => r.key)]), [
    ['done', 'Done', ['RS-8', 'RS-9', 'RS-10']],
    ['doneAfter', 'Done after close', ['RS-11']],
    ['notDone', 'Not done', ['RS-12', 'RS-13', 'RS-7']],
  ]);
  assert.deepEqual(model.notes, [FOOTNOTE]);
});

test('rows link to the issue in Jira and carry the points, status and where unfinished work went', () => {
  const rows = viewModel(bodyFor(100), { siteHost: SITE }).groups.flatMap((g) => g.rows);
  const byKey = Object.fromEntries(rows.map((r) => [r.key, r]));
  assert.deepEqual(byKey['RS-12'], {
    key: 'RS-12',
    href: 'https://fake-team.atlassian.net/browse/RS-12',
    summary: 'Pay with a saved card',
    issueType: 'Story',
    status: 'To Do',
    statusCategory: 'new',
    assignee: null,
    points: null,
    chips: [{ kind: 'carried', text: 'Carried in' }, { kind: 'next', text: '→ Sprint 9' }],
  });
  assert.deepEqual(byKey['RS-7'].chips, [{ kind: 'backlog', text: '→ Backlog' }]);
  assert.deepEqual(byKey['RS-11'].chips, [], 'done after close, and not in a later sprint');
  assert.deepEqual(byKey['RS-9'].chips, [{ kind: 'carried', text: 'Carried in' }], 'done work went nowhere');
  assert.equal(byKey['RS-8'].points, '3 pts');
  const sprint7 = viewModel(bodyFor(99), { siteHost: SITE }).groups.flatMap((g) => g.rows);
  assert.deepEqual(sprint7.find((r) => r.key === 'RS-9').chips, [{ kind: 'next', text: '→ Sprint 8' }], 'done after close, in the next sprint');
  assert.equal(viewModel(bodyFor(100), { siteHost: null }).groups[0].rows[0].href, null);
});

test('an active sprint has no done-after outcome and no next sprint or backlog chips', () => {
  const model = viewModel(bodyFor(101), { siteHost: SITE });
  assert.deepEqual(model.tiles.map((t) => t.outcome), ['done', 'notDone']);
  assert.deepEqual(model.groups.map((g) => g.outcome), ['done', 'notDone']);
  assert.equal(model.heading.goal, null);
  assert.match(model.heading.dates, / · active$/);
  assert.deepEqual(model.groups.flatMap((g) => g.rows).map((r) => r.chips.map((c) => c.kind)), [[], ['carried'], ['carried']]);
});

test('without a points field every points value is hidden and a note says why', () => {
  const model = viewModel(bodyFor(100, (r) => ({ ...r, pointsKnown: false })), { siteHost: SITE });
  assert.ok(model.tiles.every((t) => t.points === null));
  assert.ok(model.groups.flatMap((g) => g.rows).every((r) => r.points === null));
  assert.deepEqual(model.notes, [NO_POINTS, FOOTNOTE]);
});

test('a truncated sprint says so; empty groups keep their tile and lose their section', () => {
  const model = viewModel(bodyFor(99, (r) => ({ ...r, truncated: true })), { siteHost: SITE });
  assert.deepEqual(model.notes, [TRUNCATED, FOOTNOTE]);
  const empty = viewModel(bodyFor(101, (r) => ({ ...r, groups: r.groups.map((g) => ({ ...g, count: 0, points: 0, issues: [] })) })), { siteHost: SITE });
  assert.deepEqual(empty.tiles.map((t) => t.count), [0, 0]);
  assert.deepEqual(empty.groups, []);
});

test('no board and no sprints are empty states without a report', () => {
  for (const empty of ['no_board', 'no_sprints']) {
    const model = viewModel({ sprints: [], empty, report: null }, { siteHost: SITE });
    assert.equal(model.empty, empty);
    assert.deepEqual([model.picker, model.tiles, model.groups, model.notes, model.selected], [[], [], [], [], null]);
  }
});

test('reports are fetched once per minute per team and sprint, shared in flight, and dropped on failure', async () => {
  const id = team();
  const [a, b] = await Promise.all([fetchReport(id, null), fetchReport(id, null)]);
  assert.equal(a, b);
  assert.deepEqual(requests, [`/api/teams/${id}/jira/sprint-report?sprints=4`]);
  await fetchReport(id, 100);
  assert.equal(requests.length, 1, 'the default sprint is also cached under its own id');
  await fetchReport(id, 101);
  assert.deepEqual(requests.slice(1), [`/api/teams/${id}/jira/sprint-report?sprint=101&sprints=4`]);
  await fetchReport(id, 101, { force: true });
  assert.equal(requests.length, 3);

  replies.push(() => reply(404, { code: 'jira_not_found' }));
  await assert.rejects(fetchReport(id, 99), (error) => error.code === 'jira_not_found');
  await fetchReport(id, 99);
  assert.equal(requests.length, 5, 'a failed fetch is not cached');
});

test('a refresh of the default sprint also replaces its default entry, so reopening shows the refreshed report', async () => {
  const id = team();
  assert.equal((await fetchReport(id, null)).report.goal, 'Take card payments end to end');
  replies.push(() => reply(200, bodyFor(100, (r) => ({ ...r, goal: 'Refreshed' }))));
  await fetchReport(id, 100, { force: true });
  assert.equal((await fetchReport(id, null)).report.goal, 'Refreshed');
  assert.equal((await fetchReport(id, 100)).report.goal, 'Refreshed');
  assert.equal(requests.length, 2);
});

function panel(fields = {}) {
  const dock = document.createElement('div');
  document.body.append(dock);
  let closed = 0;
  const report = createSprintReport({ teamId: team(), boardId: 'board-1', siteHost: SITE, dock, onClose: () => closed++, ...fields });
  return { report, dock, closed: () => closed };
}

test('the panel opens beside the board with the default sprint, and Escape closes it', async () => {
  const doc = installDom();
  const { report, dock, closed } = panel();
  report.toggle();
  assert.equal(report.isOpen, true);
  assert.ok(dock.classList.contains('has-side-sheet'));
  assert.match(doc.querySelector('.retro-sprint-status').textContent, /Loading/);
  await settle();
  assert.equal(doc.querySelector('.retro-sprint-picker').textContent, 'Sprint 8');
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-tile').map((t) => t.dataset.outcome), ['done', 'doneAfter', 'notDone']);
  assert.equal(doc.querySelectorAll('.retro-sprint-row').length, 7);
  assert.equal(doc.querySelector('.retro-sprint-key').getAttribute('href'), 'https://fake-team.atlassian.net/browse/RS-8');

  const sheet = doc.querySelector('.retro-sprint-sheet');
  sheet.dispatchEvent(new FakeEvent('keydown', { key: 'Escape' }));
  assert.equal(report.isOpen, false);
  assert.ok(!dock.classList.contains('has-side-sheet'));
  assert.equal(closed(), 1);
  report.destroy();
});

test('Jira text is drawn as text, never as markup', async () => {
  const doc = installDom();
  const evil = '<img src=x onerror="alert(1)">';
  replies.push(() => reply(200, bodyFor(100, (r) => {
    r.goal = evil;
    r.groups[0].issues[0].summary = evil;
    r.groups[0].issues[0].assignee = evil;
    return r;
  })));
  const { report } = panel();
  report.toggle();
  await settle();
  assert.equal(doc.querySelector('.retro-sprint-summary').textContent, evil);
  assert.equal(doc.querySelector('.retro-sprint-assignee').textContent, evil);
  assert.match(doc.querySelector('.retro-sprint-goal').textContent, /<img src=x/);
  report.destroy();
});

test('a failed load says why and Try again loads it again', async () => {
  const doc = installDom();
  replies.push(() => reply(409, { code: 'jira_not_configured' }));
  const { report } = panel();
  report.toggle();
  await settle();
  const alert = doc.querySelector('.retro-sprint-status');
  assert.equal(alert.getAttribute('role'), 'alert');
  assert.equal(alert.textContent, 'Jira is not set up for this team.');
  doc.querySelector('.retro-sprint-content').querySelector('button').dispatchEvent(new FakeEvent('click'));
  await settle();
  assert.equal(doc.querySelectorAll('.retro-sprint-row').length, 7);
  assert.equal(requests.length, 2);
  report.destroy();
});

test('a missing board or sprint is worded for the report, not as a missing issue', async () => {
  const doc = installDom();
  replies.push(() => reply(404, { code: 'jira_not_found' }));
  const { report } = panel();
  report.toggle();
  await settle();
  assert.equal(doc.querySelector('.retro-sprint-status').textContent,
    'Jira could not find the team’s board or that sprint. A team admin can check the board in Team Settings.');
  report.destroy();
});

test('Refresh during a pending pick reloads the picked sprint, not the one still shown', async (t) => {
  const doc = installDom();
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  globalThis.cancelAnimationFrame = () => {};
  t.after(() => {
    delete globalThis.window;
    delete globalThis.cancelAnimationFrame;
  });
  const { report } = panel();
  report.toggle();
  await settle();
  assert.equal(doc.querySelector('.retro-sprint-picker').textContent, 'Sprint 8');

  let release;
  const held = new Promise((resolve) => { release = resolve; });
  replies.push(async (url) => {
    await held;
    return reply(200, bodyFor(params(url).get('sprint')));
  });
  doc.querySelector('.retro-sprint-picker').click();
  doc.querySelectorAll('[role=menuitemradio]').find((item) => item.dataset.id === '101').click();
  assert.match(doc.querySelector('.retro-sprint-status').textContent, /Loading/);
  doc.querySelector('[aria-label=Refresh]').click();
  await settle();
  release();
  await settle();

  assert.deepEqual(requests.slice(1).map((url) => params(url).get('sprint')), ['101', '101']);
  assert.equal(doc.querySelector('.retro-sprint-picker').textContent, 'Sprint 9 (active)');
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-tile').map((t) => t.dataset.outcome), ['done', 'notDone']);
  assert.equal(storage.get(`${PICK_KEY}:board-1`), '101');
  report.destroy();
});

test('the picked sprint is remembered per board as its id, and one that is gone falls back to the default', async () => {
  installDom();
  storage.set(`${PICK_KEY}:board-1`, '101');
  const first = panel();
  first.report.toggle();
  await settle();
  assert.match(requests[0], /\?sprint=101&sprints=4$/);
  first.report.destroy();

  storage.set(`${PICK_KEY}:board-1`, '55');
  replies.push(() => reply(404, { code: 'jira_not_found' }));
  const second = panel();
  second.report.toggle();
  await settle();
  await settle();
  assert.match(requests[1], /\?sprint=55&sprints=4$/);
  assert.match(requests[2], /sprint-report\?sprints=4$/);
  assert.equal(storage.has(`${PICK_KEY}:board-1`), false);
  assert.equal(document.querySelectorAll('.retro-sprint-row').length, 7);
  second.report.destroy();
});

test('an empty board shows its message and no picker', async () => {
  const doc = installDom();
  replies.push(() => reply(200, { sprints: [], empty: 'no_sprints', report: null }));
  const { report } = panel();
  report.toggle();
  await settle();
  assert.equal(doc.querySelector('.retro-sprint-picker').hidden, true);
  assert.equal(doc.querySelector('.retro-sprint-status').textContent, 'The team’s Jira board has no active or closed sprints yet.');
  report.destroy();
});

test('the person filter lists the assignees by name with their counts, then the unassigned issues', () => {
  const model = viewModel(bodyFor(100), { siteHost: SITE });
  assert.deepEqual(model.people, [
    { value: EVERYONE, label: 'Everyone' },
    { value: '@Ada Fake', label: 'Ada Fake · 3' },
    { value: '@ben Example', label: 'ben Example · 1' },
    { value: '@Grace Fixture', label: 'Grace Fixture · 2' },
    { value: UNASSIGNED, label: 'Unassigned · 1' },
  ]);
  assert.equal(model.person, EVERYONE);
  assert.deepEqual(viewModel(bodyFor(99, (r) => ({ ...r, groups: r.groups.map((g) => ({ ...g, issues: g.issues.map((i) => ({ ...i, assignee: 'Ada Fake' })) })) })), { siteHost: SITE }).people.map((p) => p.label),
    ['Everyone', 'Ada Fake · 3']);
  assert.deepEqual(viewModel({ sprints: [], empty: 'no_sprints', report: null }, { siteHost: SITE }).people, []);
});

test('a person keeps their issues in every group and recounts the tiles and points from them', () => {
  const ada = viewModel(bodyFor(100), { siteHost: SITE, person: '@Ada Fake' });
  assert.equal(ada.person, '@Ada Fake');
  assert.deepEqual(ada.tiles.map((t) => [t.outcome, t.count, t.points]), [['done', 1, '3 pts'], ['doneAfter', 1, '5 pts'], ['notDone', 1, '5 pts']]);
  assert.deepEqual(ada.groups.map((g) => [g.outcome, g.rows.map((r) => r.key)]), [['done', ['RS-8']], ['doneAfter', ['RS-11']], ['notDone', ['RS-13']]]);

  const nobody = viewModel(bodyFor(100), { siteHost: SITE, person: UNASSIGNED });
  assert.deepEqual(nobody.tiles.map((t) => [t.count, t.points]), [[0, '0 pts'], [0, '0 pts'], [1, '0 pts']]);
  assert.deepEqual(nobody.groups.map((g) => [g.outcome, g.rows.map((r) => r.key)]), [['notDone', ['RS-12']]]);

  const hidden = viewModel(bodyFor(100, (r) => ({ ...r, pointsKnown: false })), { siteHost: SITE, person: '@Grace Fixture' });
  assert.deepEqual(hidden.tiles.map((t) => [t.count, t.points]), [[1, null], [0, null], [1, null]]);
});

test('a person who is not in the report means everyone, with the server\'s own counts', () => {
  const model = viewModel(bodyFor(101), { siteHost: SITE, person: '@ben Example' });
  assert.equal(model.person, EVERYONE);
  assert.deepEqual(model.tiles.map((t) => [t.count, t.points]), [[1, '2 pts'], [2, '5 pts']]);
});

function withPopovers(t) {
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  globalThis.requestAnimationFrame = () => 0;
  globalThis.cancelAnimationFrame = () => {};
  t.after(() => {
    delete globalThis.window;
    delete globalThis.requestAnimationFrame;
    delete globalThis.cancelAnimationFrame;
  });
}

const sprintItems = (doc) => doc.querySelectorAll('[role=menuitemradio]');
const moreButton = (doc) => doc.querySelector('.retro-sprint-more');

test('the sprint menu opens on the checked sprint and Load more asks for 8 more closed sprints for the same sprint', async (t) => {
  const doc = installDom();
  withPopovers(t);
  const { report } = panel();
  report.toggle();
  await settle();
  const picker = doc.querySelector('.retro-sprint-picker');
  assert.equal(picker.getAttribute('aria-haspopup'), 'menu');
  picker.click();
  assert.equal(picker.getAttribute('aria-expanded'), 'true');
  assert.deepEqual(sprintItems(doc).map((i) => [i.textContent, i.getAttribute('aria-checked')]),
    [['Sprint 9 (active)', 'false'], ['Sprint 8', 'true'], ['Sprint 7', 'false'], ['Sprint 6', 'false'], ['Sprint 5', 'false']]);
  assert.equal(doc.activeElement, sprintItems(doc)[1]);
  assert.equal(moreButton(doc).hidden, false);

  moreButton(doc).focus();
  moreButton(doc).click();
  assert.equal(moreButton(doc).getAttribute('aria-disabled'), 'true');
  moreButton(doc).click();
  await settle();
  assert.deepEqual(requests.slice(1).map((url) => [params(url).get('sprint'), params(url).get('sprints')]), [['100', '12']], 'one request while one is in flight');
  assert.equal(sprintItems(doc).length, 12);
  assert.equal(moreButton(doc).hidden, true);
  assert.equal(doc.activeElement, sprintItems(doc)[5], 'focus lands on the first sprint it added');
  assert.equal(doc.querySelectorAll('.retro-sprint-row').length, 7, 'the report stays as it was');

  sprintItems(doc).find((i) => i.textContent === 'Pilot').click();
  await settle();
  assert.equal(picker.getAttribute('aria-expanded'), 'false');
  assert.equal(doc.activeElement, picker);
  assert.deepEqual([params(requests.at(-1)).get('sprint'), params(requests.at(-1)).get('sprints')], ['90', '12']);
  assert.equal(picker.textContent, 'Pilot');
  assert.equal(doc.querySelector('.retro-sprint-empty').textContent, 'No issues in this sprint.');
  report.destroy();
});

test('scrolling to the bottom of the sprint menu loads more, and a shorter cached list does not shrink it', async (t) => {
  const doc = installDom();
  withPopovers(t);
  const { report } = panel();
  report.toggle();
  await settle();
  doc.querySelector('.retro-sprint-picker').click();
  const menu = doc.querySelector('.retro-sprint-menu');
  Object.assign(menu, { scrollTop: 0, clientHeight: 200, scrollHeight: 400 });
  menu.dispatchEvent(new FakeEvent('scroll'));
  assert.equal(requests.length, 1, 'not near the bottom yet');
  menu.scrollTop = 190;
  menu.dispatchEvent(new FakeEvent('scroll'));
  await settle();
  assert.equal(params(requests[1]).get('sprints'), '12');
  assert.equal(sprintItems(doc).length, 12);

  sprintItems(doc).find((i) => i.textContent === 'Sprint 7').click();
  await settle();
  assert.equal(params(requests[2]).get('sprints'), '12');
  doc.querySelector('.retro-sprint-picker').click();
  sprintItems(doc).find((i) => i.textContent === 'Sprint 8').click();
  await settle();
  assert.equal(requests.length, 3, 'the report fetched with 4 sprints is reused');
  assert.equal(sprintItems(doc).length, 12);
  report.destroy();
});

test('a closed panel starts again at 4 sprints and everyone', async (t) => {
  const doc = installDom();
  withPopovers(t);
  const { report } = panel();
  report.toggle();
  await settle();
  doc.querySelector('.retro-sprint-picker').click();
  moreButton(doc).click();
  await settle();
  report.toggle();
  report.toggle();
  await settle();
  doc.querySelector('.retro-sprint-picker').click();
  assert.equal(sprintItems(doc).length, 12, 'the cached report still lists 12');
  assert.equal(doc.querySelector('.retro-sprint-person').textContent, 'Everyone');
  report.destroy();
});

test('the person stays chosen across sprints where they have issues, and resets to everyone where they do not', async (t) => {
  const doc = installDom();
  withPopovers(t);
  const { report } = panel();
  report.toggle();
  await settle();
  const person = () => doc.querySelector('.retro-sprint-person');
  person().click();
  doc.querySelectorAll('[role=menuitem]').find((i) => i.dataset.action === '@Ada Fake').click();
  assert.equal(person().textContent, 'Ada Fake · 3');
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-row').map((r) => r.dataset.key), ['RS-8', 'RS-11', 'RS-13']);
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-count').map((c) => c.textContent), ['1', '1', '1']);
  assert.equal(requests.length, 1, 'filtering needs no request');

  doc.querySelector('.retro-sprint-picker').click();
  sprintItems(doc).find((i) => i.textContent === 'Sprint 9 (active)').click();
  await settle();
  assert.equal(person().textContent, 'Ada Fake · 1');
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-row').map((r) => r.dataset.key), ['RS-13']);

  person().click();
  doc.querySelectorAll('[role=menuitem]').find((i) => i.dataset.action === UNASSIGNED).click();
  doc.querySelector('.retro-sprint-picker').click();
  sprintItems(doc).find((i) => i.textContent === 'Sprint 7').click();
  await settle();
  assert.equal(person().textContent, 'Unassigned · 1');
  assert.deepEqual(doc.querySelectorAll('.retro-sprint-row').map((r) => r.dataset.key), ['RS-12']);

  const filter = person();
  doc.querySelector('.retro-sprint-picker').click();
  sprintItems(doc).find((i) => i.textContent === 'Sprint 6').click();
  await settle();
  assert.equal(filter.isConnected, false, 'a sprint without issues has no filter');
  doc.querySelector('.retro-sprint-picker').click();
  sprintItems(doc).find((i) => i.textContent === 'Sprint 7').click();
  await settle();
  assert.equal(person().textContent, 'Everyone');
  report.destroy();
});
