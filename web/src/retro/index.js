// /retro/:code. Resolves the board (boot snapshot, or by-code for in-app navigation), handles the
// password and sign-in gates, joins the board room on the tab's socket and mounts the board.

import { h, replaceContent } from '../core/h.js';
import { ApiError, http } from '../core/http.js';
import { getSocket } from '../screens/app.js';
import { createButton } from '../ui/button.js';
import { toast } from '../ui/toast.js';
import { createRetroModel } from './model.js';
import { createBoard } from './board.js';
import { stagesFeature } from './stages.js';
import { focusFeature } from './focus.js';
import { timerFeature } from './timer.js';
import { configFeature } from './config.js';
import { shareFeature } from './share.js';
import { actionItems } from './actions.js';
import { previousActionItems } from './prev-actions.js';
import { kudos } from './kudos.js';

const FEATURES = [
  stagesFeature, focusFeature, timerFeature, shareFeature, configFeature,
  actionItems, previousActionItems, kudos,
];

const NACK_MESSAGES = {
  forbidden: 'You are not allowed to do that on this board.',
  not_found: 'That is no longer on the board.',
  invalid: 'That change was refused.',
  stale: 'Someone changed this first; the latest version is shown.',
  stage_forbids: 'The current stage does not allow that.',
  board_archived: 'This board is archived.',
  vote_limit: 'You have used all your votes.',
  self_vote: 'You cannot vote for your own card.',
  too_large: 'That is too long to save.',
  outbox_full: 'You have been offline too long. Reload the page to continue.',
  internal: 'Something went wrong. Please try again.',
};

export function nackMessage(nack) {
  return NACK_MESSAGES[nack.code] ?? 'That change could not be saved.';
}

function message(text, ...extra) {
  return h('div', { class: 'retro-gate' }, h('p', { class: 'retro-gate-message', text }), ...extra);
}

export function mount(root, boot, app = {}) {
  const el = h('div', { class: 'retro' });
  root.replaceChildren(el);
  let code = null;
  let generation = 0;
  let socket = app.socket ?? null;
  let room = null;
  let board = null;

  // Until the welcome names the viewer, the boot JSON's /api/me body stands in, in welcome.me's shape.
  const principal = boot.me?.principal;
  const bootMe = principal ? { pid: principal.id, kind: principal.kind, name: principal.name, avatar: principal.avatar } : boot.me ?? null;
  const me = () => socket?.me ?? bootMe;

  function leave() {
    board?.destroy();
    board = null;
    if (room) {
      socket.leave(room);
      room = null;
    }
  }

  async function resolve(target) {
    const preset = boot.room;
    const presetCode = preset?.d?.code ?? preset?.code;
    if (preset?.r?.startsWith('board:') && (presetCode === undefined || presetCode.toUpperCase() === target.toUpperCase())) {
      return { id: preset.r.slice('board:'.length), access: preset.access ?? 'granted', snap: preset.d ? preset : null };
    }
    const found = await http.get(`/api/retro/by-code/${encodeURIComponent(target)}`);
    return { id: found.id, access: found.access, snap: null };
  }

  async function join(id, snap, mine) {
    socket ??= await getSocket();
    if (mine !== generation) {
      return;
    }
    socket.start();
    const model = createRetroModel({ me, now: () => socket.clock.serverNow() });
    room = socket.room(`board:${id}`, model, { onNack: (nack) => toast(nackMessage(nack), { variant: 'error' }) });
    if (snap && room.view === null) {
      room.seed(snap);
    }
    board = createBoard({ room, socket, me, boardId: id, features: FEATURES });
    replaceContent(el, board.el);
  }

  function unlockForm(id, mine) {
    const input = h('input', { class: 'input', type: 'password', autocomplete: 'current-password', required: true, 'aria-label': 'Board password' });
    const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
    const form = h('form', {
      class: 'retro-unlock',
      on: {
        async submit(event) {
          event.preventDefault();
          try {
            await http.post(`/api/retro/${id}/unlock`, { password: input.value });
          } catch (failure) {
            if (mine !== generation) {
              return;
            }
            error.hidden = false;
            error.textContent = failure instanceof ApiError && failure.code === 'rate_limited'
              ? 'Too many attempts. Wait a minute and try again.'
              : 'That password is not right.';
            input.select();
            return;
          }
          if (mine === generation) {
            join(id, null, mine);
          }
        },
      },
    }, h('h1', { class: 'retro-gate-title', text: 'This board needs a password' }), input, error,
    createButton({ label: 'Open board', variant: 'primary', type: 'submit' }));
    replaceContent(el, form);
    input.focus();
  }

  async function open(target) {
    const mine = ++generation;
    leave();
    replaceContent(el, message('Loading board…'));
    let found;
    try {
      found = await resolve(target);
    } catch (failure) {
      if (mine === generation) {
        replaceContent(el, message(failure instanceof ApiError && failure.code === 'network'
          ? 'The server could not be reached.'
          : 'This board does not exist.'));
      }
      return;
    }
    if (mine !== generation) {
      return;
    }
    if (found.access === 'granted') {
      join(found.id, found.snap, mine);
    } else if (found.access === 'password') {
      unlockForm(found.id, mine);
    } else if (found.access === 'signin') {
      const login = `/login?return=${encodeURIComponent(location.pathname)}`;
      replaceContent(el, message('Sign in to open this board.', h('a', { class: 'btn btn-primary', href: login, dataset: { nav: '' } }, 'Sign in')));
    } else {
      replaceContent(el, message('This board does not exist.'));
    }
  }

  return {
    update(params) {
      if (params.code !== code) {
        code = params.code;
        open(code);
      }
    },
    unmount() {
      generation++;
      leave();
      root.replaceChildren();
    },
  };
}
