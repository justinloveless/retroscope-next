// Sign-up through the real form, the verification mail through SMTP into Mailpit and the emailed link, then the first
// team. A new account is in no team, so it lands on the create-or-join screen, which creates a team only once the
// address is verified; the new team's home is its Poker section.
import { randomUUID } from 'node:crypto';
import { PASSWORD, expect, mailedToken, test } from './_stack.js';

test('a new account signs up, verifies through the mailed link and creates its first team', async ({ page }) => {
  const email = `e2e-${randomUUID()}@example.test`;
  await page.goto('/login?tab=signup');
  const field = (label) => page.getByLabel(label, { exact: true }).filter({ visible: true });
  await field('Full name').fill('Ada Smoke');
  await field('Email').fill(email);
  await field('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/teams$/);
  await expect(page.getByRole('heading', { level: 1, name: /^Welcome, / })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create team' })).toBeDisabled();

  const token = await mailedToken(page.request, email, '/auth/verify');
  await page.goto(`/auth/verify#t=${token}`);
  await expect(page.getByText('Your email is verified.')).toBeVisible();

  // Signed in and in no team, '/' is the create-or-join screen.
  await page.goto('/');
  await expect(page).toHaveURL(/\/teams$/);
  const name = `Smoke ${Date.now()}`;
  await field('Team name').fill(name);
  await page.getByRole('button', { name: 'Create team' }).click();
  await expect(page).toHaveURL(/\/teams\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: `${name} sections` })).toBeVisible();
});
