// Jira on a team poker page (§f, §g.7, §g.8, docs/followups/UX1.md). `jiraFeature` is the poker screen's
// FEATURES entry: it reads the team's Jira configuration, shows the viewed round's issue at the centre of
// the stage and puts the browse panel on the left (both from lazy chunks), opens browsed issues in the
// drawer, runs the spotlight holder's pending preview, and draws the "Submit to Jira" chips in the results
// of a revealed round. The chips live here, in the poker chunk, because they must paint with the reveal.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { createButton } from '../ui/button.js';
import { createResizable } from '../ui/resizable.js';
import { toast } from '../ui/toast.js';
import { readPanels, writePanel } from './history.js';
import { roundById, uuidv7 } from './model.js';

export const SUBMIT_CHIPS = [1, 2, 3, 5, 8, 13, 21];
const MOBILE = '(max-width: 767px)';
const IDLE_PREFETCH_MS = 1500;

const JIRA_ERRORS = {
  jira_auth_failed: 'Jira refused the team’s credentials. A team admin can update them in Team Settings.',
  jira_forbidden: 'The team’s Jira account may not do that.',
  jira_not_found: 'Jira could not find that issue.',
  jira_rate_limited: 'Jira is busy. Try again in a moment.',
  jira_unavailable: 'Jira is unavailable right now. Try again in a moment.',
  jira_not_configured: 'Jira is not set up for this team.',
  jira_pending: 'A submit for this round is still in progress.',
  stale: 'The issue changed in Jira meanwhile.',
  forbidden: 'You are not allowed to do that.',
  network: 'You are offline. Try again when the connection is back.',
};

/** A sentence for a failed Jira call: an ApiError, a nack ({code, detail}), a bare code, or anything thrown. */
export function jiraMessage(error) {
  const code = typeof error === 'string' ? error : error?.code;
  if (code === 'jira_validation' && error?.fields) {
    const first = Object.values(error.fields).find((v) => typeof v === 'string');
    if (first) {
      return first;
    }
  }
  return JIRA_ERRORS[code] ?? (error?.detail || 'Something went wrong with Jira.');
}

/** The chip a revealed round suggests: the winner, or the nearest chip (the higher one on a tie). */
export function defaultChip(winner) {
  if (!Number.isFinite(winner) || winner <= 0) {
    return null;
  }
  return SUBMIT_CHIPS.reduce((best, chip) => (Math.abs(chip - winner) <= Math.abs(best - winner) ? chip : best));
}

function personName(view, pid) {
  return [...(view.roster ?? []), ...(view.presence ?? [])].find((p) => p.pid === pid)?.name ?? 'someone';
}

function formatPoints(points) {
  return `${points} ${points === 1 ? 'pt' : 'pts'}`;
}

/** A fixed side sheet (non-modal, so menus and toasts stay above it). Escape and the close button call onClose. */
export function createSheet({ side = 'right', label, width = '36rem', onClose }) {
  const close = createButton({ label: 'Close', icon: 'x', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => onClose() });
  const body = h('div', { class: 'jira-sheet-body', style: { flex: '1', overflow: 'auto', padding: '0 1rem 1rem' } });
  const title = h('h2', { class: 'jira-sheet-title', style: { 'font-size': '1rem', 'font-weight': '600', flex: '1', 'min-width': '0' } });
  const tools = h('div', { style: { display: 'flex', gap: '0.25rem', 'align-items': 'center' } });
  const el = h('aside', {
    class: ['jira-sheet', `jira-sheet-${side}`],
    role: 'dialog',
    'aria-label': label,
    tabindex: '-1',
    hidden: true,
    style: {
      position: 'fixed',
      top: '0',
      bottom: '0',
      [side]: '0',
      width: `min(${width}, 100vw)`,
      'z-index': '900',
      display: 'flex',
      'flex-direction': 'column',
      background: 'hsl(var(--background))',
      color: 'hsl(var(--foreground))',
      'border-inline': '1px solid hsl(var(--border))',
      'box-shadow': 'var(--shadow-lg)',
    },
    on: {
      keydown(event) {
        if (event.key === 'Escape' && !event.defaultPrevented) {
          event.preventDefault();
          onClose();
        }
      },
    },
  },
  h('header', { style: { display: 'flex', 'align-items': 'center', gap: '0.5rem', padding: '0.75rem 1rem' } }, title, tools, close.el),
  body);
  let returnFocus = null;
  return {
    el,
    body,
    title,
    tools,
    get isOpen() {
      return !el.hidden;
    },
    open() {
      if (!el.isConnected) {
        document.body.append(el);
      }
      if (el.hidden) {
        returnFocus = document.activeElement;
        el.hidden = false;
        el.focus();
      }
    },
    hide() {
      if (!el.hidden) {
        el.hidden = true;
        if (returnFocus?.isConnected) {
          returnFocus.focus();
        }
        returnFocus = null;
      }
    },
    destroy() {
      el.remove();
    },
  };
}

