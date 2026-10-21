import { json, problem } from '../../../web-fake-server/http.js';
import { ADMIN, MEMBER, OWNER, TEAM_ID, TYPE_1, TYPE_2, setupTeam, team, teamUrl, expect, test } from './_team.js';

const settingsUrl = `/teams/${TEAM_ID}/settings`;

function jiraConfig(extra = {}) {
  return {
    configured: true,
    siteHost: 'acme.atlassian.net',
    projectKey: 'PLAT',
    boardId: 7,
    ticketPrefix: 'PLAT-',
    membersCanEdit: true,
    status: 'ok',
    lastCheckedAt: Date.UTC(2026, 9, 20, 8),
    emailMasked: 'j•••@acme.com',
    tokenLast4: 'x9Qa',
    ...extra,
  };
}

test.describe('team settings', () => {
  test('members are told settings are for admins', async ({ page, fake }) => {
    const calls = setupTeam(fake, { me: MEMBER });
    await page.goto(settingsUrl);
    await expect(page.getByText('Only team admins and the owner can change the settings.')).toBeVisible();
    expect(calls.jira).toHaveLength(0);
  });

  test('saves only the general fields that changed', async ({ page, fake }) => {
    const calls = setupTeam(fake, { me: ADMIN });
    const patches = [];
    fake.route('PATCH', `/api/teams/${TEAM_ID}`, (req) => {
      patches.push(req.body);
      return { status: 204 };
    });
    await page.goto(settingsUrl);
    await expect(page.getByLabel('Team name')).toHaveValue('Platform');
    await expect(page.getByLabel('Description for 3 points')).toHaveValue('A day');

    await page.getByLabel('Description for 8 points').fill('A week');
    await page.getByLabel('Kudos per person per board').fill('5');
    await page.getByRole('button', { name: 'Save settings' }).click();
    await expect.poll(() => patches).toEqual([{ pointDescriptions: { 3: 'A day', 8: 'A week' }, maxEndorsementsPerBoard: 5 }]);
    await expect.poll(() => calls.team.length).toBe(2);
    await expect(page.getByText('Danger zone')).toBeHidden();
  });

  test('the owner deletes the team by typing its name', async ({ page, fake }) => {
    setupTeam(fake, { me: OWNER });
    let body = null;
    fake.route('DELETE', `/api/teams/${TEAM_ID}`, (req) => {
      body = req.body;
      return { status: 204 };
    });
    await page.goto(settingsUrl);
    await page.getByRole('button', { name: 'Delete team' }).click();
    const dialog = page.getByRole('dialog', { name: 'Delete this team?' });
    await dialog.getByLabel('Type "Platform" to confirm').fill('Platfrom');
    await dialog.getByRole('button', { name: 'Delete team' }).click();
    await expect(dialog.getByRole('alert')).toHaveText('The name does not match.');
    await dialog.getByLabel('Type "Platform" to confirm').fill('Platform');
    await dialog.getByRole('button', { name: 'Delete team' }).click();
    await expect(page).toHaveURL(/\/teams$/);
    expect(body).toEqual({ confirmName: 'Platform' });
  });
});

