// /retro/:code. Resolves the board (boot snapshot, or by-code for in-app navigation), joins the board room on the
// tab's socket and mounts the board. Boards are team-only (UX18): a board that does not exist and one of a team
// the user is not on look the same, and a member removed while the board is open sees that they no longer
// have access. The header shows the board's team, from its snapshot, with Retros active.

import { h, replaceContent } from '../core/h.js';
import { ApiError, http } from '../core/http.js';
import { getSocket, setHeaderTeam } from '../screens/app.js';
import { kickScreen, showNoAccess } from '../screens/no-access.js';
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
import { sprintFeature } from './sprint.js';

const FEATURES = [
  stagesFeature, focusFeature, timerFeature, shareFeature, configFeature,
  actionItems, previousActionItems, kudos, sprintFeature,
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
  let unwatch = [];

  // Until the welcome names the viewer, the boot JSON's /api/me body stands in, in welcome.me's shape.
  const principal = boot.me?.principal;
  const bootMe = principal ? { pid: principal.id, kind: principal.kind, name: principal.name, avatar: principal.avatar } : boot.me ?? null;
  const me = () => socket?.me ?? bootMe;

  function leave() {
    for (const stop of unwatch) {
      stop();
    }
    unwatch = [];
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
      return { id: preset.r.slice('board:'.length), snap: preset.d ? preset : null };
    }
    const found = await http.get(`/api/retro/by-code/${encodeURIComponent(target)}`);
    return { id: found.id, snap: null };
  }

  function noAccess(screen) {
    leave();
    showNoAccess(el, screen);
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
    const joined = room;
    unwatch = [
      joined.select((view) => view?.teamId ?? null, Object.is, (teamId) => setHeaderTeam(teamId)),
      joined.select(() => joined.kicked, Object.is, (code) => {
        const screen = kickScreen(code);
        if (screen && joined === room) {
          noAccess(screen);
        }
      }),
    ];
    setHeaderTeam(room.view?.teamId ?? snap?.d?.teamId ?? null);
    board = createBoard({ room, socket, me, boardId: id, features: FEATURES, timerMusic: boot.timerMusic === true });
    replaceContent(el, board.el);
  }

  async function open(target) {
    const mine = ++generation;
    leave();
    replaceContent(el, message('Loading board…'));
    let found;
    try {
      found = await resolve(target);
    } catch (failure) {
      if (mine !== generation) {
        return;
      }
      if (failure instanceof ApiError && (failure.status === 404 || failure.status === 403)) {
        showNoAccess(el);
      } else {
        replaceContent(el, message(failure instanceof ApiError && failure.code === 'network'
          ? 'The server could not be reached.'
          : 'This board could not be loaded.',
        h('button', { type: 'button', class: 'btn btn-secondary', on: { click: () => open(target) } }, 'Try again')));
      }
      return;
    }
    if (mine === generation) {
      join(found.id, found.snap, mine);
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
