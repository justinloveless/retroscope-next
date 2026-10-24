// The sprint report panel (UX17, §g chunk sprint-report): a side sheet beside the board with a snapshot of
// one sprint as Jira holds it now: what got done, what got done after the sprint closed, and what did not,
// with where it went. The viewer picks the sprint and the pick is remembered per board (the sprint id only);
// Jira text lives in memory only (tenet 4).

import { rovingTabindex } from '../core/a11y.js';
import { plural } from '../core/fmt.js';
import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { jiraMessage } from '../core/jira-errors.js';
import { createButton } from '../ui/button.js';
import { issueTypeGlyph, statusBadge } from '../ui/glyphs.js';
import { icon } from '../ui/icons.js';
import { createPopover } from '../ui/popover.js';
import { createSideSheet } from '../ui/side-sheet.js';
import { toast } from '../ui/toast.js';

export const CACHE_MS = 60_000;
export const PICK_KEY = 'rs.sprint';
// Closed sprints the picker lists at first, how many more each "Load more" asks for, and the server's limit.
export const SPRINTS = 4;
const MORE = 8;
const MAX_SPRINTS = 100;
const WIDTH = '26rem';

const OUTCOMES = {
  done: 'Done',
  doneAfter: 'Done after close',
  notDone: 'Not done',
};

const EMPTY = {
  no_board: 'The team’s Jira settings have no board. A team admin can choose one in Team Settings.',
  no_sprints: 'The team’s Jira board has no active or closed sprints yet.',
};

// The report asks Jira for a board and a sprint, never for one issue.
const ERRORS = {
  jira_not_found: 'Jira could not find the team’s board or that sprint. A team admin can check the board in Team Settings.',
};

export const FOOTNOTE = 'A snapshot of the sprint as Jira holds it now. Issues removed before the sprint closed are not listed, '
  + 'issues added during the sprint are not marked, and points are current estimates.';
export const NO_POINTS = 'The team has no story points field in its Jira settings, so points are not shown.';
export const TRUNCATED = 'This sprint has more issues than the report reads; the counts cover the first 1,000.';

const day = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });

// Shared by every panel of the tab: `${teamId}:${sprintId | 'default'}` → {at, promise}. The key leaves out how many
// sprints were listed: a panel keeps the longest list it has seen, so a cached shorter one only costs nothing.
const cache = new Map();

export const sprintLabel = (s) => (s.state === 'active' ? `${s.name} (active)` : s.name);

function formatDay(value) {
  const at = value ? Date.parse(value) : Number.NaN;
  return Number.isNaN(at) ? null : day.format(at);
}

function points(value) {
  return value == null ? null : plural(value, 'pt');
}

function chipsOf(issue, outcome, closed) {
  const chips = [];
  if (issue.carriedIn) {
    chips.push({ kind: 'carried', text: 'Carried in' });
  }
  // Work finished after the close says where it was finished only when that was a later sprint.
  if (closed && issue.next) {
    chips.push({ kind: 'next', text: `→ ${issue.next}` });
  } else if (closed && outcome === 'notDone') {
    chips.push({ kind: 'backlog', text: '→ Backlog' });
  }
  return chips;
}

/**
 * The sprint-report response → what the panel draws. Only a closed sprint has a "done after close" outcome
 * and a place its unfinished work went; points show only when the team has a points field.
 */
export function viewModel(body, { siteHost }) {
  const sprints = Array.isArray(body?.sprints) ? body.sprints : [];
  const report = body?.report ?? null;
  const sprint = report ? sprints.find((s) => s.id === report.sprintId) ?? null : null;
  const closed = sprint?.state === 'closed';
  const pointsKnown = report?.pointsKnown === true;
  const groups = (report?.groups ?? []).filter((g) => OUTCOMES[g.outcome] && (closed || g.outcome !== 'doneAfter'));
  const start = formatDay(sprint?.startDate);
  const end = formatDay(sprint?.endDate);
  const range = start && end ? `${start} – ${end}` : start ?? end;
  const completed = closed ? formatDay(sprint.completeDate) : null;
  const phase = closed ? (completed ? `closed ${completed}` : 'closed') : 'active';
  const notes = [];
  if (report?.truncated) {
    notes.push(TRUNCATED);
  }
  if (report && !pointsKnown) {
    notes.push(NO_POINTS);
  }
  return {
    empty: report ? null : body?.empty ?? 'no_sprints',
    picker: sprints.map((s) => ({ value: String(s.id), label: sprintLabel(s) })),
    selected: sprint ? String(sprint.id) : null,
    heading: sprint && {
      name: sprint.name,
      goal: report.goal || null,
      dates: [range, phase].filter(Boolean).join(' · '),
    },
    tiles: groups.map((g) => ({ outcome: g.outcome, label: OUTCOMES[g.outcome], count: g.count, points: pointsKnown ? points(g.points) : null })),
    groups: groups.filter((g) => g.issues?.length > 0).map((g) => ({
      outcome: g.outcome,
      title: OUTCOMES[g.outcome],
      rows: g.issues.map((issue) => ({
        key: issue.key,
        href: siteHost ? `https://${siteHost}/browse/${encodeURIComponent(issue.key)}` : null,
        summary: issue.summary ?? '',
        issueType: issue.issueType ?? null,
        status: issue.status ?? null,
        statusCategory: issue.statusCategory ?? null,
        assignee: issue.assignee ?? null,
        points: pointsKnown ? points(issue.points) : null,
        chips: chipsOf(issue, g.outcome, closed),
      })),
    })),
    notes: report ? [...notes, FOOTNOTE] : [],
  };
}

