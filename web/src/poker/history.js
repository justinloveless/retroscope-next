// The Round panel's History tab (§0.1): every round's result in strip order, with "Revealed by X ·
// 14:02" lines in place of the old system chat messages. The panel itself (tabs, collapse, resize,
// persisted in localStorage['rs.panels']) is shared with the Issue tab, which the Jira units add.

import { h } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { createSidePanel } from '../ui/side-panel.js';
import { createTabs } from '../ui/tabs.js';
import { voteLabel } from './model.js';
import { personOf } from './strip.js';

const PANELS_KEY = 'rs.panels';
const timeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });

export function readPanels() {
  try {
    return JSON.parse(localStorage.getItem(PANELS_KEY)) ?? {};
  } catch {
    return {};
  }
}

export function writePanel(side, patch) {
  try {
    const panels = readPanels();
    panels[side] = { ...panels[side], ...patch };
    localStorage.setItem(PANELS_KEY, JSON.stringify(panels));
  } catch {
    // Storage is a convenience here; the panel works without it.
  }
}

/** What a history entry says about a round: {title, status, detail}. */
export function roundSummary(view, round) {
  const title = round.ticketKey ? `Round ${round.number} · ${round.ticketKey}` : `Round ${round.number}`;
  if (round.pending) {
    return { title, status: 'Preview', detail: null };
  }
  if (round.state !== 'revealed') {
    return { title, status: round.active ? 'Voting' : 'Ended without a reveal', detail: null };
  }
  const by = round.revealedBy ? `Revealed by ${personOf(view, round.revealedBy)?.name ?? 'someone'}` : 'Revealed automatically';
  const at = round.revealedAt ? ` · ${timeFormat.format(round.revealedAt)}` : '';
  const winner = round.winner == null ? 'No points' : voteLabel(round.winner);
  return { title, status: winner, detail: `${by}${at}` };
}

function jiraLine(view, round) {
  if (round.jira) {
    return `Jira: ${round.jira.points} pts`;
  }
  const submit = view.submits[round.id];
  if (submit?.status === 'pending') {
    return `Jira: sending ${submit.points} pts…`;
  }
  return submit?.status === 'failed' ? 'Jira: the update failed' : null;
}

/** The list itself, used by the desktop panel and the mobile sheet. */
export function createHistoryList(ctx) {
  const el = h('ol', { class: 'nt-history', 'aria-label': 'Round history' });
  el.addEventListener('click', (event) => {
    const item = event.target.closest('[data-round]');
    if (item) {
      ctx.navigate(item.dataset.round);
      ctx.onPick?.();
    }
  });

  let view = null;
  let viewed = null;

  function patch(node, round) {
    const { title, status, detail } = roundSummary(view, round);
    const jira = jiraLine(view, round);
    node.dataset.state = round.pending ? 'pending' : round.state;
    node.parts.title.textContent = title;
    node.parts.status.textContent = status;
    node.parts.detail.textContent = detail ?? '';
    node.parts.detail.hidden = !detail;
    node.parts.jira.textContent = jira ?? '';
    node.parts.jira.hidden = !jira;
    if (round.id === viewed) {
      node.parts.button.setAttribute('aria-current', 'true');
    } else {
      node.parts.button.removeAttribute('aria-current');
    }
  }

  function create(round) {
    const parts = {
      title: h('span', { class: 'nt-history-title' }),
      status: h('span', { class: 'nt-history-status' }),
      detail: h('span', { class: 'nt-history-detail' }),
      jira: h('span', { class: 'nt-history-jira' }),
    };
    const button = h('button', { type: 'button', class: 'nt-history-entry', dataset: { round: round.id } },
      parts.title, parts.status, parts.detail, parts.jira);
    const node = h('li', { class: 'nt-history-item' }, button);
    node.parts = { ...parts, button };
    patch(node, round);
    return node;
  }

  return {
    el,
    update(next, roundId) {
      view = next;
      viewed = roundId;
      reconcile(el, view.rounds, (r) => r.id, create, patch);
    },
  };
}

const panels = new WeakMap();

/**
 * The Round panel in the right-hand slot: a tab list with the drawer handles (side-panel.js). One per
 * slot; `addTab({id, label, order})` returns the tab's panel element.
 */
export function roundPanel(slot) {
  let panel = panels.get(slot);
  if (panel) {
    return panel;
  }
  const saved = readPanels().right ?? {};
  const tabs = [];
  const tablist = createTabs({ tabs: [], label: 'Round panel', onChange: (id) => writePanel('right', { tab: id }) });
  const body = h('div', { class: 'nt-round-panel-body' });
  slot.classList.add('nt-round-panel');
  slot.append(h('div', { class: 'nt-round-panel-head' }, tablist.el), body);
  const side = createSidePanel({
    panel: slot,
    edge: 'right',
    name: 'round panel',
    openIcon: 'list-ordered',
    width: saved.width ?? 320,
    open: saved.open !== false,
    onToggle(open) {
      side.setOpen(open, { focus: true });
      writePanel('right', { open });
    },
    onCommit: (width) => writePanel('right', { width }),
  });

  panel = {
    addTab({ id, label, order = 0 }) {
      tabs.push({ id, label, order });
      tabs.sort((a, b) => a.order - b.order);
      tablist.update({ tabs: tabs.map(({ id: tab, label: text }) => ({ id: tab, label: text })) });
      const content = tablist.panels.get(id);
      body.append(...tabs.map((t) => tablist.panels.get(t.id)));
      tablist.update({ selected: tabs.some((t) => t.id === saved.tab) ? saved.tab : tabs[0].id });
      return content;
    },
    destroy() {
      panels.delete(slot);
      tablist.destroy();
      side.destroy();
      slot.replaceChildren();
      slot.classList.remove('nt-round-panel');
      slot.hidden = true;
    },
  };
  panels.set(slot, panel);
  return panel;
}

export function createHistory(ctx) {
  const panel = roundPanel(ctx.slots.right);
  const content = panel.addTab({ id: 'history', label: 'History', order: 1 });
  const list = createHistoryList(ctx);
  content.append(list.el);
  return {
    update(view, roundId) {
      list.update(view, roundId);
    },
    destroy() {
      panel.destroy();
    },
  };
}