/** The submit chips and the shared submit state of one revealed round. */
function createSubmit({ onSubmit }) {
  const chips = h('div', { class: 'jira-submit-chips', role: 'group', 'aria-label': 'Points to submit' });
  const send = createButton({ label: 'Submit to Jira', icon: 'send', variant: 'emerald', press: true, size: 'sm', onClick: () => onSubmit(chosen) });
  const status = h('p', { class: 'jira-submit-status', role: 'status' });
  const el = h('section', {
    class: 'jira-submit',
    'aria-label': 'Submit points to Jira',
    hidden: true,
  }, chips, send.el, status);
  const buttons = SUBMIT_CHIPS.map((points) => createButton({
    label: String(points),
    variant: 'default',
    size: 'sm',
    press: true,
    pressed: false,
    className: 'jira-submit-chip',
    onClick() {
      chosen = points;
      picked.set(roundId, points);
      paint();
    },
  }));
  chips.append(...buttons.map((b) => b.el));
  const picked = new Map();
  let roundId = null;
  let chosen = null;
  let last = null;

  function paint() {
    const { round, submit, canEdit, names } = last;
    const pending = submit?.status === 'pending';
    const blocked = !round.ticketKey || !canEdit;
    buttons.forEach((button, i) => button.update({ pressed: SUBMIT_CHIPS[i] === chosen, disabled: blocked || pending }));
    send.update({
      label: chosen == null || !round.ticketKey ? 'Submit to Jira' : `Submit ${formatPoints(chosen)} to ${round.ticketKey}`,
      disabled: blocked || pending || chosen == null,
      busy: pending,
    });
    el.dataset.state = submit?.status ?? (round.jira ? 'ok' : 'idle');
    if (!round.ticketKey) {
      status.textContent = 'Add a Jira issue key to this round to submit points.';
    } else if (pending) {
      status.textContent = `Submitting ${formatPoints(submit.points)} to ${submit.key}… (${names(submit.by)})`;
    } else if (submit?.status === 'failed') {
      status.textContent = `Could not submit ${formatPoints(submit.points)} to ${submit.key}: ${jiraMessage(submit.code)}`;
    } else if (round.jira) {
      status.textContent = `${round.ticketKey} has ${formatPoints(round.jira.points)} in Jira · submitted by ${names(round.jira.by)}`;
    } else if (!canEdit) {
      status.textContent = 'Only team admins can write to Jira in this team.';
    } else {
      status.textContent = '';
    }
  }

  return {
    el,
    update(state) {
      el.hidden = !state;
      if (!state) {
        return;
      }
      if (state.round.id !== roundId) {
        roundId = state.round.id;
        chosen = picked.get(roundId) ?? defaultChip(state.round.winner);
      }
      last = state;
      paint();
    },
  };
}

/**
 * The poker screen's Jira feature (docs/decisions/U31.md contract 4). Quick sessions and teams without
 * Jira get nothing; everything else loads on demand.
 */
