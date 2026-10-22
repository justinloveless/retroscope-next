// The Jira issue drawer (§f, §g.7 chunk jira-drawer; parity: original JiraIssueDrawer). Details come from
// the proxy's 60 s detail cache and a 60 s client cache that a 600 ms hover prefetch fills. Every field
// edit is a proxy write followed by a reload. The description is edited as markup with the issue's
// opaque islands (tables, screenshots, panels…) as read-only atoms, and saved with `descriptionBase`, so
// a concurrent edit in Jira answers `stale` instead of being overwritten.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { createEditor } from '../markup/editor.js';
import { DIALECT_JIRA, DIALECT_JIRA_DESCRIPTION, toMarkup } from '../markup/doc.js';
import { checkMarkup } from '../markup/parse.js';
import { render } from '../markup/render.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { issueTypeGlyph, priorityGlyph, statusBadge } from '../ui/glyphs.js';
import { createMenu } from '../ui/menu.js';
import { createPopover } from '../ui/popover.js';
import { toast } from '../ui/toast.js';

export const CACHE_MS = 60_000;
const ASSIGNEE_DEBOUNCE_MS = 280;
const KEY_PATTERN = /^[A-Za-z][A-Za-z0-9_]*-\d+$/;
const IMAGE_TYPES = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp']);

const ROW = { display: 'flex', 'align-items': 'center', gap: '0.375rem', 'flex-wrap': 'wrap' };
const MUTED = { color: 'hsl(var(--muted-foreground))', 'font-size': '0.75rem' };
const SECTION = { display: 'grid', gap: '0.5rem', 'margin-top': '1.25rem' };
const HEADING = { 'font-size': '0.8125rem', 'font-weight': '600', 'text-transform': 'uppercase', 'letter-spacing': '0.04em', color: 'hsl(var(--muted-foreground))' };
const PROSE = { 'font-size': '0.875rem', 'line-height': '1.5', 'overflow-wrap': 'anywhere' };

// Shared by every drawer of the tab: `${teamId}:${key}` → {at, promise}.
const cache = new Map();

/** The description as editable markup (islands as `[[island:id]]` refs), or null when it has no markup form. */
export function descriptionMarkup(description) {
  try {
    const blocks = (description?.c ?? []).map((node) => (node.t === 'island' ? { t: 'island', id: node.id, c: [] } : node));
    return toMarkup({ t: 'doc', c: blocks });
  } catch {
    return null;
  }
}

function islandsOf(description) {
  return new Map((description?.c ?? []).filter((node) => node.t === 'island').map((node) => [node.id, node]));
}