test.describe('Jira settings', () => {
  test('never shows the token, and keeps it when the field stays empty', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    const puts = [];
    fake.route('PUT', `/api/teams/${TEAM_ID}/jira`, (req) => {
      puts.push(req.body);
      return json(jiraConfig({ membersCanEdit: req.body.membersCanEdit }));
    });
    await page.goto(settingsUrl);

    const jira = page.locator('#jira');
    await expect(jira).toContainText('Connected to acme.atlassian.net with the token ending in x9Qa');
    await expect(jira.getByLabel('API token')).toHaveValue('');
    await expect(jira.getByLabel('API token')).toHaveAttribute('type', 'password');
    await expect(jira.getByLabel('API token')).toHaveAttribute('placeholder', /x9Qa/);
    await expect(jira.getByLabel('Atlassian account email')).toHaveValue('');
    await expect(jira.getByLabel('Atlassian account email')).toHaveAttribute('placeholder', 'j•••@acme.com');
    expect(await page.content()).not.toContain('secret');

    const toggle = jira.getByRole('switch', { name: 'Members may edit Jira issues and submit points' });
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
    await toggle.click();
    await jira.getByRole('button', { name: 'Save Jira settings' }).click();
    await expect.poll(() => puts).toEqual([{ siteHost: 'acme.atlassian.net', projectKey: 'PLAT', boardId: 7, ticketPrefix: 'PLAT-', membersCanEdit: false }]);
    await expect(jira.getByRole('switch', { name: 'Members may edit Jira issues and submit points' })).toHaveAttribute('aria-checked', 'false');
  });

  test('a new site needs the token again', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    const puts = [];
    fake.route('PUT', `/api/teams/${TEAM_ID}/jira`, (req) => {
      puts.push(req.body);
      return json(jiraConfig({ siteHost: req.body.siteHost, tokenLast4: req.body.token.slice(-4) }));
    });
    await page.goto(settingsUrl);
    const jira = page.locator('#jira');
    await jira.getByLabel('Jira site').fill('other.atlassian.net');
    await jira.getByRole('button', { name: 'Save Jira settings' }).click();
    await expect(jira.getByRole('alert')).toHaveText('A new site needs the API token again.');
    await expect(jira.getByLabel('API token')).toHaveAttribute('aria-invalid', 'true');
    expect(puts).toHaveLength(0);

    await jira.getByLabel('API token').fill('ATATT-secret-Zz12');
    await jira.getByRole('button', { name: 'Save Jira settings' }).click();
    await expect(jira).toContainText('Connected to other.atlassian.net with the token ending in Zz12');
    expect(puts[0]).toMatchObject({ siteHost: 'other.atlassian.net', token: 'ATATT-secret-Zz12' });
    await expect(jira.getByLabel('API token')).toHaveValue('');
  });

  test('rotate-required shows a blocking banner here and on the team page for admins only', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN, teamInfo: team('admin', { jira: { configured: true, status: 'rotate_required', membersCanEdit: true } }) });
    fake.route('GET', `/api/teams/${TEAM_ID}/jira`, json(jiraConfig({ status: 'rotate_required' })));
    await page.goto(teamUrl());
    const banner = page.locator('.team-banner');
    await expect(banner).toContainText('must be replaced');
    await banner.getByRole('link', { name: 'Update Jira settings' }).click();

    await expect(page).toHaveURL(new RegExp(`${settingsUrl}#jira$`));
    const blocking = page.locator('#jira .team-banner-blocking');
    await expect(blocking).toHaveAttribute('data-status', 'rotate_required');
    await expect(blocking).toContainText('Revoke it in Atlassian');
  });

  test('members never see the rotate-required banner', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER, teamInfo: team('member', { jira: { configured: true, status: 'rotate_required', membersCanEdit: true } }) });
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();
    await expect(page.locator('.team-banner')).toBeHidden();
  });

  test('a failed connection test gives the classic-token hint', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    fake.route('POST', `/api/teams/${TEAM_ID}/jira/test`, problem(502, 'jira_auth_failed'));
    await page.goto(settingsUrl);
    await page.locator('#jira').getByRole('button', { name: 'Test connection' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'Use a classic (unscoped) API token.' })).toBeVisible();
  });

  test('connects a team without Jira', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    fake.route('GET', `/api/teams/${TEAM_ID}/jira`, json({ configured: false, membersCanEdit: true }));
    let put = null;
    fake.route('PUT', `/api/teams/${TEAM_ID}/jira`, (req) => {
      put = req.body;
      return json(jiraConfig());
    });
    await page.goto(settingsUrl);
    const jira = page.locator('#jira');
    await expect(jira).toContainText('Not connected.');
    await jira.getByLabel('Jira site').fill('acme.atlassian.net');
    await jira.getByLabel('Atlassian account email').fill('jo@acme.com');
    await jira.getByLabel('API token').fill('ATATT-new-x9Qa');
    await jira.getByRole('button', { name: 'Connect Jira' }).click();
    await expect(jira).toContainText('Connected to acme.atlassian.net');
    expect(put).toMatchObject({ siteHost: 'acme.atlassian.net', email: 'jo@acme.com', token: 'ATATT-new-x9Qa', membersCanEdit: true });
  });
});

