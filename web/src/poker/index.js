// The poker screen (/poker/:code and /teams/:tid/poker/:code): joins the room, paints from the boot
// snapshot, follows pointer and spotlight navigation (R13), keeps ?round=N in the URL and wires the stage
// (the issue with the players seated around it), the hand, drag-to-play and the shortcuts to the room's
// commands. The toolbar stands in for the app header (UX18 decision 12): "<team> / Poker / <day>" from the
// snapshot's teamId, Copy link, and the header's mentions and account menu docked at its end. The address
// becomes /teams/<teamId>/poker/<code> once the snapshot names the team.

import { h, replaceContent } from '../core/h.js';
import { ApiError, http } from '../core/http.js';
import { shallowEq } from '../core/store.js';
import { formatDay } from '../core/day.js';
import { currentMe, getSocket, navigate as go, session, setHeaderTeam } from '../screens/app.js';
import { navLink, teamBadge, teamPath } from '../screens/header.js';
import { kickScreen, showNoAccess } from '../screens/no-access.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { createDrag } from './drag.js';
import { createHand } from './hand.js';
import { createHistory } from './history.js';
import { bindKeyboard } from './keyboard.js';
import { createMobile } from './mobile.js';
import {
  abstainIntent,
  initialRound,
  isObserver,
  lockIntent,
  myVote,
  pokerModel,
  resolveNav,
  roundById,
  roundPhase,
  ticketIntent,
  uuidv7,
  uuidv7Time,
  voteIntent,
} from './model.js';
import { createObserverToggle } from './observers.js';
import { createSettings } from './settings.js';
import { createSpotlight } from './spotlight.js';
import { createStrip } from './strip.js';
import { jiraFeature } from './submit.js';
import { askNewRound, createTable } from './table.js';

const OFFLINE_BANNER_MS = 10_000;

// Optional parts of the screen (strip, spotlight, history, Jira) mount here. Each entry is
// `(ctx) → {update(view, roundId), destroy()}`; see docs/decisions/U31.md.
const FEATURES = [createStrip, createSpotlight, createObserverToggle, createSettings, jiraFeature, createHistory, createMobile];

const NACK_TEXT = {
  observer: 'Observers cannot vote.',
  forbidden: 'You are not allowed to do that.',
  not_found: 'That round no longer exists.',
  round_pending: 'This round is a preview; it cannot be voted on yet.',
  round_revealed: 'This round was already revealed.',
  round_inactive: 'This round has ended.',
  round_not_voting: 'This round is not open for voting.',
  vote_locked: 'Your vote is locked in.',
  stale: 'Someone else changed this first; the table shows the latest state.',
  invalid: 'That action is not valid any more.',
  outbox_full: 'You have been offline too long. Reload the page to sync.',
};

/** The boot JSON's `me` is the /api/me body (`{principal:{id,…}}`); welcome.me carries `pid`. */
export function pidOf(me) {
  return me?.principal?.id ?? me?.pid ?? null;
}

function nackText(nack) {
  return NACK_TEXT[nack.code] ?? nack.msg ?? 'Something went wrong.';
}

function stripUndefined(object) {
  return Object.fromEntries(Object.entries(object).filter(([, v]) => v !== undefined));
}

/** The session's address under its team, which every link to it uses. */
export function sessionPath(teamId, code) {
  return `${teamPath(teamId)}/poker/${encodeURIComponent(code)}`;
}

/** When the session was created: the snapshot's createdAt, else the time in its UUIDv7 id; null if neither. */
export function createdAt(view) {
  return view.createdAt ?? uuidv7Time(view.sessionId);
}

function message(title, text, ...actions) {
  return h('div', { class: 'nt-message', role: 'status' },
    h('h1', { class: 'nt-message-title', text: title }),
    text ? h('p', { text }) : null,
    ...actions);
}

