// The Browse Jira panel (§f, §g.7 chunk jira-browse; parity: original EmbeddedTicketQueue). Filters are
// kept per team in localStorage, issues arrive in sprint buckets in the server's order (sprints by start
// date then name, Backlog last, Jira rank inside), and a row's add button is disabled with "Pointing" or
// "Pointed" while its key is on an active or ended round. Search runs on Enter, not per keystroke.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { plural } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { issueTypeGlyph, priorityGlyph, statusBadge } from '../ui/glyphs.js';
import { createMenu } from '../ui/menu.js';
import { createSwitch } from '../ui/switch.js';
import { toast } from '../ui/toast.js';

export const FILTERS_KEY = 'rs.jira.filters';
export const COLLAPSED_KEY = 'rs.jira.collapsed';
const HOVER_PREFETCH_MS = 600;
const KEY_PATTERN = /^(?:[A-Za-z][A-Za-z0-9_]*-)?\d+$/;

const STATUS = [['not-done', 'Exclude Done'], ['all', 'All statuses'], ['new', 'To Do'], ['indeterminate', 'In Progress'], ['done', 'Done']];
const POINTS = [['unpointed', 'Unestimated'], ['any', 'Any points'], ...[1, 2, 3, 5, 8, 13, 21].map((n) => [String(n), `${n} pts`])];
const SCOPE = [['board', 'Board sprints + backlog'], ['open', 'Any open sprint + backlog'], ['all', 'All sprints (incl. closed)']];
export const DEFAULT_FILTERS = { q: '', status: 'not-done', points: 'unpointed', scope: 'board', showFilters: false, hidePointed: true };

const REASON_LABEL = { pointing: 'Pointing', pointed: 'Pointed' };

const ROW = { display: 'flex', 'align-items': 'center', gap: '0.375rem' };
const MUTED = { color: 'hsl(var(--muted-foreground))', 'font-size': '0.75rem' };

function storageKey(prefix, teamId) {
  return `${prefix}:${teamId}`;
}

function readJson(key) {
  try {
    return JSON.parse(localStorage.getItem(key) ?? 'null');
  } catch {
    return null;
  }
}

function writeJson(key, value) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Private mode or a full quota: the setting lasts for this page view.
  }
}

/** Stored filters, each value checked against its allow-list (parity: use-persisted-jira-browse-filters). */
export function readFilters(teamId) {
  const stored = readJson(storageKey(FILTERS_KEY, teamId)) ?? {};
  const pick = (name, options) => (options.some(([value]) => value === stored[name]) ? stored[name] : DEFAULT_FILTERS[name]);
  const flag = (name) => (typeof stored[name] === 'boolean' ? stored[name] : DEFAULT_FILTERS[name]);
  return {
    q: typeof stored.q === 'string' ? stored.q.slice(0, 200) : '',
    status: pick('status', STATUS),
    points: pick('points', POINTS),
    scope: pick('scope', SCOPE),
    showFilters: flag('showFilters'),
    hidePointed: flag('hidePointed'),
  };
}

/** Why a key cannot be added again: on an active round ("pointing") or an ended one ("pointed"); previews do not count. */
export function disabledReason(key, rounds) {
  const on = rounds.filter((r) => r.ticketKey === key && !r.pending);
  if (on.length === 0) {
    return null;
  }
  return on.some((r) => r.active) ? 'pointing' : 'pointed';
}

function labelOf(options, value) {
  return options.find(([v]) => v === value)?.[1] ?? value;
}