function fresh(entry) {
  return entry != null && Date.now() - entry.at < CACHE_MS;
}

/**
 * GET …/sprint-report listing `sprints` closed sprints, cached in memory for a minute per team and sprint, shared
 * while in flight, dropped on failure. A forced fetch replaces every fresh entry that showed the same sprint, the
 * default one included.
 */
export function fetchReport(teamId, sprintId, { signal, force = false, sprints = SPRINTS } = {}) {
  const id = `${teamId}:${sprintId ?? 'default'}`;
  const hit = cache.get(id);
  if (!force && fresh(hit)) {
    return hit.promise;
  }
  const query = sprintId == null ? '' : `sprint=${encodeURIComponent(sprintId)}&`;
  const promise = http.get(`/api/teams/${encodeURIComponent(teamId)}/jira/sprint-report?${query}sprints=${sprints}`, { signal });
  const at = Date.now();
  cache.set(id, { at, promise, own: null });
  promise.then((body) => {
    const own = body?.report ? `${teamId}:${body.report.sprintId}` : null;
    if (!own || cache.get(id)?.promise !== promise) {
      return;
    }
    const entry = { at, promise, own };
    cache.set(id, entry);
    // The default sprint's report is also that sprint's, so picking it next costs no request.
    if (force || !fresh(cache.get(own))) {
      cache.set(own, entry);
    }
    if (force) {
      for (const [key, other] of cache) {
        if (other.own === own && fresh(other)) {
          cache.set(key, entry);
        }
      }
    }
  }, () => {
    if (cache.get(id)?.promise === promise) {
      cache.delete(id);
    }
  });
  return promise;
}

function readPick(boardId) {
  try {
    const value = localStorage.getItem(`${PICK_KEY}:${boardId}`);
    return value && /^[1-9]\d*$/.test(value) ? Number(value) : null;
  } catch {
    return null;
  }
}

function writePick(boardId, sprintId) {
  try {
    if (sprintId == null) {
      localStorage.removeItem(`${PICK_KEY}:${boardId}`);
    } else {
      localStorage.setItem(`${PICK_KEY}:${boardId}`, String(sprintId));
    }
  } catch {
    // Private mode or a full quota: the pick lasts for this page view.
  }
}

function rowEl(row) {
  const key = row.href
    ? h('a', { class: 'retro-sprint-key', href: row.href, target: '_blank', rel: 'noopener noreferrer', title: `Open ${row.key} in Jira`, text: row.key })
    : h('span', { class: 'retro-sprint-key', text: row.key });
  return h('li', { class: 'retro-sprint-row', dataset: { key: row.key } },
    h('div', { class: 'retro-sprint-row-top' },
      issueTypeGlyph(row.issueType),
      key,
      row.points ? h('span', { class: 'badge retro-sprint-points', text: row.points }) : null),
    h('p', { class: 'retro-sprint-summary', text: row.summary }),
    h('div', { class: 'retro-sprint-meta' },
      row.status ? statusBadge(row.status, row.statusCategory) : null,
      row.assignee ? h('span', { class: 'retro-sprint-assignee', text: row.assignee }) : null,
      row.chips.map((chip) => h('span', { class: 'retro-sprint-chip', dataset: { kind: chip.kind }, text: chip.text }))));
}

