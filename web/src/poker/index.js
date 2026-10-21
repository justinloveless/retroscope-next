// The poker screen (/poker/:code and /teams/:tid/poker/:code): joins the room, paints from the boot
// snapshot, follows pointer and spotlight navigation (R13), keeps ?round=N in the URL and wires the table,
// the hand, drag-to-play and the shortcuts to the room's commands.

import { h, replaceContent } from '../core/h.js';
import { ApiError, http } from '../core/http.js';
import { shallowEq } from '../core/store.js';
import { createSocket } from '../net/socket.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { createDrag } from './drag.js';
import { createHand } from './hand.js';
import { bindKeyboard } from './keyboard.js';
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
  voteIntent,
} from './model.js';
import { jiraFeature } from './submit.js';
import { askNewRound, createTable } from './table.js';

const OFFLINE_BANNER_MS = 10_000;

// Optional parts of the screen (strip, spotlight, history, Jira) mount here. Each entry is
// `(ctx) → {update(view, roundId), destroy()}`; see docs/decisions/U31.md.
const FEATURES = [jiraFeature];

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

// One socket per tab; it outlives a single poker page so in-app navigation keeps the connection. A new
// principal (guest join, sign-in) gets a new socket, so hello and the outbox carry the right pid.
let tabSocket = null;
let tabSocketPid = null;

function socketFor(pid) {
  if (tabSocket && tabSocketPid !== pid) {
    tabSocket.stop();
    tabSocket = null;
  }
  if (!tabSocket) {
    tabSocket = createSocket({ pid });
    tabSocketPid = pid;
    tabSocket.start();
  }
  return tabSocket;
}

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

function message(title, text, ...actions) {
  return h('div', { class: 'nt-message', role: 'status' },
    h('h1', { class: 'nt-message-title', text: title }),
    text ? h('p', { text }) : null,
    ...actions);
}

