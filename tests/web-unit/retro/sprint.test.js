import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from '../core/_dom.js';
import { sprintFeature } from '../../../web/src/retro/sprint.js';

const TEAM = '7e000000-0000-4000-8000-000000000001';
const ME = { pid: 'a1000000-0000-4000-8000-000000000001', kind: 'user', name: 'Alice' };

let requests;
let teamJira;

beforeEach(() => {
  installDom();
  requests = [];
  teamJira = { configured: true, status: 'ok', siteHost: 'acme.atlassian.net', boardId: 7, membersCanEdit: true };
  globalThis.fetch = async (url) => {
    requests.push(String(url));
    const body = String(url).endsWith(`/api/teams/${TEAM}`)
      ? { id: TEAM, role: 'member', jira: teamJira }
      : { sprints: [], empty: 'no_sprints', report: null };
    return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } });
  };
  const storage = new Map();
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: { getItem: (k) => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, String(v)), removeItem: (k) => storage.delete(k) },
  });
});

afterEach(() => {
  delete globalThis.fetch;
  delete globalThis.localStorage;
});

function boardView(fields = {}) {
  return { teamId: TEAM, members: [{ pid: ME.pid, name: ME.name, role: 'member' }], ...fields };
}

function mount(view, me = ME) {
  const rooms = [];
  const ctx = {
    boardId: 'b0000000-0000-4000-8000-0000000000b1',
    me: () => me,
    view: () => view,
    socket: {
      room(key) {
        rooms.push(key);
        return { view: null, select: () => () => {}, send: () => null };
      },
      leave() {},
    },
  };
  const board = document.createElement('div');
  board.className = 'retro-board';
  const toolbar = document.createElement('div');
  board.append(toolbar);
  document.body.append(board);
  const feature = sprintFeature.board(ctx, { toolbar });
  return { feature, rooms, board, button: toolbar.querySelector('.retro-sprint-open') };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

test('a member of a team whose Jira has a board gets the button, and nothing is fetched for the report until a click', async () => {
  const { feature, rooms, button } = mount(boardView());
  assert.equal(button.hidden, true, 'hidden until the team answers');
  await settle();
  assert.equal(button.hidden, false);
  assert.equal(button.textContent, 'Sprint report');
  assert.deepEqual(rooms, [`team:${TEAM}`]);
  assert.deepEqual(requests, [`/api/teams/${TEAM}`]);

  button.dispatchEvent(new FakeEvent('click'));
  // The click loads the report's chunk first, which takes a few turns of the event loop.
  for (let turn = 0; turn < 20 && requests.length < 2; turn++) {
    await settle();
  }
  assert.deepEqual(requests, [`/api/teams/${TEAM}`, `/api/teams/${TEAM}/jira/sprint-report?sprints=4`]);
  assert.equal(button.getAttribute('aria-expanded'), 'true');
  assert.ok(document.querySelector('.retro-board').classList.contains('has-side-sheet'));
  feature.destroy();
  assert.equal(document.querySelector('.retro-sprint-sheet'), null);
});

test('quick boards, guests and non-members never see the button and send no request', async () => {
  const cases = [
    ['a quick board', boardView({ teamId: null, members: undefined }), ME],
    ['a non-member', boardView({ members: [{ pid: 'someone-else', name: 'Bob', role: 'owner' }] }), ME],
    ['a guest', boardView(), { pid: 'g0000000-0000-4000-8000-000000000009', kind: 'guest', name: 'Guest' }],
  ];
  for (const [name, view, me] of cases) {
    const { feature, rooms, button } = mount(view, me);
    await settle();
    assert.equal(button.hidden, true, name);
    assert.deepEqual(rooms, [], name);
    feature.destroy();
  }
  assert.deepEqual(requests, []);
});

test('a team without Jira, or with Jira but no board, hides the button after asking only for the team', async () => {
  for (const jira of [{ configured: false }, { ...teamJira, boardId: null }]) {
    teamJira = jira;
    requests = [];
    const { feature, button } = mount(boardView());
    await settle();
    assert.equal(button.hidden, true, JSON.stringify(jira));
    assert.deepEqual(requests, [`/api/teams/${TEAM}`]);
    feature.destroy();
  }
});

test('losing membership hides the button and closes an open report', async () => {
  let view = boardView();
  const { feature, button } = mount(view);
  await settle();
  button.dispatchEvent(new FakeEvent('click'));
  await settle();
  await settle();
  assert.ok(document.querySelector('.retro-sprint-sheet'));
  view = boardView({ members: [] });
  feature.update(view);
  assert.equal(button.hidden, true);
  assert.equal(document.querySelector('.retro-sprint-sheet'), null);
  assert.ok(!document.querySelector('.retro-board').classList.contains('has-side-sheet'));
  feature.destroy();
});