export function createJiraBrowse(ctx) {
  const { teamId, jira, explain, openIssue, prefetchIssue, addRounds } = ctx;
  let filters = readFilters(teamId);
  const collapsed = new Set(readJson(storageKey(COLLAPSED_KEY, teamId)) ?? []);
  let data = null;
  let error = null;
  let loading = false;
  let rounds = [];
  let shownKey = '';
  let request = 0;
  let abort = null;
  let hoverTimer = 0;
  const menus = [];

  const search = h('input', {
    class: 'input',
    type: 'search',
    name: 'q',
    placeholder: 'Search summary or key',
    'aria-label': 'Search Jira',
    value: filters.q,
    style: { flex: '1', 'min-width': '0' },
    on: {
      keydown(event) {
        if (event.key === 'Enter') {
          event.preventDefault();
          setFilters({ q: search.value.trim() });
        }
      },
    },
  });
  const searchButton = createButton({ label: 'Search', icon: 'search', iconOnly: true, variant: 'secondary', size: 'icon', onClick: () => setFilters({ q: search.value.trim() }) });
  const filterButton = createButton({ label: 'Filters', icon: 'funnel', iconOnly: true, variant: 'ghost', size: 'icon', pressed: filters.showFilters, onClick: () => setFilters({ showFilters: !filters.showFilters }, false) });
  const refreshButton = createButton({ label: 'Refresh', icon: 'refresh-cw', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => load() });
  const newButton = createButton({ label: 'New ticket', icon: 'plus', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => createTicket() });

  function menuButton(name, options, label) {
    const button = createButton({ label: '', title: label, variant: 'outline', size: 'sm', className: `jira-filter-${name}` });
    const menu = createMenu({
      label,
      items: options.map(([action, text]) => ({ action, label: text })),
      onSelect: (value) => setFilters({ [name]: value }),
    });
    menu.attach(button.el);
    menus.push(menu);
    return { button, paint: () => button.update({ label: labelOf(options, filters[name]) }) };
  }

  const statusMenu = menuButton('status', STATUS, 'Status filter');
  const pointsMenu = menuButton('points', POINTS, 'Points filter');
  const scopeMenu = menuButton('scope', SCOPE, 'Sprint scope');
  const hideSwitch = createSwitch({ label: 'Hide pointed and pointing', checked: filters.hidePointed, onChange: (checked) => setFilters({ hidePointed: checked }, false) });
  const filterRow = h('div', { class: 'jira-filters', style: { display: 'flex', 'flex-wrap': 'wrap', gap: '0.375rem', 'align-items': 'center' } },
    statusMenu.button.el, pointsMenu.button.el, scopeMenu.button.el, hideSwitch.el);

  const manualInput = h('input', { class: 'input', name: 'ticketKey', placeholder: `Add by key, e.g. ${(jira.ticketPrefix ?? 'PROJ').replace(/-$/, '')}-123`, 'aria-label': 'Issue key to add', autocomplete: 'off', style: { flex: '1', 'min-width': '0' } });
  const manual = h('form', {
    class: 'jira-manual',
    style: ROW,
    on: {
      submit(event) {
        event.preventDefault();
        const key = manualInput.value.trim();
        if (!KEY_PATTERN.test(key)) {
          toast('Enter an issue key such as PROJ-123.', { variant: 'error', duration: 4000 });
          return;
        }
        addRounds([key.toUpperCase()]);
        toast(`Added ${key.toUpperCase()} to the rounds`, { variant: 'success' });
        manualInput.value = '';
      },
    },
  }, manualInput, createButton({ label: 'Add', icon: 'plus', variant: 'secondary', size: 'sm', type: 'submit' }).el);

  const notice = h('p', { class: 'jira-notice', role: 'status', hidden: true, style: { ...MUTED, margin: '0' } });
  const list = h('div', { class: 'jira-buckets', style: { flex: '1', overflow: 'auto', 'min-height': '0', display: 'grid', 'align-content': 'start', gap: '0.5rem' } });
  const el = h('section', {
    class: 'jira-browse',
    'aria-label': 'Browse Jira',
    style: { display: 'flex', 'flex-direction': 'column', gap: '0.5rem', height: '100%', padding: '0.75rem', 'box-sizing': 'border-box' },
  },
  h('header', { style: ROW }, h('h2', { text: 'Browse Jira', style: { flex: '1', 'font-size': '0.9375rem', 'font-weight': '600' } }), newButton.el, refreshButton.el, filterButton.el),
  h('div', { style: ROW }, search, searchButton.el),
  filterRow,
  manual,
  notice,
  list);

  // Rows are delegated: one listener set for the whole list.
  list.addEventListener('click', (event) => {
    const target = event.target.closest('[data-action]');
    if (!target || !list.contains(target)) {
      return;
    }
    const { action, key, bucket } = target.dataset;
    if (action === 'open') {
      openIssue(key);
    } else if (action === 'add') {
      addRounds([key]);
      toast(`Added ${key} to the rounds`, { variant: 'success' });
    } else if (action === 'add-all') {
      const keys = addable(data.buckets.find((b) => b.id === bucket));
      const count = addRounds(keys);
      toast(`Added ${plural(count, 'ticket')} to active rounds`, { variant: 'success' });
    } else if (action === 'collapse') {
      if (collapsed.has(bucket)) {
        collapsed.delete(bucket);
      } else {
        collapsed.add(bucket);
      }
      writeJson(storageKey(COLLAPSED_KEY, teamId), [...collapsed]);
      paint();
    }
  });
  list.addEventListener('pointerover', (event) => {
    const row = event.target.closest('[data-action="open"]');
    if (row && !row.contains(event.relatedTarget)) {
      clearTimeout(hoverTimer);
      hoverTimer = setTimeout(() => prefetchIssue(row.dataset.key), HOVER_PREFETCH_MS);
    }
  });
  list.addEventListener('pointerout', (event) => {
    const row = event.target.closest('[data-action="open"]');
    if (row && !row.contains(event.relatedTarget)) {
      clearTimeout(hoverTimer);
    }
  });

  function reasonOf(row) {
    return disabledReason(row.key, rounds) ?? row.roundState ?? null;
  }

  function addable(bucket) {
    return (bucket?.issues ?? []).filter((row) => reasonOf(row) === null).map((row) => row.key);
  }

  function setFilters(change, refetch = true) {
    filters = { ...filters, ...change };
    writeJson(storageKey(FILTERS_KEY, teamId), filters);
    paintFilters();
    if (refetch) {
      load();
    } else {
      paint();
    }
  }

  function paintFilters() {
    filterRow.hidden = !filters.showFilters;
    filterButton.update({ pressed: filters.showFilters });
    statusMenu.paint();
    pointsMenu.paint();
    scopeMenu.paint();
    hideSwitch.update({ checked: filters.hidePointed });
    if (document.activeElement !== search) {
      search.value = filters.q;
    }
  }

  async function load() {
    const mine = ++request;
    abort?.abort();
    abort = new AbortController();
    loading = true;
    paint();
    const query = new URLSearchParams({ status: filters.status, points: filters.points, scope: filters.scope });
    if (filters.q) {
      query.set('q', filters.q);
    }
    try {
      const body = await http.get(`/api/teams/${encodeURIComponent(teamId)}/jira/browse?${query}`, { signal: abort.signal });
      if (mine !== request) {
        return;
      }
      data = { buckets: Array.isArray(body?.buckets) ? body.buckets : [], partial: Boolean(body?.partial) };
      error = null;
    } catch (failure) {
      if (failure?.name === 'AbortError' || mine !== request) {
        return;
      }
      error = failure;
    }
    loading = false;
    paint();
  }

  function rowEl(row, reason) {
    const meta = h('span', { style: { ...ROW, ...MUTED, 'flex-wrap': 'wrap' } },
      statusBadge(row.status, row.statusCategory),
      priorityGlyph(row.priority),
      row.parentKey ? h('span', { class: 'badge jira-parent', title: row.parentSummary ?? row.parentKey, text: row.parentSummary ?? row.parentKey }) : null,
      row.points != null ? h('span', { class: 'badge jira-points', text: `${row.points} pts` }) : null);
    const open = h('button', {
      type: 'button',
      class: 'jira-row-open',
      dataset: { action: 'open', key: row.key },
      'aria-label': `${row.key}: ${row.summary ?? ''}`,
      style: { flex: '1', 'min-width': '0', display: 'grid', gap: '0.25rem', 'text-align': 'start', padding: '0.375rem 0.5rem', border: '0', background: 'transparent', color: 'inherit', cursor: 'pointer', 'border-radius': 'var(--radius-sm)' },
    },
    h('span', { style: ROW }, issueTypeGlyph(row.issueType), h('span', { class: 'jira-key', text: row.key, style: { 'font-family': 'var(--font-mono)', 'font-size': '0.75rem', 'font-weight': '600' } })),
    h('span', { class: 'jira-summary', text: row.summary ?? '', style: { 'font-size': '0.8125rem', 'overflow-wrap': 'anywhere' } }),
    meta);
    const action = reason
      ? h('span', { class: 'badge jira-reason', dataset: { reason }, text: REASON_LABEL[reason], title: reason === 'pointing' ? 'Already on an active round' : 'Already pointed in this session' })
      : createButton({ label: `Add ${row.key} to rounds`, icon: 'plus', iconOnly: true, variant: 'ghost', size: 'icon', action: 'add' }).el;
    if (!reason) {
      action.dataset.key = row.key;
    }
    return h('li', { class: 'jira-row', dataset: { key: row.key, reason: reason ?? undefined }, style: { ...ROW, 'border-bottom': '1px solid hsl(var(--border))' } }, open, action);
  }

  function bucketEl(bucket) {
    const hide = filters.hidePointed && !filters.q;
    const rows = bucket.issues.map((row) => [row, reasonOf(row)]).filter(([, reason]) => !(hide && reason));
    const open = !collapsed.has(bucket.id);
    const count = addable(bucket).length;
    const header = h('header', { style: { ...ROW, position: 'sticky', top: '0', background: 'hsl(var(--background))', 'z-index': '1' } },
      h('button', {
        type: 'button',
        class: 'jira-bucket-toggle',
        'aria-expanded': String(open),
        dataset: { action: 'collapse', bucket: bucket.id },
        style: { ...ROW, flex: '1', 'min-width': '0', border: '0', background: 'transparent', color: 'inherit', cursor: 'pointer', padding: '0.25rem 0', 'font-weight': '600', 'font-size': '0.8125rem', 'text-align': 'start' },
      },
      h('span', { 'aria-hidden': 'true', text: open ? '▾' : '▸' }),
      h('span', { class: 'jira-bucket-name', text: bucket.name }),
      bucket.state === 'active' ? h('span', { class: 'badge', text: 'Active' }) : null,
      h('span', { style: MUTED, text: `(${rows.length})` })),
      createButton({ label: 'Add all', icon: 'list-plus', variant: 'ghost', size: 'sm', disabled: count === 0, action: 'add-all', title: `Add ${plural(count, 'ticket')} from ${bucket.name}` }).el);
    header.lastChild.dataset.bucket = bucket.id;
    return h('section', { class: 'jira-bucket', dataset: { bucket: bucket.id }, 'aria-label': bucket.name },
      header,
      open ? h('ul', { class: 'jira-rows', style: { 'list-style': 'none', margin: '0', padding: '0' } }, rows.map(([row, reason]) => rowEl(row, reason))) : null);
  }

  function paint() {
    notice.hidden = !(error || data?.partial || loading);
    if (error) {
      notice.textContent = explain(error);
    } else if (loading && !data) {
      notice.textContent = 'Loading issues…';
    } else if (data?.partial) {
      notice.textContent = 'Some sprints could not be loaded from Jira.';
    } else {
      notice.hidden = true;
    }
    el.setAttribute('aria-busy', String(loading));
    if (!data) {
      return;
    }
    const scroll = list.scrollTop;
    const buckets = data.buckets.map(bucketEl);
    replaceContent(list, buckets.length > 0 ? buckets : h('p', { class: 'jira-empty', style: MUTED, text: 'No issues match these filters.' }));
    list.scrollTop = scroll;
  }

  function createTicket() {
    const summary = h('input', { class: 'input', name: 'summary', maxlength: '255', required: true, autocomplete: 'off', 'aria-label': 'Summary' });
    const description = h('textarea', { class: 'textarea', name: 'description', rows: '5', 'aria-label': 'Description' });
    const add = createSwitch({ label: 'Add to the rounds', checked: true });
    const form = h('form', {
      style: { display: 'grid', gap: '0.75rem' },
      on: {
        async submit(event) {
          event.preventDefault();
          const text = summary.value.trim();
          if (!text) {
            summary.focus();
            return;
          }
          save.update({ busy: true, disabled: true });
          try {
            const body = { summary: text };
            if (description.value.trim()) {
              body.description = description.value;
            }
            const created = await http.post(`/api/teams/${encodeURIComponent(teamId)}/jira/issues`, body);
            dialog.close('ok');
            toast(`Created ${created.key}`, { variant: 'success' });
            if (add.checked) {
              addRounds([created.key]);
            }
            load();
          } catch (failure) {
            toast(explain(failure), { variant: 'error', duration: 6000 });
            save.update({ busy: false, disabled: false });
          }
        },
      },
    },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Summary' }), summary),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Description (optional)' }), description),
    add.el);
    const save = createButton({ label: 'Create in Jira', variant: 'primary', onClick: () => form.requestSubmit() });
    const dialog = createDialog({
      title: 'New Jira ticket',
      content: form,
      actions: [createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }).el, save.el],
      onClose: () => dialog.destroy(),
    });
    dialog.open();
    summary.focus();
  }

  paintFilters();
  load();

  return {
    el,
    update(view) {
      rounds = view.rounds ?? [];
      const key = rounds.filter((r) => r.ticketKey && !r.pending).map((r) => `${r.ticketKey}:${r.active}`).sort().join(',');
      if (key !== shownKey) {
        shownKey = key;
        paint();
      }
    },
    refresh: load,
    destroy() {
      request++;
      abort?.abort();
      clearTimeout(hoverTimer);
      menus.forEach((menu) => menu.destroy());
      el.remove();
    },
  };
}