export function jiraFeature(ctx) {
  const { room, me, slots } = ctx;
  const toggle = createButton({ label: 'Browse Jira', icon: 'panel-left', variant: 'ghost', size: 'sm', pressed: false, className: 'jira-toggle', onClick: () => setPanel(!panelOpen, true) });
  toggle.el.hidden = true;
  slots.toolbar.append(toggle.el);
  const submit = createSubmit({ onSubmit: submitPoints });

  let view = null;
  let roundId = null;
  let teamId = null;
  let team = null;
  let browse = null;
  let browseLoad = null;
  let drawer = null;
  let drawerLoad = null;
  let issuePanel = null;
  let sheet = null;
  let resizer = null;
  let panelOpen = false;
  let preview = null;
  let idle = 0;
  let destroyed = false;

  const canEdit = () => Boolean(team && (team.jira.membersCanEdit !== false || team.role === 'admin' || team.role === 'owner'));
  const mobile = () => globalThis.matchMedia?.(MOBILE).matches ?? false;
  const holdsSpotlight = () => view?.spotlight?.holder === me && view.heldByThisTab === true;
  const send = (k, d, options) => room.send(k, d, options);

  const shared = () => ({
    teamId,
    jira: team.jira,
    canEdit: canEdit(),
    room,
    me,
    uuidv7,
    explain: jiraMessage,
    createSheet,
    openIssue,
    prefetchIssue,
    addRounds,
  });

  async function loadTeam(id) {
    try {
      const body = await http.get(`/api/teams/${encodeURIComponent(id)}`);
      if (destroyed || id !== teamId) {
        return;
      }
      team = body?.jira?.configured ? { role: body.role, jira: body.jira } : null;
    } catch {
      team = null;
    }
    if (!team) {
      return;
    }
    toggle.el.hidden = false;
    loadDrawer();
    idle = setTimeout(loadBrowse, IDLE_PREFETCH_MS);
    if (!mobile() && readPanels().left?.open !== false) {
      setPanel(true, false);
    }
    render();
  }

  function loadBrowse() {
    browseLoad ??= import('./jira-browse.js').then((module) => {
      if (!destroyed) {
        browse = module.createJiraBrowse(shared());
        if (view) {
          browse.update(view);
        }
      }
      return browse;
    });
    return browseLoad;
  }

  function loadDrawer() {
    drawerLoad ??= import('./issue-drawer.js').then((module) => {
      if (!destroyed) {
        drawer = module.createIssueDrawer({ ...shared(), onClose: endPreview });
        issuePanel = module.createIssuePanel({ ...shared(), scroller: slots.issueScroller, onLink: () => ctx.actions.editTicket() });
        slots.issue.append(issuePanel.el);
        render();
      }
      return drawer;
    });
    return drawerLoad;
  }

  async function setPanel(open, remember) {
    panelOpen = open;
    toggle.update({ pressed: open });
    if (remember && !mobile()) {
      writePanel('left', { open });
    }
    if (!open) {
      slots.left.hidden = true;
      sheet?.hide();
      return;
    }
    const panel = await loadBrowse();
    if (!panel || !panelOpen) {
      return;
    }
    if (mobile()) {
      sheet ??= createSheet({ side: 'left', label: 'Browse Jira', width: '28rem', onClose: () => setPanel(false, false) });
      sheet.body.append(panel.el);
      sheet.open();
    } else {
      // Resizable 240–480 px like the Round panel (§g.8); the width is kept in rs.panels.left.
      resizer ??= createResizable({
        panel: slots.left,
        side: 'right',
        width: readPanels().left?.width ?? 320,
        label: 'Resize Jira panel',
        onCommit: (width) => writePanel('left', { width }),
      });
      slots.left.classList.add('jira-panel');
      slots.left.replaceChildren(panel.el, resizer.el);
      slots.left.hidden = false;
    }
  }

  function prefetchIssue(key) {
    loadDrawer().then((d) => d?.prefetch(key));
  }

  /** Opens the drawer; the spotlight holder's open also previews the issue to everyone as a pending round. */
  async function openIssue(key) {
    if (!key || !team) {
      return;
    }
    let previewId = null;
    if (holdsSpotlight() && !view.rounds.some((r) => r.ticketKey === key && !r.pending)) {
      endPreview();
      previewId = uuidv7();
      preview = { roundId: previewId, key, committed: false };
      send('poker.round.create', { roundId: previewId, ticketKey: key, pending: true, forceNew: true });
    }
    const d = await loadDrawer();
    d?.open(key, previewId ? { onAdd: () => commitPreview(previewId) } : {});
  }

  function commitPreview(id) {
    if (preview?.roundId !== id || preview.committed) {
      return;
    }
    preview.committed = true;
    send('poker.round.commitPending', { roundId: id });
    toast(`Added ${preview.key} to the rounds`, { variant: 'success' });
  }

  // A preview the holder walks away from is deleted; it may already be gone (spotlight lost, round deleted).
  function endPreview() {
    if (preview && !preview.committed) {
      send('poker.round.discardPending', { roundId: preview.roundId }, { onNack: () => true });
    }
    preview = null;
  }

  function addRounds(keys) {
    const fresh = keys.filter(Boolean);
    if (fresh.length === 1) {
      send('poker.round.create', { roundId: uuidv7(), ticketKey: fresh[0] });
    } else {
      for (let i = 0; i < fresh.length; i += 50) {
        send('poker.round.createMany', { rounds: fresh.slice(i, i + 50).map((ticketKey) => ({ roundId: uuidv7(), ticketKey })) });
      }
    }
    return fresh.length;
  }

  function submitPoints(points) {
    const round = roundById(view, roundId);
    if (round && points != null) {
      send('poker.jira.submitPoints', { roundId: round.id, points }, {
        onNack(nack) {
          toast(jiraMessage({ code: nack.code, detail: nack.msg }), { variant: 'error', duration: 5000 });
          return true;
        },
      });
    }
  }

  function render() {
    if (!view || !team) {
      submit.update(null);
      return;
    }
    const round = roundById(view, roundId);
    issuePanel?.show(round?.ticketKey ?? null);
    if (preview && !preview.committed && view.rounds.some((r) => r.id === preview.roundId && !r.pending)) {
      preview.committed = true;
    }
    if (submit.el.parentNode !== slots.results) {
      slots.results.append(submit.el);
    }
    submit.update(round && round.state === 'revealed' && !round.pending
      ? { round, submit: view.submits?.[round.id] ?? null, canEdit: canEdit(), names: (pid) => personName(view, pid) }
      : null);
    browse?.update(view);
  }

  return {
    update(next, id) {
      view = next;
      roundId = id;
      if (next.teamId && next.teamId !== teamId) {
        teamId = next.teamId;
        loadTeam(teamId);
      }
      render();
    },
    destroy() {
      destroyed = true;
      clearTimeout(idle);
      endPreview();
      browse?.destroy();
      drawer?.destroy();
      issuePanel?.destroy();
      sheet?.destroy();
      submit.el.remove();
      toggle.el.remove();
      resizer?.destroy();
      slots.left.hidden = true;
      slots.left.classList.remove('jira-panel');
      slots.left.style.removeProperty('width');
      replaceContent(slots.left);
    },
  };
}
