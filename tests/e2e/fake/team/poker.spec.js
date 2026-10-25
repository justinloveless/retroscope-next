import { readFileSync } from 'node:fs';
import { json, problem } from '../../../web-fake-server/http.js';
import { topicSummary, toneIndex } from '../../../../web/src/team/topics.js';
import { MEMBER, ROOM, TEAM_ID, setupTeam, team, teamUrl, expect, test } from './_team.js';

const SESSION_1 = '0192f0a0-0000-7000-8000-000000005001';
const SESSION_2 = '0192f0a0-0000-7000-8000-000000005002';
const CREATED = Date.UTC(2026, 9, 14, 13, 5);

function session(id, code, extra = {}) {
  return { id, code, createdAt: CREATED, pointed: 2, total: 5, parentKeys: [], lastActivityAt: CREATED, ...extra };
}

// The newest first, as the server lists them: the first is the latest session.
function sessions() {
  return [
    session(SESSION_1, 'POKER00001', { parentKeys: [{ key: 'PLAT-10', count: 4 }, { key: null, count: 1 }], lastActivityAt: Date.now() - 5 * 60_000 }),
    session(SESSION_2, 'POKER00002', { pointed: 0, total: 1, parentKeys: [{ key: null, count: 1 }] }),
  ];
}

const latest = (page) => page.locator('.team-latest');
const row = (page, id) => page.locator(`.team-session-row[data-id="${id}"]`);

test.describe('poker section', () => {
  test('highlights the latest session and lists the earlier ones, with topics named through Jira', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    const metaRequests = [];
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/meta`, (req) => {
      metaRequests.push(req.body);
      return json({ items: [{ key: 'PLAT-10', summary: 'Checkout revamp', issueType: 'Epic', status: 'In Progress' }] });
    });
    await page.goto(teamUrl());

    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await expect(latest(page)).toHaveAttribute('data-id', SESSION_1);
    await expect(latest(page).getByText('Latest session')).toBeVisible();
    await expect(latest(page).getByRole('heading', { level: 2 })).toHaveText(/Oct 14/);
    await expect(latest(page).getByText('2 of 5 stories pointed · last activity 5 minutes ago')).toBeVisible();
    const badge = latest(page).locator('.team-topic');
    await expect(badge).toHaveText('Checkout revamp');
    await expect(badge).toHaveAttribute('title', 'PLAT-10');
    await expect(badge).toHaveClass(new RegExp(`team-tone-${toneIndex('PLAT-10')}`));
    await expect(latest(page).getByRole('link', { name: 'Join' })).toHaveAttribute('href', `/teams/${TEAM_ID}/poker/POKER00001`);

    await expect(page.getByRole('heading', { level: 2, name: 'Earlier sessions' })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Earlier sessions' }).getByRole('listitem')).toHaveCount(1);
    await expect(row(page, SESSION_2).getByRole('link')).toHaveAttribute('href', `/teams/${TEAM_ID}/poker/POKER00002`);
    await expect(row(page, SESSION_2)).toContainText('0 of 1 pointed');
    await expect(row(page, SESSION_2)).toContainText('various topics');
    // Sessions never close and nothing records that one is running: the list claims neither.
    await expect(page.getByRole('main')).not.toContainText(/\b(live|in progress)\b/i);
    expect(metaRequests).toEqual([{ keys: ['PLAT-10'] }]);
    expect(calls.team.length).toBe(1);
  });

  test('falls back to bare parent keys when Jira cannot name them', async ({ page, fake }) => {
    setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/meta`, problem(503, 'jira_unavailable'));
    await page.goto(teamUrl('?tab=poker'));
    await expect(latest(page).locator('.team-topic')).toHaveText('PLAT-10');
  });

  test('never asks Jira when the team has no Jira configured', async ({ page, fake }) => {
    setupTeam(fake, { teamInfo: team('owner', { jira: { configured: false } }) });
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    let asked = false;
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/meta`, () => {
      asked = true;
      return json({ items: [] });
    });
    await page.goto(teamUrl('?tab=poker'));
    await expect(latest(page).locator('.team-topic')).toHaveText('PLAT-10');
    expect(asked).toBe(false);
  });

  test('patches progress from team.poker.changed, and a new session becomes the latest', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    let list = sessions();
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, (req) => {
      calls.sessions.push(req);
      return json({ items: list, nextCursor: null });
    });
    await page.goto(teamUrl('?tab=poker'));
    await expect(latest(page).getByText(/^2 of 5 stories pointed/)).toBeVisible();
    await fake.waitForSubscription(ROOM);

    const loads = calls.sessions.length;
    fake.tx(ROOM, [{ k: 'team.poker.changed', d: { sessionId: SESSION_1, pointed: 3, total: 5 } }]);
    await expect(latest(page).getByText('3 of 5 stories pointed · last activity now')).toBeVisible();
    expect(calls.sessions.length).toBe(loads);

    const SESSION_3 = '0192f0a0-0000-7000-8000-000000005003';
    list = [session(SESSION_3, 'POKER00003', { pointed: 0, total: 0 }), ...list];
    fake.tx(ROOM, [{ k: 'team.poker.changed', d: { sessionId: SESSION_3, pointed: 0, total: 0 } }]);
    await expect(latest(page)).toHaveAttribute('data-id', SESSION_3);
    await expect(latest(page).getByText(/^No stories yet/)).toBeVisible();
    await expect(row(page, SESSION_1)).toContainText('3 of 5 pointed');
    expect(calls.sessions.length).toBe(loads + 1);
  });

  test('starts a session and opens it', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER });
    fake.route('POST', `/api/teams/${TEAM_ID}/poker-sessions`, json({ id: SESSION_1, code: 'NEWPOKER01' }, 201));
    await page.goto(teamUrl('?tab=poker'));
    await expect(page.getByRole('heading', { name: 'No poker sessions yet' })).toBeVisible();
    await page.getByRole('button', { name: 'Start poker session' }).click();
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}/poker/NEWPOKER01$`));
  });

  test('admins delete a session after confirming', async ({ page, fake }) => {
    setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    let deleted = null;
    fake.route('DELETE', '/api/poker/:id', (req) => {
      deleted = req.params.id;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=poker'));
    await row(page, SESSION_2).getByRole('button', { name: /^Delete the session of / }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete session' }).click();
    await expect(row(page, SESSION_2)).toHaveCount(0);
    expect(deleted).toBe(SESSION_2);
  });

  test('members get no delete buttons', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER });
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    await page.goto(teamUrl('?tab=poker'));
    await expect(latest(page)).toBeVisible();
    await expect(page.getByRole('button', { name: /^Delete the session of / })).toHaveCount(0);
  });
});

