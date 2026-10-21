// Readiness for the current stage (DESIGN §c.4 Presence): only principals present on the board count, so
// someone who left cannot hold the room back. The server keeps every ready mark it was sent; the page
// intersects them with presence, as `retro.stage.set` does when readiness is enforced.

import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { isMember } from './model.js';

/** Readiness and focus: everyone on a quick board, team members only on a team board (RetroRoles.MayPace). */
export function mayPace(view, me) {
  return Boolean(me) && (!view.teamId || isMember(view, me.pid));
}

export function readinessOf(view) {
  const marked = view.readiness.stage === view.stage ? new Set(view.readiness.ready) : new Set();
  const present = [...view.presence.keys()];
  const ready = present.filter((pid) => marked.has(pid));
  return {
    total: present.length,
    ready,
    waiting: present.filter((pid) => !marked.has(pid)),
    all: present.length > 0 && ready.length === present.length,
  };
}

export function createReadiness(ctx) {
  const count = h('span', { class: 'retro-ready-count' });
  const fill = h('span', { class: 'retro-ready-fill' });
  const bar = h('span', { class: 'retro-ready-bar', 'aria-hidden': 'true' }, fill);
  const toggle = createButton({
    size: 'sm',
    onClick() {
      const view = ctx.view();
      const me = ctx.me();
      ctx.send('retro.ready.set', { stage: view.stage, ready: !readinessOf(view).ready.includes(me.pid) });
    },
  });
  const el = h('div', { class: 'retro-ready' }, count, bar, toggle);

  function update(view) {
    const me = ctx.me();
    const state = readinessOf(view);
    const names = state.ready.map((pid) => ctx.personName(pid) ?? 'Someone');
    count.textContent = `${state.ready.length}/${state.total} ready`;
    count.title = names.length === 0 ? 'Nobody is ready yet' : `Ready: ${names.join(', ')}`;
    fill.style.setProperty('--ready', state.total === 0 ? '0' : String(state.ready.length / state.total));
    const mine = Boolean(me) && state.ready.includes(me.pid);
    const closed = view.stage === 'closed' || view.meta.archived;
    toggle.el.hidden = closed || !mayPace(view, me) || !view.presence.has(me.pid);
    toggle.update({ label: mine ? 'Ready' : 'Mark ready', icon: mine ? 'check' : null, pressed: mine, variant: mine ? 'emerald' : 'outline' });
    el.dataset.all = String(state.all);
  }

  return { el, update };
}
