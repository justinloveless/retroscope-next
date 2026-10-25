import { readFileSync } from 'node:fs';
import { json, problem } from '../../../web-fake-server/http.js';
import { expect, test } from './fixtures.js';

const FIXTURE = JSON.parse(readFileSync(new URL('../../../web-fake-server/fixtures/jira.json', import.meta.url), 'utf8'));

const TEAM = '7ea70000-0000-4000-8000-000000000001';

// Calls the API from the page, the way core/http.js does (same origin, X-RS on every request).
function api(page, method, path, body) {
  return page.evaluate(async ([m, p, b]) => {
    const res = await fetch(p, {
      method: m,
      headers: { 'X-RS': '1', Accept: 'application/json', ...(b === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: b === undefined ? undefined : JSON.stringify(b),
    });
    const type = res.headers.get('content-type') ?? '';
    return { status: res.status, type, body: type.includes('json') ? await res.json() : null };
  }, [method, path, body]);
}

test('fixture routes answer the page and record what it sent', async ({ page, fake }) => {
  fake.route('GET', '/api/teams/:id', ({ params }) => json({ id: params.id, name: 'Fixture team' }));
  fake.route('POST', '/api/teams/:id/boards', ({ body }) => json({ id: 'b1', code: 'ABCDEFGHJK', title: body.title }, 201));
  fake.route('DELETE', '/api/retro/:id', problem(403, 'forbidden'));
  await page.goto('/login');

  expect((await api(page, 'GET', `/api/teams/${TEAM}`)).body).toEqual({ id: TEAM, name: 'Fixture team' });
  const created = await api(page, 'POST', `/api/teams/${TEAM}/boards`, { title: 'Sprint 9 retro' });
  expect(created.status).toBe(201);
  expect(created.body.title).toBe('Sprint 9 retro');
  const denied = await api(page, 'DELETE', '/api/retro/b1');
  expect(denied).toMatchObject({ status: 403, type: 'application/problem+json', body: { code: 'forbidden' } });

  const post = fake.requests.find((r) => r.method === 'POST');
  expect(post.json).toEqual({ title: 'Sprint 9 retro' });
  expect(post.headers.origin).toBe(fake.origin);
});

test('the fake Jira proxy serves browse, detail, writes and mapped errors', async ({ page, fake }) => {
  const jira = fake.jira({ teamId: TEAM });
  await page.goto(`/teams/${TEAM}/poker/FAKE123456`);
  const base = `/api/teams/${TEAM}/jira`;

  const browse = await api(page, 'GET', `${base}/browse?q=card`);
  expect(browse.body.buckets.flatMap((b) => b.issues.map((i) => i.key))).toEqual(['RS-12']);

  const detail = (await api(page, 'GET', `${base}/issues/RS-12`)).body;
  expect(detail.islandIds).toEqual(Object.keys(FIXTURE.issues.find((i) => i.key === 'RS-12').islands));
  const attachment = await page.request.get(`${base}/issues/RS-12/attachments/20001`);
  expect(attachment.headers()['content-type']).toBe('image/png');

  const saved = await api(page, 'PATCH', `${base}/issues/RS-12`, { summary: 'Pay with a stored card', descriptionBase: detail.updated });
  expect(saved.body.fields.summary).toBe('Pay with a stored card');
  const stale = await api(page, 'PATCH', `${base}/issues/RS-12`, { description: 'x', descriptionBase: detail.updated });
  expect(stale.body.code).toBe('stale');

  expect((await api(page, 'PUT', `${base}/issues/RS-12/points`, { points: 3.5 })).status).toBe(200);
  expect(jira.issues.get('RS-12').points).toBe(3.5);

  jira.failNext(problem(503, 'jira_unavailable'));
  expect((await api(page, 'GET', `${base}/sprints`)).body.code).toBe('jira_unavailable');
  expect((await api(page, 'GET', `${base}/sprints`)).body.sprints).toHaveLength(2);
});

test('writes without X-RS are refused like the CSRF filter does', async ({ page, fake }) => {
  fake.route('POST', '/api/poker', json({ id: 'p1', code: 'X' }));
  await page.goto('/');
  const status = await page.evaluate(async () => (await fetch('/api/poker', { method: 'POST', body: '{}' })).status);
  expect(status).toBe(403);
});
