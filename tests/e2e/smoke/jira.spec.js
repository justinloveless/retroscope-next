// The Jira proxy end to end: TLS to FakeJira under the stack's CA, through the client's public-address rule.
import { expect, json, test, verifiedUser } from './_stack.js';

test('a team admin connects Jira and browses the board buckets through the proxy', async ({ page }) => {
  await verifiedUser(page.request, 'Jo Jira');
  const team = await page.request.post('/api/teams', json({ name: `Smoke ${Date.now()}` }));
  expect(team.status()).toBe(201);
  const { id } = await team.json();

  const config = await page.request.put(`/api/teams/${id}/jira`, json({
    siteHost: 'acme.atlassian.net',
    email: 'bot@acme.test',
    token: 'ATATT-fake-token-1234',
    projectKey: 'ACME',
    boardId: 1,
  }));
  expect(config.ok(), await config.text()).toBe(true);

  const browse = await page.request.get(`/api/teams/${id}/jira/browse?status=all`, { headers: { 'X-RS': '1' } });
  expect(browse.ok(), await browse.text()).toBe(true);
  const { buckets } = await browse.json();
  const keys = buckets.flatMap((bucket) => bucket.issues.map((issue) => issue.key));
  expect(keys).toEqual(expect.arrayContaining(['ACME-1', 'ACME-4', 'ACME-6']));
});
