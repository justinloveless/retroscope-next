import { after, beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDay } from '../../../web/src/core/day.js';
import { timeAgo } from '../../../web/src/core/fmt.js';
import { createPokerTab, lastActivity, progressText, sessionPath, splitLatest, topicsLine } from '../../../web/src/team/poker-list.js';
import { TEAM, fakeCtx, fakeRoom, settle, setupDom, text } from './_ctx.js';

const MIN = 60_000;
const DAY = 86_400_000;
const NOW = Date.now();
const S1 = '0192f0a0-0000-7000-8000-000000005001';
const S2 = '0192f0a0-0000-7000-8000-000000005002';
const S3 = '0192f0a0-0000-7000-8000-000000005003';

const session = (id, code, extra = {}) => ({ id, code, createdAt: NOW - DAY, pointed: 0, total: 0, parentKeys: [], ...extra });

function sessions() {
  return [
    session(S1, 'POKER00001', { createdAt: NOW - 2 * 60 * MIN, lastActivityAt: NOW - 3 * MIN, pointed: 4, total: 9, parentKeys: [{ key: 'PAY-1', count: 9 }] }),
    session(S2, 'POKER00002', { createdAt: NOW - 10 * DAY, pointed: 12, total: 12, parentKeys: [{ key: 'PAY-3', count: 6 }, { key: 'PAY-1', count: 6 }] }),
    session(S3, 'POKER00003', { createdAt: NOW - 17 * DAY, pointed: 1, total: 1, parentKeys: [{ key: null, count: 1 }] }),
  ];
}

let mounted = null;

beforeEach(() => {
  mounted?.destroy();
  mounted = null;
  setupDom();
});

// The section keeps a clock for "last activity"; destroying it stops the clock, so the run can end.
after(() => mounted?.destroy());

async function mount(options = {}) {
  const ctx = fakeCtx({ answers: { 'GET /poker-sessions': { items: sessions(), nextCursor: null } }, ...options });
  mounted = createPokerTab(ctx);
  document.body.append(mounted.el);
  await settle();
  return { ctx, el: mounted.el };
}

test('progress reads "4 of 9 stories pointed", shortened in rows, and says when there are no stories yet', () => {
  assert.equal(progressText(4, 9), '4 of 9 stories pointed');
  assert.equal(progressText(1, 1), '1 of 1 story pointed');
  assert.equal(progressText(12, 12, true), '12 of 12 pointed');
  assert.equal(progressText(0, 0), 'No stories yet');
});

test('the last activity is the newest of the list, a notice and the creation', () => {
  assert.equal(lastActivity({ createdAt: 5 }), 5);
  assert.equal(lastActivity({ createdAt: 5, lastActivityAt: 9 }), 9);
  assert.equal(lastActivity({ createdAt: 5, lastActivityAt: 9 }, 12), 12);
  assert.equal(lastActivity({ createdAt: 5, lastActivityAt: 9 }, 7), 9);
});

test('the newest session is the latest and the rest are earlier', () => {
  assert.deepEqual(splitLatest([]), { latest: null, earlier: [] });
  const [a, b, c] = sessions();
  assert.deepEqual(splitLatest([a, b, c]), { latest: a, earlier: [b, c] });
});

test('a row names its topics in one line', () => {
  assert.equal(topicsLine(null), '');
  assert.equal(topicsLine({ kind: 'various', tone: 1 }), 'various topics');
  assert.equal(topicsLine({ kind: 'badges', topics: [{ label: 'Wallet' }, { label: 'Checkout v2' }] }), 'Wallet · Checkout v2');
});

test('a session is addressed under its team', () => {
  assert.equal(sessionPath(TEAM, 'AB CD'), `/teams/${TEAM}/poker/AB%20CD`);
});