test.describe('templates and kudos types', () => {
  test('creates a template with an action-items column and makes another the default', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    const TEMPLATE = '0192f0a0-0000-7000-8000-0000000000f1';
    const starfish = {
      id: TEMPLATE,
      name: 'Starfish',
      isDefault: false,
      columns: [{ title: 'Keep', color: 'green', actionItems: false }, { title: 'Do', color: 'yellow', actionItems: true }],
      config: { allowAnonymous: true, votingEnabled: true, maxVotes: 3, showAuthorNames: true, stagesEnabled: false, enforceReadiness: false, allowSelfVotes: true, voteEmoji: '👍' },
    };
    let templates = [starfish];
    fake.route('GET', `/api/teams/${TEAM_ID}/templates`, () => json({ items: templates }));
    let created = null;
    fake.route('POST', `/api/teams/${TEAM_ID}/templates`, (req) => {
      created = req.body;
      templates = [...templates, { id: '0192f0a0-0000-7000-8000-0000000000f2', ...req.body }];
      return json(templates.at(-1), 201);
    });
    let put = null;
    fake.route('PUT', `/api/teams/${TEAM_ID}/templates/:id`, (req) => {
      put = { id: req.params.id, ...req.body };
      return { status: 204 };
    });
    await page.goto(settingsUrl);
    await expect(page.locator(`.team-template[data-id="${TEMPLATE}"]`)).toContainText('Starfish');

    await page.getByRole('button', { name: 'New template' }).click();
    const dialog = page.getByRole('dialog', { name: 'New template' });
    await dialog.getByLabel('Name', { exact: true }).fill('Mad Sad Glad');
    await dialog.getByLabel('Column 2 title').fill('Sad');
    await dialog.getByRole('button', { name: 'Remove column 3' }).click();
    await dialog.getByLabel('Column 1 collects action items').check();
    await dialog.getByRole('switch', { name: 'Retro stages' }).click();
    await dialog.getByLabel('Votes per person (0 = unlimited)').fill('5');
    await dialog.getByRole('button', { name: 'Create template' }).click();

    await expect(page.locator('.team-template')).toHaveCount(2);
    expect(created).toEqual({
      name: 'Mad Sad Glad',
      isDefault: false,
      columns: [
        { title: 'Good', color: 'green', actionItems: true },
        { title: 'Sad', color: 'red', actionItems: false },
        { title: 'Action Items', color: 'yellow', actionItems: false },
      ],
      config: { allowAnonymous: true, votingEnabled: true, maxVotes: 5, showAuthorNames: true, stagesEnabled: true, enforceReadiness: false, allowSelfVotes: true, voteEmoji: '👍' },
    });

    await page.locator(`.team-template[data-id="${TEMPLATE}"]`).getByRole('button', { name: 'Make default' }).click();
    await expect.poll(() => put?.isDefault).toBe(true);
    expect(put).toMatchObject({ id: TEMPLATE, name: 'Starfish', columns: starfish.columns });
  });

  test('adds, renames and archives kudos types', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    const NEW_TYPE = '0192f0a0-0000-7000-8000-0000000000e3';
    let posted = null;
    fake.route('POST', `/api/teams/${TEAM_ID}/endorsement-types`, (req) => {
      posted = req.body;
      return json({ id: NEW_TYPE, archived: false, ...req.body }, 201);
    });
    let patched = null;
    fake.route('PATCH', `/api/teams/${TEAM_ID}/endorsement-types/:id`, (req) => {
      patched = { id: req.params.id, ...req.body };
      return json({ id: req.params.id, name: req.body.name, emoji: '🤝', archived: false });
    });
    let archived = null;
    fake.route('DELETE', `/api/teams/${TEAM_ID}/endorsement-types/:id`, (req) => {
      archived = req.params.id;
      return { status: 204 };
    });
    await page.goto(settingsUrl);
    const types = page.locator('.team-endorsement-types');
    await expect(types.locator(`[data-id="${TYPE_1}"]`)).toBeVisible();

    await page.getByLabel('New type emoji').fill('🚀');
    await page.getByLabel('New type name').fill('Shipper');
    await page.getByLabel('New type description').fill('Got it out the door');
    await page.getByRole('button', { name: 'Add type' }).click();
    await expect(types.locator(`[data-id="${NEW_TYPE}"]`)).toBeVisible();
    expect(posted).toEqual({ name: 'Shipper', emoji: '🚀', description: 'Got it out the door' });

    await types.getByLabel('Helper name').fill('Great helper');
    await types.getByRole('button', { name: 'Save Helper' }).click();
    await expect.poll(() => patched).toEqual({ id: TYPE_1, name: 'Great helper' });

    await types.getByRole('button', { name: 'Archive Teacher' }).click();
    await expect(types.locator(`[data-id="${TYPE_2}"]`)).toHaveCount(0);
    expect(archived).toBe(TYPE_2);
  });
});