function reportEl(model) {
  const { heading } = model;
  return [
    h('div', { class: 'retro-sprint-head' },
      heading.goal ? h('p', { class: 'retro-sprint-goal' }, h('span', { class: 'retro-sprint-label', text: 'Goal' }), ' ', heading.goal) : null,
      heading.dates ? h('p', { class: 'retro-sprint-dates', text: heading.dates }) : null),
    h('div', { class: 'retro-sprint-tiles' }, model.tiles.map((tile) => h('div', { class: 'retro-sprint-tile', dataset: { outcome: tile.outcome } },
      h('span', { class: 'retro-sprint-count', text: String(tile.count) }),
      h('span', { class: 'retro-sprint-tile-label', text: tile.label }),
      tile.points ? h('span', { class: 'retro-sprint-tile-points', text: tile.points }) : null))),
    model.groups.length === 0
      ? h('p', { class: 'retro-sprint-empty', text: 'No issues in this sprint.' })
      : model.groups.map((group) => h('section', { class: 'retro-sprint-group', dataset: { outcome: group.outcome }, 'aria-label': group.title },
        h('h3', { class: 'retro-sprint-group-title' }, group.title, h('span', { class: 'retro-sprint-group-count', text: String(group.rows.length) })),
        h('ul', { class: 'retro-sprint-rows' }, group.rows.map(rowEl)))),
    model.notes.map((note) => h('p', { class: 'retro-sprint-note', text: note })),
  ];
}

/**
 * The panel of one board: toggle() opens it and loads the remembered sprint (or the server's default),
 * close() hides it and abandons a load in flight. `dock` gets .has-side-sheet while open, so the board
 * makes room for the sheet on wide screens.
 */
