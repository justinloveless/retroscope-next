// Jira integration settings. The token is write-only: the page only ever shows its last four
// characters, an empty token field keeps the stored one, and a new site needs the token again (§f).

import { h, replaceContent } from '../core/h.js';
import { formatDateTime } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { createSwitch } from '../ui/switch.js';
import { confirmDialog } from '../ui/confirm.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { card, errorMessage, field, input, loading, toastError } from './util.js';

const STATUS_TEXT = {
  rotate_required: 'This team\'s Jira token was imported from the previous app and must be treated as exposed. Revoke it in Atlassian and save a new token here.',
  auth_failed: 'Jira rejected the saved credentials. The token may have expired or been revoked; save a new one.',
};

export function createJiraSettings(ctx) {
  const body = h('div', { class: 'team-jira' }, loading('Loading Jira settings…'));
  const el = card('Jira integration', 'Connect a Jira Cloud site so poker sessions can browse issues and submit points.', body);
  el.id = 'jira';
  let config = null;
  let destroyed = false;

  async function load() {
    try {
      config = await ctx.api.get('/jira');
      if (!destroyed) {
        render();
      }
    } catch (error) {
      replaceContent(body, h('p', { class: 'team-muted', text: errorMessage(error, 'The Jira settings could not be loaded.') }));
    }
  }

  function render() {
    const configured = config.configured;
    const site = input({ name: 'siteHost', value: config.siteHost ?? '', placeholder: 'your-site.atlassian.net', autocomplete: 'off', required: true });
    const email = input({ name: 'email', type: 'email', autocomplete: 'off', placeholder: config.emailMasked ?? 'you@example.com' });
    const token = input({
      name: 'token',
      type: 'password',
      autocomplete: 'new-password',
      placeholder: config.tokenLast4 ? `•••• ${config.tokenLast4} (leave empty to keep)` : 'API token',
    });
    const projectKey = input({ name: 'projectKey', value: config.projectKey ?? '', placeholder: 'ABC' });
    const boardId = input({ name: 'boardId', value: config.boardId == null ? '' : String(config.boardId), inputmode: 'numeric' });
    const ticketPrefix = input({ name: 'ticketPrefix', value: config.ticketPrefix ?? '', placeholder: 'ABC-' });
    const membersCanEdit = createSwitch({ label: 'Members may edit Jira issues and submit points', checked: config.membersCanEdit ?? true });
    const error = h('p', { class: 'team-form-error', role: 'alert', hidden: true });
    const save = createButton({ label: configured ? 'Save Jira settings' : 'Connect Jira', icon: 'save', variant: 'primary', onClick: submit });

    async function submit() {
      error.hidden = true;
      for (const control of [site, token]) {
        control.removeAttribute('aria-invalid');
      }
      const host = site.value.trim();
      if (!host) {
        return fail(site, 'Enter your Jira site, for example your-site.atlassian.net.');
      }
      const changedSite = configured && host.toLowerCase() !== (config.siteHost ?? '').toLowerCase();
      if ((changedSite || !configured) && !token.value) {
        return fail(token, changedSite ? 'A new site needs the API token again.' : 'Enter an API token.');
      }
      const request = {
        siteHost: host,
        projectKey: projectKey.value.trim() || null,
        boardId: boardId.value.trim() ? Number(boardId.value.trim()) : null,
        ticketPrefix: ticketPrefix.value.trim() || null,
        membersCanEdit: membersCanEdit.checked,
      };
      if (email.value.trim()) {
        request.email = email.value.trim();
      }
      if (token.value) {
        request.token = token.value;
      }
      save.update({ busy: true, disabled: true });
      try {
        config = await ctx.api.put('/jira', request);
        ctx.reloadTeam();
        toast('Jira settings saved.', { variant: 'success', duration: 3000 });
        render();
      } catch (failure) {
        save.update({ busy: false, disabled: false });
        fail(failure?.fields?.token ? token : failure?.fields?.siteHost ? site : null, errorMessage(failure, 'The Jira settings could not be saved.'));
      }
    }

    function fail(control, text) {
      control?.setAttribute('aria-invalid', 'true');
      control?.focus();
      error.textContent = text;
      error.hidden = false;
    }

    async function test() {
      try {
        const result = await ctx.api.post('/jira/test', {});
        toast(result.ok ? `Connected to Jira as ${result.accountName}.` : 'Jira did not accept the connection.', { variant: result.ok ? 'success' : 'error', duration: 5000 });
      } catch (failure) {
        toastError(failure, 'The connection test failed.');
      }
    }

    async function disconnect() {
      if (await confirmDialog({ title: 'Remove the Jira integration?', message: 'The stored token is deleted. Poker sessions lose the Jira browser.', confirmLabel: 'Remove', destructive: true })) {
        try {
          await ctx.api.del('/jira');
          config = { configured: false, membersCanEdit: true };
          ctx.reloadTeam();
          render();
        } catch (failure) {
          toastError(failure, 'The integration could not be removed.');
        }
      }
    }

    const notice = STATUS_TEXT[config.status];
    replaceContent(body,
      notice ? h('div', { class: ['team-banner', 'team-banner-blocking'], role: 'alert', dataset: { status: config.status } }, icon('triangle-alert'), h('span', { text: notice })) : null,
      configured
        ? h('p', { class: 'team-jira-status' }, icon(config.status === 'ok' ? 'circle-check' : 'circle-alert'),
          ` Connected to ${config.siteHost}`,
          config.tokenLast4 ? ` with the token ending in ${config.tokenLast4}` : '',
          config.lastCheckedAt ? h('span', { class: 'team-muted', text: ` · checked ${formatDateTime(config.lastCheckedAt)}` }) : null)
        : h('p', { class: 'team-muted', text: 'Not connected.' }),
      h('form', { class: 'team-form team-jira-form', on: { submit: (event) => { event.preventDefault(); submit(); } } },
        field('Jira site', site),
        field('Atlassian account email', email, configured ? 'Leave empty to keep the saved email.' : null),
        field('API token', token, 'Use a classic (unscoped) API token from id.atlassian.com. It is never shown again.'),
        h('div', { class: 'team-form-row' }, field('Project key', projectKey), field('Board id', boardId), field('Ticket prefix', ticketPrefix)),
        membersCanEdit.el,
        error,
        h('div', { class: 'team-actions' },
          configured ? createButton({ label: 'Test connection', icon: 'zap', variant: 'secondary', onClick: test }).el : null,
          configured ? createButton({ label: 'Remove', icon: 'trash', variant: 'ghost', onClick: disconnect }).el : null,
          save.el)));
  }

  load();
  return {
    el,
    destroy() {
      destroyed = true;
    },
  };
}
