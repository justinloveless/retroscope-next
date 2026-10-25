// /teams/:id/settings (team admins): general settings, Jira, board templates, endorsement types and,
// for the owner, deleting the team. Loaded as its own chunk.

import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { refreshMe } from '../screens/app.js';
import { card, confirmTyped, field, input, toastError } from './util.js';
import { createJiraSettings } from './jira-settings.js';
import { createTemplatesSettings } from './templates.js';
import { createEndorsementTypesSettings } from './endorsement-types.js';

const DECK = [1, 2, 3, 5, 8, 13, 21];

export function createSettings(ctx) {
  const team = ctx.team();
  const back = h('a', { class: 'btn btn-ghost btn-icon', href: `/teams/${ctx.teamId}`, 'data-nav': '', 'aria-label': 'Back to the team' }, icon('arrow-left'));
  const header = h('header', { class: 'team-header' }, back,
    h('div', { class: 'team-heading' },
      h('h1', { class: 'team-title', text: 'Team settings' }),
      h('p', { class: 'team-description', text: team.name })));
  document.title = `Team settings · ${team.name} · Retroscope`;

  if (!ctx.isAdmin()) {
    const el = h('main', { class: 'team-screen' }, header,
      h('p', { class: 'team-message card', text: 'Only team admins and the owner can change the settings.' }));
    return { el, settings: true, destroy: () => el.remove() };
  }

  const parts = [createJiraSettings(ctx), createTemplatesSettings(ctx), createEndorsementTypesSettings(ctx)];
  const el = h('main', { class: 'team-screen team-settings' }, header,
    general(ctx), ...parts.map((p) => p.el), ctx.isOwner() ? dangerZone(ctx) : null);

  if (location.hash === '#jira') {
    requestAnimationFrame(() => el.querySelector('#jira')?.scrollIntoView({ block: 'start' }));
  }

  return {
    el,
    settings: true,
    destroy() {
      for (const part of parts) {
        part.destroy?.();
      }
      el.remove();
    },
  };
}

function general(ctx) {
  const team = ctx.team();
  const name = input({ name: 'name', required: true, maxlength: '100', value: team.name });
  const description = h('textarea', { class: 'textarea', name: 'description', rows: '2', maxlength: '500', value: team.description ?? '' });
  const max = input({ name: 'maxEndorsementsPerBoard', type: 'number', min: '1', max: '50', value: String(team.maxEndorsements ?? 3) });
  const points = new Map(DECK.map((p) => [p, input({
    name: `point-${p}`,
    maxlength: '100',
    value: team.pointDescriptions?.[p] ?? '',
    'aria-label': `Description for ${p} points`,
  })]));
  const save = createButton({ label: 'Save settings', icon: 'save', variant: 'primary', onClick: submit });

  async function submit() {
    const current = ctx.team();
    const body = {};
    if (name.value.trim() !== current.name) {
      body.name = name.value.trim();
    }
    if (description.value.trim() !== (current.description ?? '')) {
      body.description = description.value.trim();
    }
    const descriptions = Object.fromEntries(DECK.map((p) => [p, points.get(p).value.trim()]).filter(([, v]) => v));
    const before = Object.fromEntries(Object.entries(current.pointDescriptions ?? {}).filter(([, v]) => v));
    if (JSON.stringify(descriptions) !== JSON.stringify(before)) {
      body.pointDescriptions = descriptions;
    }
    if (Number(max.value) !== current.maxEndorsements) {
      body.maxEndorsementsPerBoard = Number(max.value);
    }
    if (!name.value.trim() || !max.checkValidity()) {
      toast('Check the name and the endorsement limit.', { variant: 'error', duration: 4000 });
      return;
    }
    if (Object.keys(body).length === 0) {
      toast('Nothing changed.', { duration: 2500 });
      return;
    }
    save.update({ busy: true, disabled: true });
    try {
      await ctx.api.patch('', body);
      await ctx.reloadTeam();
      if (body.name) {
        // The switcher reads `me`, which a navigation inside the team page does not reload (UX18).
        refreshMe().catch(() => {});
      }
      toast('Settings saved.', { variant: 'success', duration: 3000 });
    } catch (error) {
      toastError(error, 'The settings could not be saved.');
    } finally {
      save.update({ busy: false, disabled: false });
    }
  }

  return card('General', null,
    field('Team name', name),
    field('Description', description),
    h('fieldset', { class: 'team-fieldset' },
      h('legend', { text: 'Point descriptions (shown on the poker cards)' }),
      h('div', { class: 'team-points' }, ...DECK.map((p) => h('label', { class: 'team-point' }, h('span', { class: 'team-point-value', text: String(p) }), points.get(p))))),
    field('Kudos per person per board', max),
    h('div', { class: 'team-actions' }, save.el));
}

function dangerZone(ctx) {
  const remove = createButton({
    label: 'Delete team',
    icon: 'trash',
    variant: 'destructive',
    onClick: () => confirmTyped({
      title: 'Delete this team?',
      message: 'Its boards, poker sessions and action items become inaccessible at once and are erased after 30 days.',
      expected: ctx.team().name,
      confirmLabel: 'Delete team',
      async submit() {
        await ctx.api.del('', { confirmName: ctx.team().name });
        // '/' goes to another of the user's teams, or to create-or-join when this was the last one.
        await refreshMe().catch(() => {});
        ctx.navigate('/');
      },
    }),
  });
  const section = card('Danger zone', 'Only the owner can delete the team.', h('div', { class: 'team-actions' }, remove.el));
  section.classList.add('team-danger');
  return section;
}