function when(iso) {
  const ms = Date.parse(iso);
  return Number.isNaN(ms) ? '' : new Date(ms).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function heading(text) {
  return h('h3', { style: HEADING, text });
}

// The issue's details in a host: the side sheet (the drawer) or the stage's centre (the issue panel).
// host = {body, title, tools, scroller, inline, open(), hide()}.
function createIssueView(ctx, host) {
  const { teamId, jira, canEdit, explain, addRounds, onClose } = ctx;
  const base = `/api/teams/${encodeURIComponent(teamId)}/jira`;
  const sheet = host;

  let key = null;
  let issue = null;
  let comments = [];
  let commentTotal = 0;
  let options = null;
  let preview = null;
  let token = 0;
  let editor = null;
  let composer = null;
  let disposables = [];

  const issuePath = (path = '') => `${base}/issues/${encodeURIComponent(key)}${path}`;
  const imageSrc = (node) => (node.t === 'attachment' ? `${base}/issues/${encodeURIComponent(key)}/attachments/${encodeURIComponent(node.id)}` : null);

  function fetchIssue(k, force = false) {
    const id = `${teamId}:${k}`;
    const hit = cache.get(id);
    if (!force && hit && Date.now() - hit.at < CACHE_MS) {
      return hit.promise;
    }
    const promise = http.get(`${base}/issues/${encodeURIComponent(k)}`);
    cache.set(id, { at: Date.now(), promise });
    promise.catch(() => {
      if (cache.get(id)?.promise === promise) {
        cache.delete(id);
      }
    });
    return promise;
  }

  function dispose() {
    disposables.forEach((d) => d.destroy());
    disposables = [];
  }

  function resetEditors() {
    editor?.destroy();
    composer?.destroy();
    editor = null;
    composer = null;
  }

  async function open(k, { onAdd } = {}) {
    if (k !== key) {
      resetEditors();
      options = null;
      issue = null;
    }
    const switched = k !== key;
    key = k;
    if (onAdd) {
      preview = { key: k, onAdd, added: false };
    }
    sheet.title.textContent = k;
    sheet.open();
    if (switched && sheet.inline) {
      sheet.scroller.scrollTop = 0;
    }
    await load(false);
  }

  async function load(force) {
    const mine = ++token;
    if (!issue) {
      dispose();
      replaceContent(sheet.body, h('p', { class: 'jira-loading', role: 'status', style: MUTED, text: 'Loading issue…' }));
    }
    try {
      const body = await fetchIssue(key, force);
      if (mine !== token) {
        return;
      }
      issue = body;
      comments = body.comments ?? [];
      commentTotal = body.commentTotal ?? comments.length;
      paint();
    } catch (error) {
      if (mine === token) {
        dispose();
        replaceContent(sheet.body, h('p', { class: 'jira-error', role: 'alert', text: explain(error) }),
          createButton({ label: 'Try again', variant: 'secondary', size: 'sm', onClick: () => load(true) }).el);
      }
    }
  }

  function close() {
    token++;
    dispose();
    resetEditors();
    sheet.hide();
    issue = null;
    key = null;
    preview = null;
    onClose?.();
  }

  async function write(request, message = `Updated ${key}`) {
    try {
      await request;
    } catch (error) {
      toast(explain(error), { variant: 'error', duration: 6000 });
      return false;
    }
    cache.delete(`${teamId}:${key}`);
    toast(message, { variant: 'success' });
    await load(true);
    return true;
  }

  async function loadOptions() {
    options ??= http.get(issuePath('/options')).catch((error) => {
      options = null;
      throw error;
    });
    return options;
  }

  // A choice from a list that loads on first use; the menu opens under its button.
  async function choose(button, label, items, onSelect) {
    let entries;
    try {
      entries = await items();
    } catch (error) {
      toast(explain(error), { variant: 'error', duration: 6000 });
      return;
    }
    const menu = createMenu({ label, items: entries, onSelect });
    disposables.push(menu);
    const rect = button.getBoundingClientRect();
    menu.openAt(rect.left, rect.bottom + 4);
  }

  function fieldButton(name, label, content, onClick) {
    if (!canEdit) {
      return h('span', { class: `jira-field-${name}`, style: ROW }, content);
    }
    const button = h('button', {
      type: 'button',
      class: ['btn', 'btn-ghost', 'btn-sm', `jira-field-${name}`],
      'aria-label': label,
      'aria-haspopup': 'true',
      style: { ...ROW, 'justify-content': 'flex-start' },
      on: { click: () => onClick(button) },
    }, content);
    return button;
  }

  function changeStatus(button) {
    choose(button, 'Status', async () => (await loadOptions()).transitions.map((t) => ({
      action: t.id,
      label: t.to === t.name ? t.to : `${t.to} (${t.name})`,
    })), (transitionId) => write(http.post(issuePath('/transition'), { transitionId })));
  }

  function changeIssueType(button) {
    choose(button, 'Issue type', async () => (await loadOptions()).issueTypes.map((t) => ({ action: t.id, label: t.name })),
      (issueTypeId) => write(http.patch(issuePath(), { issueTypeId })));
  }

  function changePriority(button) {
    choose(button, 'Priority', async () => (await loadOptions()).priorities.map((p) => ({ action: p.id, label: p.name })),
      (priorityId) => write(http.patch(issuePath(), { priorityId })));
  }

  function changeSprint(button) {
    choose(button, 'Sprint', async () => {
      const body = await http.get(`${base}/sprints`);
      return [{ action: 'backlog', label: 'Backlog' }, ...(body.sprints ?? []).map((s) => ({ action: String(s.id), label: s.state === 'active' ? `${s.name} (active)` : s.name }))];
    }, (choice) => write(http.put(issuePath('/sprint'), { sprintId: choice === 'backlog' ? null : Number(choice) })));
  }

  function changeAssignee(button) {
    const input = h('input', { class: 'input', type: 'search', placeholder: 'Search people', 'aria-label': 'Search assignable people' });
    const results = h('div', { class: 'menu-list', role: 'listbox', 'aria-label': 'Assignable people' });
    const popover = createPopover({ anchor: button, label: 'Assignee', content: h('div', { style: { display: 'grid', gap: '0.375rem', padding: '0.5rem', width: '16rem' } }, input, results) });
    disposables.push(popover);
    let timer = 0;
    let asked = 0;
    const pick = (accountId) => {
      popover.close();
      write(http.patch(issuePath(), { assigneeAccountId: accountId }));
    };
    const option = (label, accountId) => h('button', { type: 'button', class: 'menu-item', role: 'option', on: { click: () => pick(accountId) } }, label);
    async function search() {
      const mine = ++asked;
      try {
        const body = await http.get(issuePath(`/assignable?q=${encodeURIComponent(input.value.trim())}`));
        if (mine === asked) {
          replaceContent(results, option('Unassigned', null), (body.users ?? []).map((u) => option(u.displayName, u.accountId)));
        }
      } catch (error) {
        replaceContent(results, h('p', { style: MUTED, text: explain(error) }));
      }
    }
    input.addEventListener('input', () => {
      clearTimeout(timer);
      timer = setTimeout(search, ASSIGNEE_DEBOUNCE_MS);
    });
    popover.open();
    input.focus();
    search();
  }

  function changePoints(button) {
    const input = h('input', { class: 'input', type: 'number', min: '0', max: '1000', step: '0.5', inputmode: 'decimal', value: issue.fields.points ?? '', 'aria-label': 'Story points' });
    const form = h('form', {
      style: { ...ROW, padding: '0.5rem' },
      on: {
        submit(event) {
          event.preventDefault();
          const text = input.value.trim();
          const points = text === '' ? null : Number(text);
          if (points !== null && !(points >= 0 && points <= 1000 && Number.isInteger(points * 2))) {
            input.setAttribute('aria-invalid', 'true');
            toast('Points must be between 0 and 1000, in steps of 0.5.', { variant: 'error', duration: 4000 });
            return;
          }
          popover.close();
          write(http.put(issuePath('/points'), { points }));
        },
      },
    }, input, createButton({ label: 'Save', variant: 'primary', size: 'sm', type: 'submit' }).el);
    const popover = createPopover({ anchor: button, label: 'Story points', content: form });
    disposables.push(popover);
    popover.open();
    input.focus();
    input.select();
  }

  function editSummary(title) {
    const input = h('input', { class: 'input', value: issue.fields.summary ?? '', maxlength: '255', 'aria-label': 'Summary' });
    const save = async () => {
      const text = input.value.trim();
      if (!text || text === issue.fields.summary) {
        paint();
        return;
      }
      await write(http.patch(issuePath(), { summary: text }));
    };
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        save();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        paint();
      }
    });
    input.addEventListener('blur', save, { once: true });
    title.replaceWith(input);
    input.focus();
    input.select();
  }

  function lightbox(attachments, start) {
    let at = start;
    const img = h('img', { alt: '', style: { 'max-width': '100%', 'max-height': '70dvh', display: 'block', margin: '0 auto', 'image-rendering': 'auto' } });
    const show = () => {
      const item = attachments[at];
      img.src = imageSrc({ t: 'attachment', id: item.id });
      img.alt = item.filename ?? '';
      dialog.update({ title: `${item.filename ?? 'Attachment'} (${at + 1} of ${attachments.length})` });
    };
    const step = (delta) => {
      at = (at + delta + attachments.length) % attachments.length;
      show();
    };
    const many = attachments.length > 1;
    const dialog = createDialog({
      title: '',
      className: 'jira-lightbox',
      content: img,
      actions: many
        ? [createButton({ label: 'Previous', icon: 'chevron-left', variant: 'secondary', onClick: () => step(-1) }).el,
          createButton({ label: 'Next', icon: 'chevron-right', variant: 'secondary', onClick: () => step(1) }).el]
        : [],
      onClose: () => dialog.destroy(),
    });
    dialog.el.style.setProperty('width', 'min(64rem, calc(100vw - 2rem))');
    dialog.el.addEventListener('keydown', (event) => {
      if (many && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
        event.preventDefault();
        step(event.key === 'ArrowLeft' ? -1 : 1);
      }
    });
    dialog.open();
    show();
  }

  function cloneIssue() {
    const summary = h('input', { class: 'input', value: `Clone: ${issue.fields.summary ?? key}`.slice(0, 255), maxlength: '255', 'aria-label': 'Summary' });
    const add = h('input', { type: 'checkbox', name: 'addToRounds', checked: true });
    const source = key;
    const run = async () => {
      const text = summary.value.trim();
      if (!text) {
        summary.focus();
        return;
      }
      confirm.update({ busy: true, disabled: true });
      try {
        const created = await http.post(`${base}/issues`, { summary: text, cloneFrom: source });
        dialog.close('ok');
        toast(`Created ${created.key} from ${source}`, { variant: 'success' });
        if (add.checked) {
          addRounds([created.key]);
        }
      } catch (error) {
        toast(explain(error), { variant: 'error', duration: 6000 });
        confirm.update({ busy: false, disabled: false });
      }
    };
    const confirm = createButton({ label: 'Clone', variant: 'primary', onClick: run });
    const dialog = createDialog({
      title: `Clone ${source}`,
      description: 'The clone copies the type, project and description, with a “Cloned from” line.',
      content: h('div', { style: { display: 'grid', gap: '0.75rem' } }, h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Summary' }), summary), h('label', { style: ROW }, add, 'Add the clone to the rounds')),
      actions: [createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }).el, confirm.el],
      onClose: () => dialog.destroy(),
    });
    dialog.open();
    summary.focus();
  }

  function tools() {
    const copy = createButton({
      label: 'Copy key',
      icon: 'copy',
      iconOnly: true,
      variant: 'ghost',
      size: 'icon',
      onClick: () => navigator.clipboard?.writeText(key).then(() => toast(`Copied ${key}`), () => toast('Could not copy the key.', { variant: 'error', duration: 4000 })),
    });
    const external = jira.siteHost
      ? h('a', { class: 'btn btn-ghost btn-icon jira-open-external', href: `https://${jira.siteHost}/browse/${encodeURIComponent(key)}`, target: '_blank', rel: 'noopener noreferrer', 'aria-label': `Open ${key} in Jira`, title: 'Open in Jira' },
        icon('external-link'))
      : null;
    const refresh = createButton({ label: 'Reload', icon: 'refresh-cw', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => load(true) });
    const clone = canEdit ? createButton({ label: 'Clone', icon: 'layers', variant: 'ghost', size: 'sm', onClick: cloneIssue }).el : null;
    replaceContent(sheet.tools, clone, copy.el, external, refresh.el);
  }

  function fields() {
    const f = issue.fields;
    const rows = [
      ['Status', fieldButton('status', `Change status: ${f.status}`, statusBadge(f.status, f.statusCategory), changeStatus)],
      ['Type', fieldButton('type', `Change issue type: ${f.issueType}`, [issueTypeGlyph(f.issueType), h('span', { text: f.issueType ?? '' })], changeIssueType)],
      ['Priority', fieldButton('priority', `Change priority: ${f.priority ?? 'none'}`, [priorityGlyph(f.priority), h('span', { text: f.priority ?? 'None' })], changePriority)],
      ['Assignee', fieldButton('assignee', `Change assignee: ${f.assignee?.displayName ?? 'Unassigned'}`, h('span', { text: f.assignee?.displayName ?? 'Unassigned' }), changeAssignee)],
      ['Story points', fieldButton('points', `Change story points: ${f.points ?? 'none'}`, h('span', { text: f.points == null ? '–' : String(f.points) }), changePoints)],
      ['Sprint', fieldButton('sprint', `Change sprint: ${f.sprint?.name ?? 'Backlog'}`, h('span', { text: f.sprint?.name ?? 'Backlog' }), changeSprint)],
      ['Reporter', h('span', { text: f.reporter?.displayName ?? '–' })],
      ['Parent', f.parent ? h('span', { class: 'badge', text: f.parent.summary ? `${f.parent.key} · ${f.parent.summary}` : f.parent.key }) : h('span', { text: '–' })],
      ['Labels', f.labels?.length ? h('span', { style: ROW }, f.labels.map((label) => h('span', { class: 'badge', text: label }))) : h('span', { text: '–' })],
      ['Created', h('span', { text: when(f.created) })],
      ['Updated', h('span', { text: when(issue.updated) })],
    ];
    return h('dl', { class: 'jira-fields', style: { display: 'grid', 'grid-template-columns': 'max-content 1fr', gap: '0.25rem 0.75rem', 'align-items': 'center', 'margin-top': '0.75rem', 'font-size': '0.8125rem' } },
      rows.map(([label, value]) => [h('dt', { style: MUTED, text: label }), h('dd', { style: { margin: '0', 'min-width': '0' } }, value)]));
  }

  function descriptionSection() {
    const description = issue.description ?? { t: 'doc', c: [] };
    const islands = islandsOf(description);
    const markup = descriptionMarkup(description);
    const editable = canEdit && issue.editable !== false && markup !== null;
    const section = h('section', { class: 'jira-description', 'aria-label': 'Description', style: SECTION });
    const head = h('header', { style: { ...ROW, 'justify-content': 'space-between' } }, heading('Description'));
    if (editor) {
      const save = createButton({
        label: 'Save',
        variant: 'primary',
        size: 'sm',
        async onClick() {
          const value = editor.getValue();
          const check = value.trim() === '' ? {} : checkMarkup(value, 'jiraDescription');
          if (check.error) {
            toast(check.error === 'too_long' ? 'The description is too long.' : 'The description cannot be saved as written.', { variant: 'error', duration: 5000 });
            return;
          }
          save.update({ busy: true, disabled: true });
          try {
            await http.patch(issuePath(), { description: value, descriptionBase: issue.updated });
          } catch (error) {
            save.update({ busy: false, disabled: false });
            if (error.code !== 'stale') {
              toast(explain(error), { variant: 'error', duration: 6000 });
              return;
            }
            toast(`${key} changed in Jira meanwhile, so your edit was not saved. The latest description is shown; edit it again.`, { variant: 'error', duration: 8000 });
          }
          editor.destroy();
          editor = null;
          cache.delete(`${teamId}:${key}`);
          await load(true);
        },
      });
      const cancel = createButton({ label: 'Cancel', variant: 'secondary', size: 'sm', onClick: () => { editor.destroy(); editor = null; paint(); } });
      section.append(head, editor.el, h('div', { style: ROW }, save.el, cancel.el));
      return section;
    }
    if (editable) {
      head.append(createButton({
        label: 'Edit description',
        icon: 'pencil',
        variant: 'ghost',
        size: 'sm',
        onClick() {
          editor = createEditor({ value: markup, dialect: DIALECT_JIRA_DESCRIPTION, islands, imageSrc, label: 'Description', placeholder: 'Describe the issue' });
          editor.el.classList.add('jira-description-editor');
          editor.input.style.setProperty('min-height', '8rem');
          editor.input.style.setProperty('padding', '0.5rem');
          editor.input.style.setProperty('border', '1px solid hsl(var(--input))');
          editor.input.style.setProperty('border-radius', 'var(--radius-sm)');
          paint();
          editor.focus();
        },
      }).el);
    }
    const body = h('div', { class: 'jira-prose', style: PROSE });
    body.append(render(description, { islands, imageSrc }));
    if (!body.hasChildNodes()) {
      body.append(h('p', { style: MUTED, text: 'No description.' }));
    }
    body.addEventListener('click', (event) => {
      const img = event.target.closest('img');
      const images = (issue.attachments ?? []).filter((a) => IMAGE_TYPES.has(a.mimeType));
      const at = img ? images.findIndex((a) => img.getAttribute('src') === imageSrc({ t: 'attachment', id: a.id })) : -1;
      if (at >= 0) {
        lightbox(images, at);
      }
    });
    section.append(head, body);
    return section;
  }

  function attachmentsSection() {
    const all = issue.attachments ?? [];
    if (all.length === 0) {
      return null;
    }
    const images = all.filter((a) => IMAGE_TYPES.has(a.mimeType));
    return h('section', { class: 'jira-attachments', 'aria-label': 'Attachments', style: SECTION },
      heading(`Attachments (${all.length})`),
      h('ul', { style: { ...ROW, 'list-style': 'none', margin: '0', padding: '0' } }, all.map((a) => {
        const at = images.indexOf(a);
        return h('li', null, at < 0
          ? h('span', { style: MUTED, text: a.filename })
          : h('button', {
            type: 'button',
            class: 'jira-attachment',
            'aria-label': `View ${a.filename}`,
            style: { padding: '0', border: '1px solid hsl(var(--border))', 'border-radius': 'var(--radius-sm)', background: 'transparent', cursor: 'zoom-in' },
            on: { click: () => lightbox(images, at) },
          }, h('img', { src: imageSrc({ t: 'attachment', id: a.id }), alt: a.filename ?? '', loading: 'lazy', style: { width: '4rem', height: '4rem', 'object-fit': 'cover', display: 'block' } })));
      })));
  }

  function linksSection() {
    const links = issue.links ?? [];
    const groups = new Map();
    for (const link of links) {
      const label = link.label ?? link.type;
      groups.set(label, [...(groups.get(label) ?? []), link]);
    }
    const section = h('section', { class: 'jira-links', 'aria-label': 'Linked issues', style: SECTION }, heading('Linked issues'));
    for (const [label, items] of groups) {
      section.append(h('div', null, h('p', { style: MUTED, text: label }),
        h('ul', { style: { 'list-style': 'none', margin: '0', padding: '0', display: 'grid', gap: '0.25rem' } }, items.map((link) => h('li', { class: 'jira-link', dataset: { key: link.key }, style: ROW },
          h('button', { type: 'button', class: 'btn btn-ghost btn-sm', style: { 'font-family': 'var(--font-mono)' }, text: link.key, on: { click: () => open(link.key) } }),
          h('span', { style: { flex: '1', 'min-width': '0', 'font-size': '0.8125rem' }, text: link.summary ?? '' }),
          canEdit ? createButton({ label: `Remove link to ${link.key}`, icon: 'x', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => write(http.del(issuePath(`/links/${encodeURIComponent(link.id)}`)), `Removed the link to ${link.key}`) }).el : null)))));
    }
    if (links.length === 0) {
      section.append(h('p', { style: MUTED, text: 'No linked issues.' }));
    }
    if (canEdit) {
      section.append(linkForm());
    }
    return section;
  }

  function linkForm() {
    let choice = null;
    const typeButton = createButton({ label: 'relates to', variant: 'outline', size: 'sm', className: 'jira-link-type' });
    const target = h('input', { class: 'input', name: 'targetKey', placeholder: 'Issue key', 'aria-label': 'Issue key to link', autocomplete: 'off', style: { width: '8rem' } });
    const pickType = () => choose(typeButton.el, 'Link type', async () => {
      const types = (await loadOptions()).linkTypes ?? [];
      return types.flatMap((t) => (t.inward === t.outward
        ? [{ action: `${t.id}:outward`, label: t.outward }]
        : [{ action: `${t.id}:outward`, label: t.outward }, { action: `${t.id}:inward`, label: t.inward }]));
    }, (action) => {
      const [typeId, direction] = action.split(':');
      choice = { typeId, direction };
      loadOptions().then((o) => {
        const type = o.linkTypes.find((t) => t.id === typeId);
        typeButton.update({ label: direction === 'inward' ? type.inward : type.outward });
      });
    });
    typeButton.el.addEventListener('click', pickType);
    return h('form', {
      class: 'jira-link-form',
      style: ROW,
      on: {
        async submit(event) {
          event.preventDefault();
          const targetKey = target.value.trim().toUpperCase();
          if (!KEY_PATTERN.test(targetKey) || targetKey === key) {
            toast(targetKey === key ? 'An issue cannot link to itself.' : 'Enter an issue key such as PROJ-123.', { variant: 'error', duration: 4000 });
            return;
          }
          if (!choice) {
            const types = (await loadOptions().catch(() => null))?.linkTypes ?? [];
            const relates = types.find((t) => /relat/i.test(t.name)) ?? types[0];
            if (!relates) {
              return;
            }
            choice = { typeId: relates.id, direction: 'outward' };
          }
          write(http.post(issuePath('/links'), { ...choice, targetKey }), `Linked ${key} to ${targetKey}`);
        },
      },
    }, typeButton.el, target, createButton({ label: 'Link', icon: 'link', variant: 'secondary', size: 'sm', type: 'submit' }).el);
  }

  function commentsSection() {
    const list = h('ul', { class: 'jira-comments', style: { 'list-style': 'none', margin: '0', padding: '0', display: 'grid', gap: '0.75rem' } },
      comments.map((c) => h('li', { class: 'jira-comment', dataset: { id: c.id } },
        h('p', { style: MUTED }, h('strong', { text: c.author ?? 'Someone' }), ` · ${when(c.created)}`),
        h('div', { class: 'jira-prose', style: PROSE }, render(c.body, { imageSrc })))));
    const section = h('section', { class: 'jira-comments-section', 'aria-label': 'Comments', style: SECTION }, heading(`Comments (${commentTotal})`));
    if (canEdit) {
      composer ??= createEditor({ dialect: DIALECT_JIRA, label: 'Add a comment', placeholder: 'Add a comment…', onSubmit: postComment });
      composer.input.style.setProperty('min-height', '3rem');
      composer.input.style.setProperty('padding', '0.5rem');
      composer.input.style.setProperty('border', '1px solid hsl(var(--input))');
      composer.input.style.setProperty('border-radius', 'var(--radius-sm)');
      section.append(composer.el, h('div', { style: ROW }, createButton({ label: 'Comment', icon: 'send', variant: 'secondary', size: 'sm', onClick: () => postComment(composer.getValue()) }).el));
    }
    section.append(list);
    if (comments.length < commentTotal) {
      section.append(createButton({ label: `Show more comments (${commentTotal - comments.length})`, variant: 'ghost', size: 'sm', className: 'jira-more-comments', onClick: moreComments }).el);
    }
    return section;
  }

  async function postComment(text) {
    const check = checkMarkup(text, 'jiraText');
    if (check.error) {
      toast(check.error === 'empty' ? 'Write a comment first.' : 'The comment cannot be sent as written.', { variant: 'error', duration: 4000 });
      return;
    }
    try {
      const comment = await http.post(issuePath('/comments'), { body: text });
      composer.setValue('');
      comments = [comment, ...comments.filter((c) => c.id !== comment.id)];
      commentTotal += 1;
      cache.delete(`${teamId}:${key}`);
      paint();
    } catch (error) {
      toast(explain(error), { variant: 'error', duration: 6000 });
    }
  }

  async function moreComments() {
    const k = key;
    try {
      const page = await http.get(issuePath(`/comments?startAt=${comments.length}`));
      if (k !== key) {
        return;
      }
      const seen = new Set(comments.map((c) => c.id));
      comments = [...comments, ...(page.comments ?? []).filter((c) => !seen.has(c.id))];
      commentTotal = page.total ?? commentTotal;
      paint();
    } catch (error) {
      toast(explain(error), { variant: 'error', duration: 6000 });
    }
  }

  function previewBar() {
    if (preview?.key !== key) {
      return null;
    }
    const add = createButton({
      label: preview.added ? 'Added to the rounds' : 'Add to rounds',
      icon: preview.added ? 'check' : 'plus',
      variant: 'emerald',
      press: true,
      size: 'sm',
      disabled: preview.added,
      className: 'jira-add-preview',
      onClick() {
        preview.added = true;
        preview.onAdd();
        paint();
      },
    });
    return h('div', { class: 'jira-preview', role: 'status', style: { ...ROW, 'justify-content': 'space-between', padding: '0.5rem', 'border-radius': 'var(--radius-sm)', background: 'hsl(var(--muted))', 'margin-bottom': '0.75rem' } },
      h('span', { style: { 'font-size': '0.8125rem' }, text: preview.added ? 'This issue is now a round.' : 'Everyone sees this issue as a preview round.' }), add.el);
  }

  function paint() {
    dispose();
    tools();
    const f = issue.fields;
    const title = canEdit
      ? h('button', {
        type: 'button',
        class: 'jira-summary-edit',
        'aria-label': `Edit summary: ${f.summary ?? ''}`,
        style: { 'text-align': 'start', border: '0', background: 'transparent', color: 'inherit', padding: '0', cursor: 'text', 'font-size': '1.125rem', 'font-weight': '600' },
        text: f.summary ?? '',
        on: { click: (event) => editSummary(event.currentTarget) },
      })
      : h('p', { style: { 'font-size': '1.125rem', 'font-weight': '600' }, text: f.summary ?? '' });
    const scroll = sheet.scroller.scrollTop;
    if (sheet.inline) {
      replaceContent(sheet.title, issueTypeGlyph(f.issueType), h('span', { class: 'jira-issue-key', text: key }));
    }
    replaceContent(sheet.body,
      previewBar(),
      sheet.inline ? null : h('div', { style: ROW }, issueTypeGlyph(f.issueType), h('span', { style: { 'font-family': 'var(--font-mono)', 'font-size': '0.8125rem' }, text: key })),
      title,
      fields(),
      descriptionSection(),
      attachmentsSection(),
      linksSection(),
      commentsSection());
    sheet.scroller.scrollTop = scroll;
    sheet.el.dataset.key = key;
  }

  return {
    open,
    close,
    get key() {
      return key;
    },
    prefetch(k) {
      fetchIssue(k).catch(() => {});
    },
    destroy() {
      close();
      sheet.el.remove();
    },
  };
}