test.describe('topic summary', () => {
  test('matches the original 75 % rule on the golden vectors it can represent', () => {
    test.skip(test.info().project.name !== 'chromium', 'pure logic: one project is enough');
    const golden = JSON.parse(readFileSync(new URL('../../../golden/topic-summary.json', import.meta.url), 'utf8'));
    const synthetic = (ticket) => ticket == null || String(ticket).trim() === '' || /^round:\d+$/i.test(String(ticket).trim());
    let compared = 0;
    for (const { rounds, expected } of golden.cases) {
      const real = rounds.filter((r) => !synthetic(r.ticket_number));
      // Only parent keys are stored now; a topic known by summary alone has no equivalent.
      if (real.some((r) => !r.ticket_parent_key?.trim() && r.ticket_parent_summary?.trim())) {
        continue;
      }
      const parentKeys = [];
      const summaries = new Map();
      for (const r of real) {
        const key = r.ticket_parent_key?.trim() || null;
        const group = parentKeys.find((p) => p.key === key);
        if (group) {
          group.count++;
        } else {
          parentKeys.push({ key, count: 1 });
        }
        if (key) {
          const summary = r.ticket_parent_summary?.trim();
          if (summary) {
            summaries.set(key, summary);
          } else {
            summaries.delete(key);
          }
        }
      }
      const actual = topicSummary(parentKeys, summaries);
      const want = expected === null ? null : expected.kind === 'various'
        ? { kind: 'various' }
        : { kind: 'badges', topics: expected.topics.map((t) => ({ label: t.label, tone: t.toneIndex })) };
      const got = actual === null ? null : actual.kind === 'various'
        ? { kind: 'various' }
        : { kind: 'badges', topics: actual.topics.map((t) => ({ label: t.label, tone: t.tone })) };
      expect(got, JSON.stringify(rounds)).toEqual(want);
      compared++;
    }
    expect(compared).toBeGreaterThanOrEqual(10);
  });
});