test('the section has its h1 and starts a session for any member', async () => {
  const { ctx, el } = await mount({
    answers: {
      'GET /poker-sessions': { items: sessions(), nextCursor: null },
      'POST /poker-sessions': { id: 'new', code: 'NEWCODE001' },
    },
  });
  assert.equal(text(el.querySelector('h1')), 'Poker');
  const start = el.querySelectorAll('button').find((b) => text(b) === 'Start poker session');
  assert.ok(start, 'Start poker session');
  start.click();
  await settle();
  assert.deepEqual(ctx.navigated, [sessionPath(TEAM, 'NEWCODE001')]);
});

test('the latest session is highlighted with its day, progress, last activity, topics and Join', async () => {
  const { el } = await mount();
  const latest = el.querySelector('.team-latest');
  assert.equal(latest.dataset.id, S1);
  assert.equal(text(latest.querySelector('.team-eyebrow')), 'Latest session');
  assert.equal(text(latest.querySelector('h2')), formatDay(NOW - 2 * 60 * MIN));
  assert.equal(text(latest.querySelector('.team-latest-progress')), `4 of 9 stories pointed · last activity ${timeAgo(NOW - 3 * MIN)}`);
  assert.deepEqual(latest.querySelectorAll('.team-topic').map(text), ['PAY-1']);
  const join = latest.querySelectorAll('a').find((a) => text(a) === 'Join');
  assert.equal(join.getAttribute('href'), sessionPath(TEAM, 'POKER00001'));
});

test('earlier sessions are rows that open the session, and nothing claims a session is live', async () => {
  const { el } = await mount();
  assert.equal(text(el.querySelectorAll('h2').find((h) => h.classList.contains('team-section-title'))), 'Earlier sessions');
  const rows = el.querySelectorAll('.team-session-row');
  assert.deepEqual(rows.map((r) => r.dataset.id), [S2, S3]);
  const link = rows[0].querySelector('.team-session-link');
  assert.equal(link.getAttribute('href'), sessionPath(TEAM, 'POKER00002'));
  assert.equal(text(link.querySelector('.team-session-day')), formatDay(NOW - 10 * DAY));
  assert.equal(text(link.querySelector('.team-session-progress')), '12 of 12 pointed');
  assert.equal(text(link.querySelector('.team-session-topics')), 'PAY-3 · PAY-1');
  assert.equal(text(rows[1].querySelector('.team-session-topics')), 'various topics');
  assert.doesNotMatch(text(el), /\b(live|in progress)\b/i);
});

test('only admins get a delete button on each session', async () => {
  const member = await mount();
  assert.equal(member.el.querySelectorAll('button').filter((b) => b.getAttribute('aria-label')?.startsWith('Delete')).length, 0);
  mounted.destroy();
  const admin = await mount({ admin: true });
  assert.equal(admin.el.querySelectorAll('button').filter((b) => b.getAttribute('aria-label')?.startsWith('Delete')).length, 3);
});

test('a progress notice updates the counts and marks the session as used just now', async () => {
  const room = fakeRoom({ poker: {} });
  const { el } = await mount({ room });
  room.set({ poker: { [S1]: { pointed: 5, total: 9 } } });
  const progress = text(el.querySelector('.team-latest-progress'));
  assert.equal(progress, `5 of 9 stories pointed · last activity ${timeAgo(Date.now())}`);
});

test('a notice for a session the list does not hold reloads the list', async () => {
  const room = fakeRoom({ poker: {} });
  const { ctx } = await mount({ room });
  const before = ctx.calls.filter((c) => c.path === '/poker-sessions').length;
  room.set({ poker: { 'unknown-session': { pointed: 0, total: 1 } } });
  await new Promise((resolve) => setTimeout(resolve, 350));
  assert.equal(ctx.calls.filter((c) => c.path === '/poker-sessions').length, before + 1);
});

test('with no sessions it says so, and shows no latest card or earlier list', async () => {
  const { el } = await mount({ answers: { 'GET /poker-sessions': { items: [], nextCursor: null } } });
  assert.equal(text(el.querySelector('.team-empty').querySelector('h3')), 'No poker sessions yet');
  assert.equal(el.querySelector('.team-latest'), null);
  assert.equal(el.querySelector('.team-session-rows').hidden, true);
});