export function mount(root, boot = {}, app = {}) {
  const teamCrumb = navLink('/', null, { class: 'nt-crumb nt-crumb-team' });
  const pokerCrumb = navLink('/', 'Poker', { class: 'nt-crumb' });
  const dayCrumb = h('span', { class: 'nt-crumb-here', 'aria-current': 'page' });
  const crumbs = h('nav', { class: 'nt-crumbs', 'aria-label': 'Breadcrumb', hidden: true },
    teamCrumb, h('span', { 'aria-hidden': 'true', text: '/' }), pokerCrumb, h('span', { 'aria-hidden': 'true', text: '/' }), dayCrumb);
  const copy = createButton({
    label: 'Copy link',
    icon: 'link',
    iconOnly: true,
    variant: 'outline',
    size: 'sm',
    className: 'btn-icon nt-copy',
    onClick() {
      navigator.clipboard?.writeText(location.origin + location.pathname)
        .then(() => toast('Link copied!', { variant: 'success' }), () => toast('Could not copy the link.', { variant: 'error' }));
    },
  });
  const prevButton = createButton({ label: 'Previous round', icon: 'chevron-left', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: () => step(-1) });
  const nextButton = createButton({ label: 'Next round', icon: 'chevron-right', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: () => step(1) });
  const roundName = h('span', { class: 'nt-round-name', 'aria-live': 'polite' });
  // The features' controls, then Copy link; the header docks mentions and the account menu after them.
  const tools = h('div', { class: 'nt-toolbar-tools' });
  const extras = h('div', { class: 'nt-toolbar-extras' }, tools, copy.el);
  const rounds = h('nav', { class: 'nt-toolbar-center', 'aria-label': 'Rounds' }, prevButton.el, roundName, nextButton.el);
  const toolbar = h('header', { class: 'nt-toolbar' },
    h('div', { class: 'nt-toolbar-start' }, crumbs),
    rounds,
    extras);
  const banner = h('p', { class: 'nt-offline', role: 'status', hidden: true, text: 'Reconnecting… your changes are saved and will sync.' });
  const strip = h('div', { class: 'nt-strip-slot' });
  const left = h('aside', { class: 'nt-panel nt-panel-left', id: 'nt-panel-left', hidden: true });
  const right = h('aside', { class: 'nt-panel nt-panel-right', id: 'nt-panel-right', hidden: true });
  const bottom = h('div', { class: 'nt-bottom-extras' });
  const center = h('main', { class: 'nt-center' });
  const page = h('div', { class: 'neotro' }, toolbar, banner, strip, h('div', { class: 'nt-body' }, left, center, right));
  root.replaceChildren(page);

  let me = pidOf(boot.me);
  let code = null;
  let room = null;
  let socket = null;
  let viewed = null;
  let deepLink = null;
  let lastConfirmed = null;
  let unsubscribe = [];
  let features = [];
  let disposed = false;
  let seeded = false;
  // The team from /teams/:tid/poker/:code until the snapshot names the session's own, and the one the header has.
  let urlTeam = null;
  let headerTeam = null;

  const send = (k, d, options) => room?.send(k, stripUndefined(d), options);

  const actions = {
    reveal: (round) => send('poker.round.reveal', { roundId: round.id }),
    replay: (round) => send('poker.round.replay', { roundId: round.id }),
    end: (round) => send('poker.round.end', { roundId: round.id }),
    nextActive(round) {
      const view = room.view;
      const order = view.rounds;
      const at = order.findIndex((r) => r.id === round.id);
      const next = [...order.slice(at + 1), ...order.slice(0, at)]
        .find((r) => r.active && !r.pending && r.state === 'voting');
      send('poker.round.end', { roundId: round.id });
      if (next) {
        navigate(next.id);
      }
    },
    async continueRound(round) {
      const ticketKey = await askNewRound();
      if (ticketKey !== null) {
        send('poker.round.next', { fromRoundId: round.id, newRoundId: uuidv7(), ticketKey: ticketKey || undefined });
      }
    },
    setTicket(round, ticketKey) {
      send('poker.round.setTicket', { roundId: round.id, ticketKey, baseVersion: round.version }, { apply: ticketIntent(round.id, ticketKey) });
    },
    editTicket: () => table.editTicket(),
  };

  function vote({ points, high = null }, lock = false) {
    const own = myVote(room.view, viewed);
    if (own?.locked) {
      return;
    }
    send('poker.vote.set', { roundId: viewed, points, high: high ?? undefined, lock: lock || undefined }, {
      apply: voteIntent(viewed, { points, high: high ?? undefined, lock }),
    });
  }

  function setLock(locked) {
    send('poker.lock.set', { roundId: viewed, locked }, { apply: lockIntent(viewed, locked) });
  }

  function setAbstain(abstain) {
    send('poker.abstain.set', { roundId: viewed, abstain }, { apply: abstainIntent(viewed, abstain) });
  }

  const table = createTable(actions);
  const drag = createDrag({ dropZone: table.field, cards: () => hand.cards(), onPlay: (selection) => vote(selection, true) });
  const hand = createHand({ onVote: (selection) => vote(selection), onLock: setLock, onAbstain: setAbstain, drag });
  const observerNote = h('p', { class: 'nt-observer', hidden: true }, icon('eye'), h('span', { text: 'You are an observer' }));
  table.attachHand(hand, observerNote);

  function canVote(view) {
    return me !== null && roundPhase(roundById(view, viewed)) === 'voting' && !isObserver(view, me);
  }

  function onShortcut(action) {
    const view = room?.view;
    if (!view || view.deleted || room.kicked) {
      return false;
    }
    const round = roundById(view, viewed);
    const own = myVote(view, viewed);
    switch (action.type) {
      case 'vote':
        if (!canVote(view) || own?.locked) {
          return false;
        }
        vote(action);
        return true;
      case 'lock':
        if (!canVote(view) || own?.points == null || own.abstained) {
          return false;
        }
        setLock(!own.locked);
        return true;
      case 'abstain':
        if (!canVote(view)) {
          return false;
        }
        setAbstain(!own?.abstained);
        return true;
      case 'reveal':
        if (roundPhase(round) !== 'voting') {
          return false;
        }
        actions.reveal(round);
        return true;
      case 'next': {
        if (roundPhase(round) !== 'revealed' || !round.active) {
          return false;
        }
        const others = view.rounds.some((r) => r.id !== round.id && r.active && !r.pending && r.state === 'voting');
        if (others) {
          actions.nextActive(round);
        } else {
          actions.continueRound(round);
        }
        return true;
      }
      case 'round':
        return step(action.step);
      default:
        return false;
    }
  }

  const unbindKeys = bindKeyboard(onShortcut);
  // A renamed team, or `me` arriving late, renames the crumb.
  const stopCrumbs = session.select((state) => state.me, Object.is, () => renderCrumbs());

  function step(delta) {
    const rounds = room?.view?.rounds ?? [];
    const at = rounds.findIndex((r) => r.id === viewed);
    const target = rounds[at + delta];
    if (at < 0 || !target) {
      return false;
    }
    navigate(target.id);
    return true;
  }

  function navigate(roundId) {
    if (roundId === viewed) {
      return;
    }
    viewed = roundId;
    render();
  }

  // The address is the session's under its own team (a link to /poker/:code, or one under another team's id,
  // is corrected in place) with the round being viewed.
  function syncUrl(view) {
    if (typeof history === 'undefined') {
      return;
    }
    const url = new URL(location.href);
    if (view.teamId && view.code) {
      url.pathname = sessionPath(view.teamId, view.code);
    }
    const round = roundById(view, viewed);
    if (round) {
      url.searchParams.set('round', String(round.number));
    }
    const next = url.pathname + url.search + url.hash;
    if (next !== location.pathname + location.search + location.hash) {
      history.replaceState(history.state, '', next);
    }
  }

  // "<team> / Poker / <day>": the snapshot's team (the URL's until it arrives), named from `me`.
  function renderCrumbs(view = room?.view) {
    const teamId = view?.teamId ?? urlTeam;
    const team = teamId ? currentMe()?.teams?.find((t) => t.id === teamId) ?? null : null;
    crumbs.hidden = team === null;
    if (team) {
      replaceContent(teamCrumb, teamBadge(team), h('span', { class: 'nt-crumb-name', text: team.name }));
      teamCrumb.href = teamPath(team.id);
      pokerCrumb.href = teamPath(team.id, 'poker');
      const created = view ? createdAt(view) : null;
      dayCrumb.textContent = created === null ? view?.code ?? code ?? '' : formatDay(created);
    }
    if (view?.teamId && view.teamId !== headerTeam) {
      headerTeam = view.teamId;
      setHeaderTeam(headerTeam);
    }
  }

  function renderToolbar(view) {
    const rounds = view.rounds;
    const at = rounds.findIndex((r) => r.id === viewed);
    const round = rounds[at];
    roundName.textContent = round ? `Round ${round.number}${round.ticketKey ? ` · ${round.ticketKey}` : ''}` : '';
    prevButton.update({ disabled: at <= 0 });
    nextButton.update({ disabled: at < 0 || at >= rounds.length - 1 });
    renderCrumbs(view);
  }

  function renderView(view) {
    if (view.deleted) {
      replaceContent(center, message('Session deleted', 'This poker session was deleted.',
        h('a', { class: 'btn btn-primary', href: view.teamId ? teamPath(view.teamId, 'poker') : '/', dataset: { nav: '' }, text: 'Back to Poker' })));
      return;
    }
    if (room.kicked) {
      const screen = kickScreen(room.kicked);
      if (screen) {
        noAccess(screen);
      } else {
        replaceContent(center, message('Too many rooms open', 'This tab has too many rooms open. Close one of them, then reload this page.'));
      }
      return;
    }
    if (!center.contains(table.el)) {
      replaceContent(center, table.el);
    }
    table.update(view, viewed, { meta: room.extras['jira.meta'] });
    const own = myVote(view, viewed);
    const observing = me !== null && isObserver(view, me);
    const voting = roundPhase(roundById(view, viewed)) === 'voting';
    hand.el.hidden = !voting || observing || me === null;
    hand.actions.hidden = hand.el.hidden;
    observerNote.hidden = !observing;
    hand.update({
      own,
      locked: Boolean(own?.locked),
      abstained: Boolean(own?.abstained),
      enabled: canVote(view),
      descriptions: view.pointDescriptions,
    });
    renderToolbar(view);
    syncUrl(view);
    for (const feature of features) {
      feature.update(view, viewed);
    }
  }

  function render() {
    if (disposed || !room) {
      return;
    }
    const view = room.view;
    const confirmed = room.confirmed;
    if (!view || !confirmed) {
      replaceContent(center, message('Joining the session…'));
      return;
    }
    if (viewed === null || lastConfirmed === null) {
      viewed = initialRound(confirmed, deepLink);
    } else if (confirmed !== lastConfirmed) {
      viewed = resolveNav(lastConfirmed, confirmed, viewed) ?? viewed;
    }
    lastConfirmed = confirmed;
    if (!roundById(view, viewed)) {
      viewed = initialRound(view);
    }
    renderView(view);
  }

  function watchStatus() {
    let timer = 0;
    const show = (status) => {
      clearTimeout(timer);
      if (status.state === 'open' || status.offlineSince === null) {
        banner.hidden = true;
        return;
      }
      const wait = OFFLINE_BANNER_MS - (Date.now() - status.offlineSince);
      if (wait <= 0) {
        banner.hidden = false;
      } else {
        banner.hidden = true;
        timer = setTimeout(() => show(socket.status.get()), wait);
      }
    };
    show(socket.status.get());
    const stop = socket.status.select((s) => s, Object.is, show);
    return () => {
      clearTimeout(timer);
      stop();
    };
  }

  async function roomKey(sessionCode) {
    const bootRoom = boot.room;
    if (!seeded && typeof bootRoom?.r === 'string' && bootRoom.r.startsWith('poker:') && bootRoom.d?.code?.toUpperCase() === sessionCode.toUpperCase()) {
      return bootRoom.r;
    }
    const found = await http.get(`/api/poker/by-code/${encodeURIComponent(sessionCode)}`);
    return `poker:${found.id}`;
  }

  // Missing and not yours look the same (UX18 decision 2), and so does a kick that ends the user's access. The
  // screen replaces the whole page, toolbar included, and the app header shows above it.
  function noAccess(screen) {
    close();
    showNoAccess(root, screen);
  }

  async function open(sessionCode, tid) {
    code = sessionCode;
    urlTeam = tid ?? null;
    if (page.parentNode !== root) {
      root.replaceChildren(page);
    }
    renderCrumbs(null);
    replaceContent(center, message('Joining the session…'));
    if (me === null) {
      // The router sends signed-out visitors to sign in first; this covers a stale boot JSON.
      go(`/login?return=${encodeURIComponent(location.pathname + location.search)}`, { replace: true });
      return;
    }
    let key;
    try {
      key = await roomKey(sessionCode);
    } catch (error) {
      if (disposed || code !== sessionCode) {
        return;
      }
      if (error instanceof ApiError && (error.status === 404 || error.status === 403)) {
        showNoAccess(root);
      } else {
        replaceContent(center, message('The session could not be loaded', error?.code === 'network' ? 'The server could not be reached.' : null,
          h('button', { type: 'button', class: 'btn btn-primary', on: { click: () => open(sessionCode, tid) } }, 'Try again')));
      }
      return;
    }
    if (disposed || code !== sessionCode) {
      return;
    }
    // The entry hands over the tab's socket; otherwise this is the first room of the tab.
    const shared = app.socket ?? await getSocket();
    if (disposed || code !== sessionCode) {
      return;
    }
    socket = shared;
    socket.start();
    let own = null;
    room = socket.room(key, pokerModel(me, { pending: () => own?.pending() ?? [] }), { onNack: (nack) => toast(nackText(nack), { variant: 'error', duration: 5000 }) });
    own = room;
    if (!seeded && boot.room?.r === key && room.confirmed === null) {
      room.seed(boot.room);
    }
    seeded = true;
    unsubscribe = [
      room.select((view) => [view, room.extras, room.kicked], shallowEq, render),
      watchStatus(),
    ];
    const slots = { toolbar: tools, rounds, bottom, strip, left, right, center, page, issue: table.issue, issueScroller: table.scroller, results: table.results };
    const ctx = { room, socket, me, actions, navigate, get viewed() { return viewed; }, slots };
    features = FEATURES.map((feature) => feature(ctx));
    render();
  }

  function close() {
    unsubscribe.forEach((stop) => stop());
    unsubscribe = [];
    features.forEach((feature) => feature.destroy());
    features = [];
    drag.cancel();
    if (room) {
      socket.leave(room);
    }
    room = null;
    viewed = null;
    lastConfirmed = null;
  }

  return {
    update(params = {}) {
      const query = params.query ?? new URLSearchParams(location.search);
      const linked = Number(query.get('round'));
      deepLink = Number.isInteger(linked) && linked > 0 ? linked : null;
      if (params.code !== code) {
        close();
        open(params.code, params.tid);
        return;
      }
      const target = deepLink == null ? null : room?.view?.rounds.find((r) => r.number === deepLink);
      if (target) {
        navigate(target.id);
      }
    },
    unmount() {
      disposed = true;
      stopCrumbs();
      close();
      unbindKeys();
      hand.destroy();
      table.destroy();
      root.replaceChildren();
    },
  };
}