export function createSprintReport({ teamId, boardId, siteHost, dock, onClose }) {
  const sheet = createSideSheet({ label: 'Sprint report', width: WIDTH, onClose: () => close() });
  sheet.el.classList.add('retro-sprint-sheet');
  sheet.title.textContent = 'Sprint report';
  const refresh = createButton({ label: 'Refresh', icon: 'refresh-cw', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => load(wanted, { force: true }) });
  sheet.tools.append(refresh.el);
  const picker = h('button', { type: 'button', class: 'select choice retro-sprint-picker', 'aria-haspopup': 'menu', 'aria-expanded': 'false', hidden: true });
  const items = h('div', { class: 'menu-list' });
  const more = h('button', { type: 'button', role: 'menuitem', class: 'menu-item retro-sprint-more', tabindex: '-1' });
  const menu = createPopover({ anchor: picker, content: [items, more], role: 'menu', label: 'Sprints', className: 'menu retro-sprint-menu' });
  menu.el.removeAttribute('tabindex');
  const roving = rovingTabindex(menu.el, { selector: '.menu-item', orientation: 'vertical' });
  const content = h('div', { class: 'retro-sprint-content' });
  sheet.body.append(h('div', { class: 'retro-sprint' }, picker, content));

  let request = 0;
  let abort = null;
  // The latest load's sprint, so Refresh during a pending pick reloads the pick rather than the painted sprint.
  let wanted = null;
  let painted = false;
  // The picker of this opening: the longest sprint list a response gave, and how many closed sprints to ask for.
  let list = { sprints: [], more: false };
  let count = SPRINTS;
  let selected = null;
  let loadingMore = false;
  let opening = 0;

  function take(body, force) {
    const sprints = Array.isArray(body?.sprints) ? body.sprints : [];
    // Every response lists a prefix of the same sprints (active, then closed newest first), so the longer one holds the other.
    if (force || sprints.length >= list.sprints.length) {
      list = { sprints, more: body?.hasMoreSprints === true };
    }
  }

  function renderPicker() {
    const chosen = list.sprints.find((s) => String(s.id) === selected);
    picker.hidden = list.sprints.length === 0;
    picker.textContent = chosen ? sprintLabel(chosen) : '';
    picker.setAttribute('aria-label', `Sprint: ${picker.textContent}`);
    items.replaceChildren(...list.sprints.map((s) => {
      const on = String(s.id) === selected;
      return h('button', { type: 'button', role: 'menuitemradio', class: 'menu-item', 'aria-checked': String(on), tabindex: '-1', dataset: { id: s.id } },
        icon(on ? 'check' : 'circle'), h('span', { text: sprintLabel(s) }));
    }));
    more.hidden = !list.more;
    more.setAttribute('aria-disabled', String(loadingMore));
    more.replaceChildren(loadingMore ? h('span', { class: 'spinner', 'aria-hidden': 'true' }) : icon('chevron-down'),
      h('span', { text: loadingMore ? 'Loading sprints…' : 'Load more sprints' }));
    roving.refresh();
    if (menu.isOpen) {
      menu.reposition();
    }
  }

  async function loadMore() {
    if (loadingMore || !list.more) {
      return;
    }
    const mine = opening;
    const added = list.sprints.length;
    const focused = more.contains(document.activeElement);
    count = Math.min(MAX_SPRINTS, list.sprints.filter((s) => s.state === 'closed').length + MORE);
    loadingMore = true;
    renderPicker();
    try {
      take(await fetchReport(teamId, wanted, { force: true, sprints: count }), true);
    } catch (error) {
      if (mine === opening) {
        toast(jiraMessage(error, ERRORS), { variant: 'error', duration: 6000 });
      }
    }
    if (mine !== opening) {
      return;
    }
    loadingMore = false;
    renderPicker();
    // Keyboard users who chose "Load more" land on the first sprint it added.
    if (focused) {
      (items.children[added] ?? more).focus();
    }
  }

  function openMenu() {
    menu.open();
    const item = items.querySelector('[aria-checked="true"]') ?? items.firstElementChild;
    if (item) {
      roving.refresh();
      item.tabIndex = 0;
      item.focus();
    }
  }

  picker.addEventListener('click', () => (menu.isOpen ? menu.close() : openMenu()));
  picker.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown' && !menu.isOpen) {
      event.preventDefault();
      openMenu();
    }
  });
  menu.el.addEventListener('click', (event) => {
    const item = event.target.closest('[role=menuitemradio]');
    if (item) {
      menu.close();
      picker.focus();
      if (item.getAttribute('aria-checked') !== 'true') {
        pick(Number(item.dataset.id));
      }
    } else if (event.target.closest('.retro-sprint-more')) {
      loadMore();
    }
  });
  menu.el.addEventListener('scroll', () => {
    if (menu.el.scrollTop + menu.el.clientHeight >= menu.el.scrollHeight - 24) {
      loadMore();
    }
  }, { passive: true });

  function status(text, { role = 'status', retry = null } = {}) {
    replaceContent(content, h('p', { class: 'retro-sprint-status', role, text }), retry);
  }

  async function load(sprintId, { force = false } = {}) {
    const mine = ++request;
    wanted = sprintId;
    abort?.abort();
    abort = new AbortController();
    content.setAttribute('aria-busy', 'true');
    if (!force) {
      painted = false;
      status('Loading the sprint from Jira…');
    }
    let body;
    try {
      body = await fetchReport(teamId, sprintId, { signal: abort.signal, force, sprints: count });
    } catch (error) {
      if (mine !== request || error?.name === 'AbortError') {
        return;
      }
      content.setAttribute('aria-busy', 'false');
      // A remembered sprint can age out of the picker; the server's default takes its place.
      if (sprintId != null && error?.code === 'jira_not_found' && sprintId === readPick(boardId)) {
        writePick(boardId, null);
        load(null);
        return;
      }
      // A failed refresh keeps the report it was refreshing.
      if (force && painted) {
        toast(jiraMessage(error, ERRORS), { variant: 'error', duration: 6000 });
        return;
      }
      painted = false;
      status(jiraMessage(error, ERRORS), {
        role: 'alert',
        retry: createButton({ label: 'Try again', icon: 'refresh-cw', variant: 'outline', size: 'sm', onClick: () => load(sprintId) }),
      });
      return;
    }
    if (mine !== request) {
      return;
    }
    content.setAttribute('aria-busy', 'false');
    const model = viewModel(body, { siteHost });
    wanted = model.selected === null ? sprintId : Number(model.selected);
    selected = model.selected;
    take(body, force);
    renderPicker();
    painted = !model.empty;
    if (model.empty) {
      status(EMPTY[model.empty] ?? EMPTY.no_sprints);
    } else {
      replaceContent(content, reportEl(model));
    }
  }

  function pick(sprintId) {
    writePick(boardId, sprintId);
    load(sprintId);
  }

  function open() {
    sheet.open();
    dock?.classList.add('has-side-sheet');
    load(readPick(boardId));
  }

  function close() {
    if (!sheet.isOpen) {
      return;
    }
    request++;
    opening++;
    abort?.abort();
    menu.close();
    list = { sprints: [], more: false };
    count = SPRINTS;
    loadingMore = false;
    sheet.hide();
    dock?.classList.remove('has-side-sheet');
    onClose?.();
  }

  return {
    get isOpen() {
      return sheet.isOpen;
    },
    toggle() {
      if (sheet.isOpen) {
        close();
      } else {
        open();
      }
    },
    close,
    destroy() {
      close();
      roving.destroy();
      menu.destroy();
      sheet.destroy();
    },
  };
}
