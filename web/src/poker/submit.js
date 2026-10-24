// Jira on a team poker page (§f, §g.7, §g.8, docs/followups/UX1.md). `jiraFeature` is the poker screen's
// FEATURES entry: it reads the team's Jira configuration, shows the viewed round's issue at the centre of
// the stage and puts the browse panel on the left (both from lazy chunks), opens browsed issues in the
// drawer, runs the spotlight holder's pending preview, and draws the "Submit to Jira" chips in the results
// of a revealed round. The chips live here, in the poker chunk, because they must paint with the reveal.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { jiraMessage } from '../core/jira-errors.js';
import { createButton } from '../ui/button.js';
import { createSidePanel } from '../ui/side-panel.js';
import { createSideSheet } from '../ui/side-sheet.js';
import { toast } from '../ui/toast.js';
import { readPanels, writePanel } from './history.js';
import { roundById, uuidv7 } from './model.js';

export const SUBMIT_CHIPS = [1, 2, 3, 5, 8, 13, 21];
const MOBILE = '(max-width: 767px)';
const IDLE_PREFETCH_MS = 1500;

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

/** The submit chips and the shared submit state of one revealed round. */
function createSubmit({ onSubmit }) {
  const chips = h('div', { class: 'jira-submit-chips', role: 'group', 'aria-label': 'Points to submit' });
  const send = createButton({ label: 'Submit to Jira', icon: 'send', variant: 'emerald', press: true, size: 'sm', onClick: () => onSubmit(chosen) });
  const status = h('p', { class: 'jira-submit-status', role: 'status' });
  // While the results are minimized, the table puts Show results in this row, beside Submit.
  const sendRow = h('div', { class: 'jira-submit-send', dataset: { resultsInline: '' } }, send.el);
  const el = h('section', {
    class: 'jira-submit',
    'aria-label': 'Submit points to Jira',
    hidden: true,
  }, chips, sendRow, status);
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
  // Phones have no side panels; the bottom bar's button opens the browse sheet instead.
  const mobileOpen = createButton({ label: 'Browse Jira', icon: 'search', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: () => setPanel(true, false) });
  mobileOpen.el.hidden = true;
  slots.bottom.append(mobileOpen.el);
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
  let side = null;
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
    createSheet: createSideSheet,
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
    mobileOpen.el.hidden = false;
    // Resizable 240–480 px like the Round panel (§g.8); open and width are kept in rs.panels.left.
    side ??= createSidePanel({
      panel: slots.left,
      edge: 'left',
      name: 'Jira panel',
      openIcon: 'search',
      width: readPanels().left?.width ?? 320,
      open: false,
      onToggle: (open) => setPanel(open, true),
      onCommit: (width) => writePanel('left', { width }),
    });
    slots.left.classList.add('jira-panel');
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
    }, (error) => {
      // Forget the failed chunk so the next open retries it.
      browseLoad = null;
      throw error;
    });
    return browseLoad;
  }

  function loadDrawer() {
    drawerLoad ??= import('./issue-drawer.js').then((module) => {
      if (!destroyed) {
        drawer = module.createIssueDrawer({ ...shared(), onClose: endPreview });
        issuePanel = module.createIssuePanel({
          ...shared(),
          scroller: slots.issueScroller,
          onLink: () => ctx.actions.editTicket(),
          openLinked: (key) => drawer?.open(key),
        });
        slots.issue.append(issuePanel.el);
        render();
      }
      return drawer;
    });
    return drawerLoad;
  }

  async function setPanel(open, remember) {
    panelOpen = open;
    if (remember && !mobile()) {
      writePanel('left', { open });
    }
    if (!open) {
      side?.setOpen(false, { focus: remember });
      sheet?.hide();
      return;
    }
    if (!mobile()) {
      side?.setOpen(true, { focus: remember });
    }
    let panel;
    try {
      panel = await loadBrowse();
    } catch {
      if (panelOpen) {
        panelOpen = false;
        side?.setOpen(false);
        toast('Could not load the Jira panel. Try again.', { variant: 'error' });
      }
      return;
    }
    if (!panel || !panelOpen) {
      return;
    }
    if (mobile()) {
      sheet ??= createSideSheet({ side: 'left', label: 'Browse Jira', width: '28rem', onClose: () => setPanel(false, false) });
      sheet.body.append(panel.el);
      sheet.open();
    } else if (panel.el.parentNode !== slots.left) {
      slots.left.prepend(panel.el);
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
      mobileOpen.el.remove();
      side?.destroy();
      slots.left.hidden = true;
      slots.left.classList.remove('jira-panel');
      replaceContent(slots.left);
    },
  };
}
