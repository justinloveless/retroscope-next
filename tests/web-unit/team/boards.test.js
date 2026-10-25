import { after, beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDate } from '../../../web/src/core/fmt.js';
import { boardMeta, boardPath, createBoardsTab, openCreateBoard } from '../../../web/src/team/boards.js';
import { fakeCtx, settle, setupDom, text } from './_ctx.js';

const T0 = Date.UTC(2026, 9, 10, 9);
const B1 = '0192f0a0-0000-7000-8000-0000000000b1';
const B2 = '0192f0a0-0000-7000-8000-0000000000b2';
const B3 = '0192f0a0-0000-7000-8000-0000000000b3';

const board = (id, title, extra = {}) => ({
  id, code: `CODE${id.slice(-2).toUpperCase()}AAAA`, title, archived: false, createdAt: T0,
  lastActivityAt: T0, stagesEnabled: false, stage: 'thinking', cardCount: 0, ...extra,
});

const active = () => [board(B1, 'Sprint 43 retro', { stagesEnabled: true, stage: 'discussing' }), board(B2, 'Sprint 42 retro', { stage: 'voting' })];
const archived = () => [board(B3, 'Sprint 38 retro', { archived: true, archivedAt: T0 + 86_400_000 })];

let mounted = null;

beforeEach(() => {
  mounted?.destroy();
  mounted = null;
  setupDom();
});

after(() => mounted?.destroy());

async function mount(options = {}) {
  const ctx = fakeCtx({
    answers: {
      'GET /boards': (body, path) => ({ items: path.includes('archived=true') ? archived() : active(), nextCursor: null }),
      'GET /templates': { items: [{ id: 'tpl-1', name: 'Start, stop, continue', isDefault: false }] },
    },
    room: { view: { boardsRev: 0 }, select: () => () => {} },
    ...options,
  });
  mounted = createBoardsTab(ctx);
  document.body.append(mounted.el);
  await settle();
  return { ctx, el: mounted.el };
}

const button = (root, name) => root.querySelectorAll('button').find((b) => text(b) === name || b.getAttribute('aria-label') === name);

test('a card says its date, when it was archived, and its stage only when the board uses stages', () => {
  assert.equal(boardMeta(board(B1, 'x')), formatDate(T0));
  assert.equal(boardMeta(board(B1, 'x', { stagesEnabled: true, stage: 'discussing' })), `${formatDate(T0)} · Discussing`);
  assert.equal(boardMeta(board(B1, 'x', { stagesEnabled: false, stage: 'closed' })), formatDate(T0));
  assert.equal(boardMeta(board(B1, 'x', { archived: true, archivedAt: T0, stagesEnabled: true, stage: 'closed' })),
    `${formatDate(T0)} · archived ${formatDate(T0)} · Closed`);
  assert.equal(boardPath('AB CD'), '/retro/AB%20CD');
});

test('the section has its h1, the Active and Archived choice and New board', async () => {
  const { el } = await mount();
  assert.equal(text(el.querySelector('h1')), 'Retros');
  const group = el.querySelector('[role="group"]');
  assert.equal(group.getAttribute('aria-label'), 'Board list');
  assert.deepEqual(group.querySelectorAll('button').map((b) => [text(b), b.getAttribute('aria-pressed')]), [['Active', 'true'], ['Archived', 'false']]);
  assert.ok(button(el, 'New board'));
});

test('a card is its title linking to the board and its date line, with no visibility', async () => {
  const { el } = await mount();
  const cards = el.querySelectorAll('.team-board');
  assert.deepEqual(cards.map((c) => c.dataset.id), [B1, B2]);
  const link = cards[0].querySelector('.team-board-link');
  assert.equal(text(link), 'Sprint 43 retro');
  assert.equal(link.getAttribute('href'), boardPath(active()[0].code));
  assert.equal(text(cards[0].querySelector('p')), `${formatDate(T0)} · Discussing`);
  assert.equal(text(cards[1].querySelector('p')), formatDate(T0));
  assert.doesNotMatch(text(el), /link|password|team only/i);
});

test('only admins get the board actions menu', async () => {
  const member = await mount();
  assert.equal(member.el.querySelectorAll('.team-board-menu').length, 0);
  mounted.destroy();
  const admin = await mount({ admin: true });
  assert.equal(admin.el.querySelectorAll('.team-board-menu').length, 2);
});

test('Archived lists the archived boards and keeps the choice in the query', async () => {
  const { ctx, el } = await mount();
  button(el, 'Archived').click();
  await settle();
  assert.equal(ctx.query().get('archived'), 'true');
  assert.deepEqual(el.querySelectorAll('.team-board').map((c) => c.dataset.id), [B3]);
  assert.equal(button(el, 'Archived').getAttribute('aria-pressed'), 'true');
});

test('New board asks for a title and a template only, and creates a team board', async () => {
  const ctx = fakeCtx({
    answers: {
      'GET /templates': { items: [{ id: 'tpl-1', name: 'Start, stop, continue', isDefault: false }] },
      'POST /boards': { id: 'new', code: 'NEWBOARD01' },
    },
  });
  openCreateBoard(ctx);
  await settle();
  const dialog = document.querySelector('dialog');
  assert.equal(dialog.querySelectorAll('input').length, 1, 'one text field: the title');
  assert.doesNotMatch(text(dialog), /password|who can open|anyone with the link/i);
  assert.match(text(dialog), /Only members of Platform Team can open it\./);
  const title = dialog.querySelector('input');
  title.value = '  Sprint 44 retro ';
  dialog.querySelector('form').reportValidity = () => true;
  button(dialog, 'Create board').click();
  await settle();
  assert.deepEqual(ctx.calls.find((c) => c.method === 'POST').body, { title: 'Sprint 44 retro' });
  assert.deepEqual(ctx.navigated, [boardPath('NEWBOARD01')]);
});
