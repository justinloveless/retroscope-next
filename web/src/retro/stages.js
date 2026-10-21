// The stage bar (DESIGN §c.4 stage gates, §g.8): thinking → voting → discussing → closed, with readiness and
// the facilitator's moves. A stage change waits for the server (§g.4); `retro.stage.set` is a
// compare-and-set on `from`, so when two facilitators click at once one wins and the other is told.

import { h } from '../core/h.js';
import { announce } from '../core/a11y.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { isFacilitator } from './model.js';
import { createReadiness, readinessOf } from './readiness.js';

export const STAGES = ['thinking', 'voting', 'discussing', 'closed'];

const STAGE = {
  thinking: { label: 'Thinking', icon: 'sparkles', advance: 'Start voting', about: 'Add cards and comments. Votes open in the next stage.' },
  voting: { label: 'Voting', icon: 'vote', advance: 'Start discussion', about: 'Vote for the cards to talk about. Voters stay hidden until the discussion.' },
  discussing: { label: 'Discussing', icon: 'message-square', advance: 'Close retro', about: 'Focus cards one by one and add action items.' },
  closed: { label: 'Closed', icon: 'lock', advance: null, about: 'The retrospective is complete.' },
};

export function stageLabel(stage) {
  return STAGE[stage]?.label ?? stage;
}

export function nextStage(stage) {
  return STAGES[STAGES.indexOf(stage) + 1] ?? null;
}

/** With readiness enforced only advancing, Reset/Restart (to thinking) and Reopen are open (StageRules). */
export function allowedMove(view, to) {
  if (to === view.stage) {
    return false;
  }
  if (!view.config.enforceReadiness) {
    return true;
  }
  return to === nextStage(view.stage) || to === 'thinking' || (view.stage === 'closed' && to === 'discussing');
}

export const stagesFeature = {
  board(ctx, { top }) {
    let shown = null;
    const pills = STAGES.map((stage) => h('button', {
      type: 'button', class: 'retro-stage-pill', dataset: { stage },
    }, icon(STAGE[stage].icon), h('span', { text: STAGE[stage].label })));
    const steps = h('ol', { class: 'retro-stage-steps', 'aria-label': 'Retro stages' }, pills.map((pill) => h('li', null, pill)));
    const readiness = createReadiness(ctx);
    const about = h('p', { class: 'retro-stage-about' });
    const advance = createButton({ variant: 'primary', size: 'sm', className: 'retro-stage-advance', onClick: () => move(nextStage(ctx.view().stage)) });
    const reset = createButton({ label: 'Reset', icon: 'rotate-ccw', variant: 'ghost', size: 'sm', onClick: () => move('thinking') });
    const reopen = createButton({ label: 'Reopen', icon: 'undo-2', variant: 'outline', size: 'sm', onClick: () => move('discussing') });
    const restart = createButton({ label: 'Restart', icon: 'refresh-cw', variant: 'ghost', size: 'sm', onClick: () => move('thinking') });
    const done = h('span', { class: 'badge retro-stage-done' }, icon('lock'), 'Retrospective complete');
    const actions = h('div', { class: 'retro-stage-actions' }, reset, reopen, restart, advance);
    const el = h('section', { class: 'retro-stages', 'aria-label': 'Stage' },
      h('div', { class: 'retro-stage-row' }, steps, readiness.el, done, actions), about);
    top.append(el);

    function move(to) {
      const view = ctx.view();
      if (!to || !allowedMove(view, to)) {
        return;
      }
      ctx.command('retro.stage.set', { from: view.stage, to }, {
        onNack(nack) {
          if (nack.code !== 'stale') {
            return false;
          }
          toast(`Someone else changed the stage first. The board is now in ${stageLabel(ctx.view().stage)}.`);
          return true;
        },
      });
    }

    steps.addEventListener('click', (event) => {
      const pill = event.target.closest('.retro-stage-pill');
      if (pill && !pill.disabled) {
        move(pill.dataset.stage);
      }
    });

    return {
      update(view) {
        el.hidden = !view.config.stagesEnabled;
        if (el.hidden) {
          shown = null;
          return;
        }
        if (shown !== null && shown !== view.stage) {
          announce(`The retro moved to ${stageLabel(view.stage)}.`);
        }
        shown = view.stage;
        const facilitator = isFacilitator(view, ctx.me()) && !view.meta.archived;
        const current = STAGES.indexOf(view.stage);
        pills.forEach((pill, i) => {
          const stage = STAGES[i];
          pill.dataset.state = i < current ? 'done' : i === current ? 'current' : 'upcoming';
          if (i === current) {
            pill.setAttribute('aria-current', 'step');
          } else {
            pill.removeAttribute('aria-current');
          }
          pill.disabled = !facilitator || view.config.enforceReadiness || stage === view.stage;
          pill.title = pill.disabled ? '' : `Move to ${STAGE[stage].label}`;
        });
        const state = readinessOf(view);
        const blocked = view.config.enforceReadiness && !state.all;
        const next = nextStage(view.stage);
        readiness.update(view);
        readiness.el.hidden = view.stage === 'closed';
        advance.el.hidden = !facilitator || next === null;
        if (next) {
          advance.update({
            label: `${STAGE[view.stage].advance} ›`,
            disabled: blocked,
            title: blocked ? 'Everyone present needs to be ready first' : null,
          });
        }
        reset.el.hidden = !facilitator || view.stage === 'thinking' || view.stage === 'closed';
        reopen.el.hidden = !facilitator || view.stage !== 'closed';
        restart.el.hidden = !facilitator || view.stage !== 'closed';
        done.hidden = facilitator || view.stage !== 'closed';
        const waiting = state.waiting.length;
        about.textContent = view.stage !== 'closed' && waiting > 0 && state.ready.length > 0
          ? `${STAGE[view.stage].about} Waiting for ${waiting} more…`
          : STAGE[view.stage].about;
        el.dataset.stage = view.stage;
      },
      destroy() {
        el.remove();
      },
    };
  },
};
