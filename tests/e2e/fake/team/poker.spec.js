import { readFileSync } from 'node:fs';
import { json, problem } from '../../../web-fake-server/http.js';
import { topicSummary, toneIndex } from '../../../../web/src/team/topics.js';
import { MEMBER, ROOM, TEAM_ID, setupTeam, team, teamUrl, expect, test } from './_team.js';

const SESSION_1 = '0192f0a0-0000-7000-8000-000000005001';
const SESSION_2 = '0192f0a0-0000-7000-8000-000000005002';
const CREATED = Date.UTC(2026, 9, 14, 13, 5);

function session(id, code, extra = {}) {
  return { id, code, createdAt: CREATED, pointed: 2, total: 5, parentKeys: [], ...extra };
}

function sessions() {
  return [
    session(SESSION_1, 'POKER00001', { parentKeys: [{ key: 'PLAT-10', count: 4 }, { key: null, count: 1 }] }),
    session(SESSION_2, 'POKER00002', { pointed: 0, total: 1, parentKeys: [{ key: null, count: 1 }] }),
  ];
}

test.describe('poker tab', () => {
  test('lists sessions with progress and topic badges named through Jira', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    const metaRequests = [];
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/meta`, (req) => {
      metaRequests.push(req.body);
      return json({ items: [{ key: 'PLAT-10', summary: 'Checkout revamp', issueType: 'Epic', status: 'In Progress' }] });
    });
    await page.goto(teamUrl('?tab=poker'));

    const first = page.locator(`[data-id="${SESSION_1}"]`);
    await expect(first.getByText('2/5 stories pointed')).toBeVisible();
    const badge = first.locator('.team-topic');
    await expect(badge).toHaveText('Checkout revamp');
    await expect(badge).toHaveAttribute('title', 'PLAT-10');
    await expect(badge).toHaveClass(new RegExp(`team-tone-${toneIndex('PLAT-10')}`));
    await expect(first.getByRole('link', { name: 'Join' })).toHaveAttribute('href', `/teams/${TEAM_ID}/poker/POKER00001`);
    await expect(page.locator(`[data-id="${SESSION_2}"] .team-topic`)).toHaveText('various topics');
    expect(metaRequests).toEqual([{ keys: ['PLAT-10'] }]);
    expect(calls.team.length).toBe(1);
  });

  test('falls back to bare parent keys when Jira cannot name them', async ({ page, fake }) => {
    setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, json({ items: sessions(), nextCursor: null }));
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/meta`, problem(503, 'jira_unavailable'));
    await page.goto(teamUrl('?tab=poker'));
    await expect(page.locator(`[data-id="${SESSION_1}"] .team-topic`)).toHaveText('PLAT-10');
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
    await expect(page.locator(`[data-id="${SESSION_1}"] .team-topic`)).toHaveText('PLAT-10');
    expect(asked).toBe(false);
  });

  test('patches progress from team.poker.changed and reloads for an unknown session', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    let list = sessions();
    fake.route('GET', `/api/teams/${TEAM_ID}/poker-sessions`, (req) => {
      calls.sessions.push(req);
      return json({ items: list, nextCursor: null });
    });
    await page.goto(teamUrl('?tab=poker'));
    await expect(page.locator(`[data-id="${SESSION_1}"]`).getByText('2/5 stories pointed')).toBeVisible();
    await fake.waitForConnection();

    const loads = calls.sessions.length;
    fake.tx(ROOM, [{ k: 'team.poker.changed', d: { sessionId: SESSION_1, pointed: 3, total: 5 } }]);
    await expect(page.locator(`[data-id="${SESSION_1}"]`).getByText('3/5 stories pointed')).toBeVisible();
    expect(calls.sessions.length).toBe(loads);

    const SESSION_3 = '0192f0a0-0000-7000-8000-000000005003';
    list = [session(SESSION_3, 'POKER00003', { pointed: 0, total: 0 }), ...list];
    fake.tx(ROOM, [{ k: 'team.poker.changed', d: { sessionId: SESSION_3, pointed: 0, total: 0 } }]);
    await expect(page.locator(`[data-id="${SESSION_3}"]`).getByText('0/0 stories pointed')).toBeVisible();
    expect(calls.sessions.length).toBe(loads + 1);
  });

  test('creates a session and opens it', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER });
    fake.route('POST', `/api/teams/${TEAM_ID}/poker-sessions`, json({ id: SESSION_1, code: 'NEWPOKER01' }, 201));
    await page.goto(teamUrl('?tab=poker'));
    await expect(page.getByRole('heading', { name: 'No poker sessions yet' })).toBeVisible();
    await page.getByRole('button', { name: 'New poker session' }).first().click();
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
    await page.locator(`[data-id="${SESSION_2}"]`).getByRole('button', { name: 'Delete poker session' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete session' }).click();
    await expect(page.locator(`[data-id="${SESSION_2}"]`)).toHaveCount(0);
    expect(deleted).toBe(SESSION_2);
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
