// Kudos (endorsements) on team boards: a tab fixed at the bottom of the screen (or the N key) opens a
// bottom drawer where a member gives or takes back endorsements, limited per board by the team's live
// `maxEndorsements` (`team.settings`; UX16). The recipient's projection marks a new endorsement with
// `celebrate`, which plays a short confetti burst and arpeggio (DESIGN §g.8; no external sound).
// Members-only data (S12): without `members` in the view none of this shows.

import { h, replaceContent } from '../core/h.js';
import { createAvatar } from '../ui/avatar.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { isMember, uuidv7 } from './model.js';
import { avatarSrc } from './card.js';
import { bindKudosKey } from './keyboard.js';

const CONFETTI_PIECES = 24;
const CONFETTI_MS = 2000;
const CELEBRATION_MS = 4000;
const CONFETTI_COLORS = ['#f43f5e', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899'];
// C5, E5, G5, C6.
const ARPEGGIO_HZ = [523.25, 659.25, 783.99, 1046.5];
const NOTE_S = 0.11;

const NACKS = {
  endorse_limit: 'You have given all your kudos on this board.',
  forbidden: 'Only team members can give kudos.',
  invalid: 'That kudos was already given.',
  not_found: 'That kind of kudos no longer exists.',
};

function withEndorsement(state, endorsement) {
  const endorsements = new Map(state.endorsements ?? []);
  endorsements.set(endorsement.id, endorsement);
  return { ...state, endorsements };
}

function withoutEndorsement(state, id) {
  if (!state.endorsements?.has(id)) {
    return state;
  }
  const endorsements = new Map(state.endorsements);
  endorsements.delete(id);
  return { ...state, endorsements };
}

/** What the drawer offers a member: the live types, the teammates and how many kudos are left. */
export function kudosOf(view, me) {
  if (!view?.teamId || !me || !isMember(view, me.pid)) {
    return null;
  }
  const all = [...(view.endorsements?.values() ?? [])];
  const given = all.filter((e) => e.from === me.pid);
  const max = view.maxEndorsements ?? 0;
  return {
    types: (view.endorsementTypes ?? []).filter((t) => !t.archived),
    teammates: view.members.filter((m) => m.pid !== me.pid),
    all,
    given,
    max,
    left: Math.max(0, max - given.length),
  };
}

function playArpeggio() {
  const Context = globalThis.AudioContext ?? globalThis.webkitAudioContext;
  if (!Context) {
    return;
  }
  try {
    const audio = new Context();
    const start = audio.currentTime + 0.02;
    ARPEGGIO_HZ.forEach((hz, i) => {
      const at = start + i * NOTE_S;
      const osc = audio.createOscillator();
      const gain = audio.createGain();
      osc.type = 'triangle';
      osc.frequency.value = hz;
      gain.gain.setValueAtTime(0.0001, at);
      gain.gain.exponentialRampToValueAtTime(0.15, at + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, at + NOTE_S * 1.8);
      osc.connect(gain).connect(audio.destination);
      osc.start(at);
      osc.stop(at + NOTE_S * 2);
    });
    setTimeout(() => audio.close().catch(() => {}), (ARPEGGIO_HZ.length * NOTE_S + 0.5) * 1000);
  } catch {
    // Audio is a flourish: a browser that refuses it (no user gesture yet) still shows the card.
  }
}

// Each piece's place and colour go through the CSSOM (style-src 'self'); the animation is Web Animations.
function confetti(layer) {
  for (let i = 0; i < CONFETTI_PIECES; i++) {
    const piece = h('span', {
      class: 'retro-confetti',
      'aria-hidden': 'true',
      style: {
        left: `${(i + 0.5) * (100 / CONFETTI_PIECES)}%`,
        background: CONFETTI_COLORS[i % CONFETTI_COLORS.length],
        'border-radius': i % 3 === 0 ? '50%' : '1px',
      },
    });
    layer.append(piece);
    const drift = (Math.random() - 0.5) * 160;
    const spin = (Math.random() - 0.5) * 1440;
    piece.animate?.([
      { transform: 'translate(0, 0) rotate(0deg)', opacity: 1 },
      { transform: `translate(${drift}px, 100vh) rotate(${spin}deg)`, opacity: 0.2 },
    ], { duration: CONFETTI_MS * (0.7 + Math.random() * 0.3), delay: Math.random() * 300, easing: 'cubic-bezier(.2,.6,.4,1)', fill: 'forwards' });
  }
}

/**
 * The recipient's celebration: a card naming the giver and the kudos, confetti and a chime. It goes in `host`,
 * which is the open kudos drawer when there is one: a modal dialog makes the rest of the page inert and
 * covers it with its backdrop. moveTo(host) carries a live celebration into or out of the drawer.
 */
export function celebrate({ emoji, typeName, from }, host = document.body) {
  const reduced = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  const layer = h('div', { class: 'retro-celebration-confetti', 'aria-hidden': 'true' });
  const card = h('div', { class: 'card retro-celebration', role: 'status' },
  h('div', { class: 'retro-celebration-emoji', 'aria-hidden': 'true', text: emoji }),
  h('p', { class: 'retro-celebration-title', text: 'You got kudos!' }),
  h('p', { class: 'retro-celebration-detail', text: `${from} recognised you as ${typeName}.` }));
  let timer = 0;
  const close = () => {
    clearTimeout(timer);
    layer.remove();
    card.remove();
  };
  card.addEventListener('click', close);
  host.append(layer, card);
  if (!reduced) {
    confetti(layer);
  }
  playArpeggio();
  timer = setTimeout(close, CELEBRATION_MS);
  return {
    close,
    moveTo(next) {
      if (card.isConnected) {
        next.append(layer, card);
      }
    },
  };
}

export const kudos = {
  board(ctx, { end }) {
    const left = h('span', { class: 'badge retro-kudos-left' });
    const button = h('button', {
      type: 'button',
      class: 'retro-kudos-tab',
      'aria-haspopup': 'dialog',
      'aria-keyshortcuts': 'N',
      on: { click: () => toggleDrawer() },
    }, icon('award'), h('span', { text: 'Kudos' }), ' ', left, h('span', { class: 'sr-only', text: ' left' }));
    // The key hint sits over the tab but outside the button, so the button's visible text stays the start of
    // its name (axe label-content-name-mismatch); clicks pass through it.
    const dock = h('div', { class: 'retro-kudos-dock', hidden: true },
      button, h('kbd', { class: 'retro-kudos-key', 'aria-hidden': 'true', text: 'N' }));
    end.append(dock);
    const summary = h('p', { class: 'retro-kudos-summary', role: 'status' });
    const people = h('ul', { class: 'retro-kudos-people' });
    let dialog = null;
    let unbindKey = null;
    let seen = ctx.view()?.celebrate ?? null;
    let celebration = null;

    function send(k, d, apply) {
      ctx.command(k, d, {
        apply,
        onNack(nack) {
          toast(NACKS[nack.code] ?? 'That kudos could not be saved.', { variant: 'error' });
          return true;
        },
      });
    }

    function toggle(person, type, mine) {
      if (mine) {
        send('retro.endorse.revoke', { endorsementId: mine.id }, (state) => withoutEndorsement(state, mine.id));
        return;
      }
      const endorsement = { id: uuidv7(ctx.now()), typeId: type.id, from: ctx.me().pid, to: person.pid, createdAt: ctx.now() };
      send('retro.endorse.give', { endorsementId: endorsement.id, toUserId: person.pid, typeId: type.id },
        (state) => withEndorsement(state, endorsement));
    }

    function paintDrawer(offer) {
      summary.textContent = offer.max === 0
        ? 'Kudos are turned off for this team.'
        : `You have ${offer.left} of ${offer.max} kudos left on this board.`;
      if (offer.teammates.length === 0) {
        replaceContent(people, h('li', { class: 'retro-kudos-empty', text: 'No other team members to give kudos to.' }));
        return;
      }
      replaceContent(people, offer.teammates.map((person) => h('li', { class: 'retro-kudos-person', dataset: { pid: person.pid } },
        createAvatar({ name: person.name, src: avatarSrc(person.avatar), size: 'lg' }),
        h('span', { class: 'retro-kudos-name', text: person.name }),
        h('div', {
          class: 'retro-kudos-types',
          role: 'group',
          'aria-label': `Kudos for ${person.name}`,
        }, offer.types.map((type) => {
          const mine = offer.given.find((e) => e.to === person.pid && e.typeId === type.id) ?? null;
          const received = offer.all.filter((e) => e.to === person.pid && e.typeId === type.id).length;
          return h('button', {
            type: 'button',
            class: ['btn', 'btn-sm', mine ? 'btn-primary' : 'btn-outline', 'retro-kudos-type'],
            'aria-pressed': String(mine !== null),
            disabled: mine === null && offer.left === 0,
            title: mine ? `Take back your ${type.name} kudos` : type.description ?? type.name,
            dataset: { type: type.id },
            on: { click: () => toggle(person, type, mine) },
          }, h('span', { 'aria-hidden': 'true', text: type.emoji }), h('span', { class: 'retro-kudos-type-name', text: type.name }),
          received > 0 ? h('span', { class: 'badge', text: String(received) }) : null);
        })))));
    }

    function open() {
      const offer = kudosOf(ctx.view(), ctx.me());
      if (!offer || dialog) {
        return;
      }
      dialog = createDialog({
        title: 'Give kudos',
        description: 'Recognise a teammate for this retro.',
        className: 'retro-kudos',
        content: h('div', { class: 'retro-kudos-body' }, summary, people),
        onClose() {
          celebration?.moveTo(document.body);
          dialog.destroy();
          dialog = null;
          // The drawer also closes when the tab hides (UX16), and then there is no tab to go back to.
          if (!dock.hidden) {
            button.focus();
          }
        },
      });
      dialog.el.querySelector('.dialog-title').prepend(icon('award'));
      paintDrawer(offer);
      dialog.open();
      celebration?.moveTo(dialog.el);
    }

    function toggleDrawer() {
      if (dialog) {
        dialog.close();
      } else {
        open();
      }
    }

    function maybeCelebrate(view) {
      const id = view.celebrate;
      if (id === seen) {
        return;
      }
      seen = id;
      const endorsement = id ? view.endorsements?.get(id) : null;
      if (!endorsement || endorsement.to !== ctx.me()?.pid) {
        return;
      }
      const type = view.endorsementTypes?.find((t) => t.id === endorsement.typeId);
      celebration?.close();
      celebration = celebrate({
        emoji: type?.emoji ?? '⭐',
        typeName: type?.name ?? 'a teammate',
        from: ctx.personName(endorsement.from) ?? 'A teammate',
      }, dialog?.el);
    }

    return {
      update(view) {
        const offer = kudosOf(view, ctx.me());
        dock.hidden = offer === null || offer.types.length === 0;
        // N and the drawer work only while the tab does.
        if (dock.hidden) {
          unbindKey?.();
          unbindKey = null;
          dialog?.close();
        } else {
          unbindKey ??= bindKudosKey(toggleDrawer, () => dialog?.el ?? null);
        }
        if (offer) {
          left.textContent = String(offer.left);
          if (dialog) {
            paintDrawer(offer);
          }
        }
        maybeCelebrate(view);
      },
      destroy() {
        unbindKey?.();
        celebration?.close();
        dialog?.destroy();
        dock.remove();
      },
    };
  },
};
