// The sprint report's entry on a team board (UX17): a toolbar button for members of a team whose Jira has a
// board. The panel is the lazy sprint-report chunk, fetched when the pointer or focus reaches the button.

import { createButton } from '../ui/button.js';
import { toast } from '../ui/toast.js';
import { teamLink } from './actions.js';

/** The team's Jira when the viewer may see a sprint report: a member's link, configured, with a board. */
export function reportable(link) {
  const jira = link.teamId ? link.jira() : null;
  return jira?.configured && jira.boardId != null ? jira : null;
}

export const sprintFeature = {
  board(ctx, { toolbar }) {
    const link = teamLink(ctx);
    const release = link.use();
    let load = null;
    let report = null;
    let destroyed = false;

    const button = createButton({
      label: 'Sprint report',
      icon: 'folder-kanban',
      variant: 'outline',
      size: 'sm',
      className: 'retro-sprint-open',
      onClick: () => toggle(),
    });
    button.el.hidden = true;
    button.el.setAttribute('aria-haspopup', 'dialog');
    button.el.setAttribute('aria-expanded', 'false');
    for (const type of ['pointerenter', 'focus']) {
      button.el.addEventListener(type, () => chunk().catch(() => {}));
    }
    toolbar.append(button.el);

    function chunk() {
      load ??= import('./sprint-report.js').catch((error) => {
        // Forget the failed chunk so the next click retries it.
        load = null;
        throw error;
      });
      return load;
    }

    async function toggle() {
      let module;
      try {
        module = await chunk();
      } catch {
        toast('Could not load the sprint report. Try again.', { variant: 'error' });
        return;
      }
      const jira = reportable(link);
      if (destroyed || !jira) {
        return;
      }
      report ??= module.createSprintReport({
        teamId: link.teamId,
        boardId: ctx.boardId,
        siteHost: jira.siteHost,
        dock: toolbar.closest('.retro-board'),
        onClose() {
          button.el.setAttribute('aria-expanded', 'false');
          // Safari does not focus a clicked button, so the sheet may have no opener to give focus back to.
          if (!button.el.hidden) {
            button.el.focus();
          }
        },
      });
      report.toggle();
      button.el.setAttribute('aria-expanded', String(report.isOpen));
    }

    function paint() {
      const visible = reportable(link) !== null;
      button.el.hidden = !visible;
      if (!visible && report) {
        report.destroy();
        report = null;
        button.el.setAttribute('aria-expanded', 'false');
      }
    }

    const unsubscribe = link.subscribe(paint);
    link.sync(ctx.view());
    paint();
    return {
      update(view) {
        link.sync(view);
        paint();
      },
      destroy() {
        destroyed = true;
        report?.destroy();
        unsubscribe();
        release();
        button.destroy();
      },
    };
  },
};