/** The drawer: a side sheet for issues opened from the browse panel or a link. */
export function createIssueDrawer(ctx) {
  let view = null;
  const sheet = ctx.createSheet({ label: 'Issue details', onClose: () => view.close() });
  sheet.el.classList.add('jira-drawer');
  view = createIssueView(ctx, { ...sheet, el: sheet.el, scroller: sheet.body, inline: false });
  return view;
}

/**
 * The viewed round's issue at the centre of the stage. show(key) loads it, or shows "No issue linked" with
 * "Link a Jira issue" (ctx.onLink) for a round without a key. The stage scrolls it (`scroller`).
 */
export function createIssuePanel(ctx) {
  const title = h('h2', { class: 'jira-issue-title' });
  const tools = h('div', { class: 'jira-issue-tools' });
  const body = h('div', { class: 'jira-issue-body' });
  const el = h('article', { class: 'jira-issue', 'aria-label': 'Issue' },
    h('header', { class: 'jira-issue-head' }, title, tools), body);
  const view = createIssueView({ ...ctx, onClose: null }, {
    el, body, title, tools, scroller: ctx.scroller, inline: true, open() {}, hide() {},
  });
  let shown;

  function unlinked() {
    view.close();
    replaceContent(title);
    replaceContent(tools);
    replaceContent(body, h('div', { class: 'nt-unlinked' },
      h('p', { class: 'nt-unlinked-title', text: 'No issue linked' }),
      h('p', { class: 'nt-unlinked-text', text: 'This round is not tied to a Jira issue yet.' }),
      createButton({ label: 'Link a Jira issue', icon: 'link', variant: 'secondary', size: 'sm', className: 'nt-link-issue', onClick: () => ctx.onLink?.() }).el));
  }

  return {
    el,
    show(key) {
      if (key === shown) {
        return;
      }
      shown = key;
      if (key) {
        view.open(key);
      } else {
        unlinked();
      }
    },
    destroy() {
      view.destroy();
    },
  };
}