export function mount(root, boot = {}) {
  const backLink = h('a', { class: 'btn btn-ghost btn-sm nt-back', href: '/', dataset: { nav: '' } }, icon('chevron-left'), h('span', { text: 'Back' }));
  const codeChip = h('span', { class: 'nt-code' });
  const copy = createButton({
    label: 'Copy link',
    icon: 'link',
    iconOnly: true,
    variant: 'ghost',
    size: 'sm',
    onClick() {
      navigator.clipboard?.writeText(location.origin + location.pathname)
        .then(() => toast('Link copied!', { variant: 'success' }), () => toast('Could not copy the link.', { variant: 'error' }));
    },
  });
  const prevButton = createButton({ label: 'Previous round', icon: 'chevron-left', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: () => step(-1) });
  const nextButton = createButton({ label: 'Next round', icon: 'chevron-right', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: () => step(1) });
  const roundName = h('span', { class: 'nt-round-name', 'aria-live': 'polite' });
  const extras = h('div', { class: 'nt-toolbar-extras' });
  const toolbar = h('header', { class: 'nt-toolbar' },
    h('div', { class: 'nt-toolbar-start' }, backLink, codeChip, copy.el),
    h('nav', { class: 'nt-toolbar-center', 'aria-label': 'Rounds' }, prevButton.el, roundName, nextButton.el),
    extras);
  const banner = h('p', { class: 'nt-offline', role: 'status', hidden: true, text: 'Reconnecting… your changes are saved and will sync.' });
  const strip = h('div', { class: 'nt-strip-slot' });
  const left = h('aside', { class: 'nt-panel nt-panel-left', hidden: true });
  const right = h('aside', { class: 'nt-panel nt-panel-right', hidden: true });
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
  const drag = createDrag({ dropZone: table.field, onPlay: (selection) => vote(selection, true) });
  const hand = createHand({ onVote: (selection) => vote(selection), onLock: setLock, onAbstain: setAbstain, drag });
  const observerNote = h('p', { class: 'nt-observer', hidden: true }, icon('eye'), h('span', { text: 'You are an observer' }));

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

  function syncUrl(view) {
    const round = roundById(view, viewed);
    if (!round || typeof history === 'undefined') {
      return;
    }
    const url = new URL(location.href);
    if (url.searchParams.get('round') !== String(round.number)) {
      url.searchParams.set('round', String(round.number));
      history.replaceState(history.state, '', url.pathname + url.search + url.hash);
    }
  }

  function renderToolbar(view) {
    const rounds = view.rounds;
    const at = rounds.findIndex((r) => r.id === viewed);
    const round = rounds[at];
    roundName.textContent = round ? `Round ${round.number}${round.ticketKey ? ` · ${round.ticketKey}` : ''}` : '';
    prevButton.update({ disabled: at <= 0 });
    nextButton.update({ disabled: at < 0 || at >= rounds.length - 1 });
    codeChip.textContent = view.code ?? code ?? '';
    copy.el.hidden = Boolean(view.teamId);
  }

  function renderView(view) {
    if (view.deleted) {
      replaceContent(center, message('Session deleted', 'This poker session was deleted.',
        h('a', { class: 'btn btn-primary', href: backLink.getAttribute('href'), dataset: { nav: '' }, text: 'Go back' })));
      return;
    }
    if (room.kicked) {
      replaceContent(center, message('You left this session', room.kicked === 'deleted'
        ? 'This poker session was deleted.'
        : 'You no longer have access to this session.'));
      return;
    }
    if (!center.contains(table.el)) {
      replaceContent(center, table.el, hand.el, observerNote);
    }
    table.update(view, viewed, { meta: room.extras['jira.meta'] });
    const own = myVote(view, viewed);
    const observing = me !== null && isObserver(view, me);
    const voting = roundPhase(roundById(view, viewed)) === 'voting';
    hand.el.hidden = !voting || observing || me === null;
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

  function askName() {
    return new Promise((resolve) => {
      const input = h('input', { class: 'input', name: 'displayName', required: true, maxlength: '40', autocomplete: 'nickname', 'aria-label': 'Your name' });
      const error = h('p', { class: 'nt-join-error', role: 'alert', hidden: true });
      const form = h('form', {
        class: 'nt-join',
        on: {
          async submit(event) {
            event.preventDefault();
            try {
              const result = await http.post('/api/guest', { displayName: input.value.trim() });
              resolve(pidOf(result));
            } catch (failure) {
              error.hidden = false;
              error.textContent = failure instanceof ApiError && failure.code === 'invalid'
                ? 'Please choose another name.'
                : 'Could not join right now. Try again.';
            }
          },
        },
      },
      h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Your name' }), input),
      error,
      createButton({ label: 'Join', type: 'submit', variant: 'green', press: true }).el);
      replaceContent(center, message('Join the planning poker session', 'Pick a name the others will see.', form));
      input.focus();
    });
  }

  async function open(sessionCode, tid) {
    code = sessionCode;
    backLink.setAttribute('href', tid ? `/teams/${encodeURIComponent(tid)}?tab=poker` : '/');
    replaceContent(center, message('Joining the session…'));
    if (me === null) {
      me = await askName();
      if (disposed) {
        return;
      }
    }
    let key;
    try {
      key = await roomKey(sessionCode);
    } catch (error) {
      if (!disposed) {
        replaceContent(center, message('Session not found', 'Check the link, or ask for a new one.',
          h('a', { class: 'btn btn-primary', href: '/', dataset: { nav: '' }, text: 'Start over' })));
      }
      return;
    }
    if (disposed || code !== sessionCode) {
      return;
    }
    socket = socketFor(me);
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
    const ctx = { room, socket, me, actions, navigate, get viewed() { return viewed; }, slots: { toolbar: extras, strip, left, right } };
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
      close();
      unbindKeys();
      hand.destroy();
      table.destroy();
      root.replaceChildren();
    },
  };
}
