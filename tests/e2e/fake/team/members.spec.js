import { json, problem } from '../../../web-fake-server/http.js';
import { ADMIN, MEMBER, OWNER, ROOM, TEAM_ID, members, setupTeam, teamUrl, choose, expect, test } from './_team.js';

const INVITE_1 = '0192f0a0-0000-7000-8000-000000006001';
const INVITE_2 = '0192f0a0-0000-7000-8000-000000006002';
const EXPIRES = Date.UTC(2026, 9, 28);

function row(page, pid) {
  return page.locator(`.team-member[data-pid="${pid}"]`);
}

test.describe('members tab', () => {
  test('lists members with their roles and follows team.members frames', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl('?tab=members'));
    await expect(row(page, OWNER)).toContainText('Olive Owner (you)');
    await expect(row(page, OWNER).locator('.badge')).toHaveText('Owner');
    await expect(row(page, ADMIN).locator('.badge')).toHaveText('Admin');
    await expect(row(page, MEMBER).locator('.badge')).toHaveText('Member');
    await expect(page.locator('.team-member')).toHaveCount(3);

    await fake.waitForSubscription(ROOM);
    const next = members().map((m) => (m.pid === MEMBER ? { ...m, role: 'admin' } : m));
    fake.tx(ROOM, [{ k: 'team.members', d: { members: next } }]);
    await expect(row(page, MEMBER).locator('.badge')).toHaveText('Admin');
  });

  test('the owner changes roles and transfers ownership through the API', async ({ page, fake }) => {
    setupTeam(fake);
    const patches = [];
    fake.route('PATCH', `/api/teams/${TEAM_ID}/members/:pid`, (req) => {
      patches.push({ pid: req.params.pid, ...req.body });
      return { status: 204 };
    });
    let transfer = null;
    fake.route('POST', `/api/teams/${TEAM_ID}/transfer-ownership`, (req) => {
      transfer = req.body;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=members'));

    await row(page, MEMBER).getByRole('button', { name: 'Actions for Mia Member' }).click();
    await page.getByRole('menuitem', { name: 'Make admin' }).click();
    await expect.poll(() => patches).toEqual([{ pid: MEMBER, role: 'admin' }]);

    await row(page, ADMIN).getByRole('button', { name: 'Actions for Adam Admin' }).click();
    await page.getByRole('menuitem', { name: 'Make member' }).click();
    await expect.poll(() => patches.length).toBe(2);
    expect(patches[1]).toEqual({ pid: ADMIN, role: 'member' });

    await row(page, ADMIN).getByRole('button', { name: 'Actions for Adam Admin' }).click();
    await page.getByRole('menuitem', { name: 'Transfer ownership' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Transfer ownership' }).click();
    await expect.poll(() => transfer).toEqual({ userId: ADMIN });
  });

  test('an admin may remove members but not other admins or the owner', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    let removed = null;
    fake.route('DELETE', `/api/teams/${TEAM_ID}/members/:pid`, (req) => {
      removed = req.params.pid;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=members'));
    await expect(row(page, OWNER).getByRole('button')).toHaveCount(0);
    await row(page, MEMBER).getByRole('button', { name: 'Actions for Mia Member' }).click();
    await expect(page.getByRole('menuitem', { name: 'Make admin' })).toHaveCount(0);
    await page.getByRole('menuitem', { name: 'Remove from team' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Remove' }).click();
    await expect.poll(() => removed).toBe(MEMBER);
  });

  test('a plain member can only leave, and sees no invites', async ({ page, fake }) => {
    const calls = setupTeam(fake, { me: MEMBER });
    let left = null;
    fake.route('DELETE', `/api/teams/${TEAM_ID}/members/:pid`, (req) => {
      left = req.params.pid;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=members'));
    await expect(row(page, ADMIN).getByRole('button')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Invites' })).toBeHidden();
    expect(calls.invites).toHaveLength(0);

    await row(page, MEMBER).getByRole('button', { name: 'Actions for Mia Member' }).click();
    await page.getByRole('menuitem', { name: 'Leave team' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Leave team' }).click();
    await expect(page).toHaveURL(/\/teams$/);
    expect(left).toBe(MEMBER);
  });

  test('a server refusal is shown, and the roster stays as the room says', async ({ page, fake }) => {
    setupTeam(fake);
    fake.route('PATCH', `/api/teams/${TEAM_ID}/members/:pid`, problem(403, 'forbidden'));
    await page.goto(teamUrl('?tab=members'));
    await row(page, MEMBER).getByRole('button', { name: 'Actions for Mia Member' }).click();
    await page.getByRole('menuitem', { name: 'Make admin' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'You do not have permission to do that.' })).toBeVisible();
    await expect(row(page, MEMBER).locator('.badge')).toHaveText('Member');
  });
});

test.describe('invites', () => {
  test('admins see pending invites, create email and link invites and revoke them', async ({ page, fake }) => {
    setupTeam(fake, { me: ADMIN });
    fake.route('GET', `/api/teams/${TEAM_ID}/invites`, json({
      items: [{ id: INVITE_1, kind: 'email', email: 'n•••@example.com', role: 'member', expiresAt: EXPIRES, createdAt: EXPIRES - 7 * 86_400_000 }],
    }));
    const posted = [];
    fake.route('POST', `/api/teams/${TEAM_ID}/invites`, (req) => {
      posted.push(req.body);
      return req.body.kind === 'link'
        ? json({ id: INVITE_2, kind: 'link', role: 'member', url: 'http://127.0.0.1/invite#t=abc', expiresAt: EXPIRES, createdAt: EXPIRES }, 201)
        : json({ id: '0192f0a0-0000-7000-8000-000000006003', kind: 'email', email: 'z•••@example.com', role: req.body.role, expiresAt: EXPIRES, createdAt: EXPIRES }, 201);
    });
    let revoked = null;
    fake.route('DELETE', `/api/teams/${TEAM_ID}/invites/:id`, (req) => {
      revoked = req.params.id;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=members'));

    const invites = page.locator('.team-invites');
    await expect(invites.locator(`[data-id="${INVITE_1}"]`)).toContainText('n•••@example.com');

    await page.getByRole('button', { name: 'Invite by email' }).click();
    const emailDialog = page.getByRole('dialog', { name: 'Invite by email' });
    await emailDialog.getByLabel('Email').fill('zoe@example.com');
    await choose(emailDialog.getByLabel('Role'), 'Admin');
    await emailDialog.getByRole('button', { name: 'Send invite' }).click();
    await expect(invites).toContainText('z•••@example.com');

    await page.getByRole('button', { name: 'Invite link' }).click();
    const linkDialog = page.getByRole('dialog', { name: 'Create an invite link' });
    await choose(linkDialog.getByLabel('Expires after'), '30 days');
    await linkDialog.getByRole('button', { name: 'Create link' }).click();
    await expect(invites.locator(`[data-id="${INVITE_2}"] code`)).toHaveText('http://127.0.0.1/invite#t=abc');

    expect(posted).toEqual([
      { kind: 'email', email: 'zoe@example.com', role: 'admin' },
      { kind: 'link', expiresInDays: 30 },
    ]);

    await invites.locator(`[data-id="${INVITE_1}"]`).getByRole('button', { name: 'Revoke' }).click();
    await expect(invites.locator(`[data-id="${INVITE_1}"]`)).toHaveCount(0);
    expect(revoked).toBe(INVITE_1);
  });

  test('email invites need a verified address; the server answer is shown in the dialog', async ({ page, fake }) => {
    setupTeam(fake, { emailVerified: false });
    await page.goto(teamUrl('?tab=members'));
    await expect(page.getByRole('button', { name: 'Invite by email' })).toBeDisabled();

    fake.route('POST', `/api/teams/${TEAM_ID}/invites`, problem(403, 'email_unverified'));
    await page.getByRole('button', { name: 'Invite link' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create an invite link' });
    await dialog.getByRole('button', { name: 'Create link' }).click();
    await expect(dialog.getByRole('alert')).toHaveText('Verify your email address first.');
  });
});
