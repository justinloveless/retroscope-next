// Sign-up through the real form, the verification mail through SMTP into Mailpit, and the emailed link.
import { randomUUID } from 'node:crypto';
import { PASSWORD, expect, mailedToken, test } from './_stack.js';

test('a new account signs up, receives its verification mail and verifies through the link', async ({ page }) => {
  const email = `e2e-${randomUUID()}@example.test`;
  await page.goto('/login?tab=signup');
  const field = (label) => page.getByLabel(label, { exact: true }).filter({ visible: true });
  await field('Full name').fill('Ada Smoke');
  await field('Email').fill(email);
  await field('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();

  const token = await mailedToken(page.request, email, '/auth/verify');
  await page.goto(`/auth/verify#t=${token}`);
  await expect(page.getByText('Your email is verified.')).toBeVisible();
});
