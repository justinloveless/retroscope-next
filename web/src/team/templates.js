// Board templates: columns (one may collect action items) plus the board configuration a new board
// starts with. The server takes the column list as a full array on every save.

import { h, replaceContent } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { createSwitch } from '../ui/switch.js';
import { confirmDialog } from '../ui/confirm.js';
import { toast } from '../ui/toast.js';
import { card, field, formDialog, input, loading, toastError } from './util.js';

const COLORS = ['green', 'red', 'blue', 'yellow', 'purple', 'pink', 'indigo', 'orange', 'gray'];
const DEFAULT_COLUMNS = [
  { title: 'Good', color: 'green', actionItems: false },
  { title: 'Bad', color: 'red', actionItems: false },
  { title: 'Kudos', color: 'blue', actionItems: false },
  { title: 'Action Items', color: 'yellow', actionItems: true },
];
const DEFAULT_CONFIG = {
  allowAnonymous: true,
  votingEnabled: true,
  maxVotes: null,
  showAuthorNames: true,
  stagesEnabled: false,
  enforceReadiness: false,
  allowSelfVotes: true,
  voteEmoji: '👍',
};
const SWITCHES = [
  ['allowAnonymous', 'Allow anonymous cards'],
  ['votingEnabled', 'Voting'],
  ['showAuthorNames', 'Show author names'],
  ['allowSelfVotes', 'Allow voting for your own cards'],
  ['stagesEnabled', 'Retro stages'],
  ['enforceReadiness', 'Require everyone ready before the next stage'],
];

export function createTemplatesSettings(ctx) {
  const list = h('ul', { class: 'team-list team-templates' }, h('li', null, loading('Loading templates…')));
  const add = createButton({ label: 'New template', icon: 'plus', variant: 'secondary', onClick: () => edit(null) });
  const el = card('Board templates', 'New boards start with the default template unless another is chosen.', list, h('div', { class: 'team-actions' }, add.el));
  let templates = [];

  async function load() {
    try {
      templates = (await ctx.api.get('/templates')).items;
      render();
    } catch (error) {
      toastError(error, 'The templates could not be loaded.');
    }
  }

  function render() {
    if (templates.length === 0) {
      replaceContent(list, h('li', { class: 'team-muted', text: 'No templates yet.' }));
      return;
    }
    list.replaceChildren(...templates.map((t) => h('li', { class: 'team-template', dataset: { id: t.id } },
      h('div', { class: 'team-template-main' },
        h('strong', { text: t.name }),
        t.isDefault ? h('span', { class: 'badge', text: 'Default' }) : null,
        h('div', { class: 'team-chips' }, ...t.columns.map((c) => h('span', { class: ['team-chip', `team-column-${c.color}`], text: c.title + (c.actionItems ? ' ✓' : '') })))),
      h('div', { class: 'team-actions' },
        t.isDefault ? null : createButton({ label: 'Make default', size: 'sm', variant: 'ghost', onClick: () => makeDefault(t) }).el,
        createButton({ label: `Edit ${t.name}`, icon: 'pencil', iconOnly: true, size: 'icon', variant: 'ghost', onClick: () => edit(t) }).el,
        createButton({ label: `Delete ${t.name}`, icon: 'trash', iconOnly: true, size: 'icon', variant: 'ghost', onClick: () => remove(t) }).el))));
  }

  function payload(t, changes) {
    return { name: t.name, isDefault: t.isDefault, columns: t.columns, config: t.config, ...changes };
  }

  async function makeDefault(t) {
    try {
      await ctx.api.put(`/templates/${t.id}`, payload(t, { isDefault: true }));
      await load();
    } catch (error) {
      toastError(error, 'The default template could not be changed.');
    }
  }

  async function remove(t) {
    if (!(await confirmDialog({ title: `Delete "${t.name}"?`, message: 'Boards made from it keep their columns.', confirmLabel: 'Delete', destructive: true }))) {
      return;
    }
    try {
      await ctx.api.del(`/templates/${t.id}`);
      templates = templates.filter((x) => x.id !== t.id);
      render();
    } catch (error) {
      toastError(error, 'The template could not be deleted.');
    }
  }

  function edit(template) {
    const name = input({ name: 'name', required: true, maxlength: '100', value: template?.name ?? '' });
    let columns = (template?.columns ?? DEFAULT_COLUMNS).map((c) => ({ ...c }));
    const config = { ...DEFAULT_CONFIG, ...template?.config };
    const rows = h('ol', { class: 'team-column-rows' });
    const radioName = `action-${Math.random().toString(36).slice(2, 8)}`;

    function renderRows() {
      rows.replaceChildren(...columns.map((column, i) => {
        const title = input({ value: column.title, required: true, maxlength: '100', 'aria-label': `Column ${i + 1} title` });
        title.addEventListener('input', () => {
          column.title = title.value;
        });
        const color = h('select', { class: 'select', 'aria-label': `Column ${i + 1} colour` }, ...COLORS.map((c) => h('option', { value: c }, c)));
        color.value = column.color;
        color.addEventListener('change', () => {
          column.color = color.value;
        });
        const action = h('input', { type: 'radio', name: radioName, checked: column.actionItems, 'aria-label': `Column ${i + 1} collects action items` });
        action.addEventListener('change', () => {
          columns.forEach((c, j) => {
            c.actionItems = j === i;
          });
        });
        return h('li', { class: 'team-column-row' }, title, color,
          h('label', { class: 'team-radio' }, action, h('span', { text: 'Action items' })),
          createButton({
            label: `Remove column ${i + 1}`,
            icon: 'x',
            iconOnly: true,
            size: 'icon',
            variant: 'ghost',
            disabled: columns.length === 1,
            onClick() {
              columns = columns.filter((_, j) => j !== i);
              renderRows();
            },
          }).el);
      }));
    }
    renderRows();

    const switches = SWITCHES.map(([key, label]) => {
      const control = createSwitch({ label, checked: config[key], onChange: (checked) => { config[key] = checked; } });
      return control.el;
    });
    const maxVotes = input({ name: 'maxVotes', type: 'number', min: '0', max: '99', value: String(config.maxVotes ?? 0) });
    const emoji = input({ name: 'voteEmoji', maxlength: '16', value: config.voteEmoji });

    formDialog({
      title: template ? `Edit "${template.name}"` : 'New template',
      content: [
        field('Name', name),
        h('fieldset', { class: 'team-fieldset' }, h('legend', { text: 'Columns' }), rows,
          createButton({
            label: 'Add column',
            icon: 'plus',
            size: 'sm',
            variant: 'ghost',
            onClick() {
              columns.push({ title: '', color: 'gray', actionItems: false });
              renderRows();
            },
          }).el),
        h('fieldset', { class: 'team-fieldset' }, h('legend', { text: 'Board options' }), switches,
          h('div', { class: 'team-form-row' }, field('Votes per person (0 = unlimited)', maxVotes), field('Vote emoji', emoji))),
      ],
      submitLabel: template ? 'Save template' : 'Create template',
      async submit() {
        const body = {
          name: name.value.trim(),
          isDefault: template?.isDefault ?? false,
          columns: columns.map((c) => ({ title: c.title.trim(), color: c.color, actionItems: c.actionItems })),
          config: { ...config, maxVotes: Number(maxVotes.value) > 0 ? Number(maxVotes.value) : null, voteEmoji: emoji.value.trim() || '👍' },
        };
        if (template) {
          await ctx.api.put(`/templates/${template.id}`, body);
        } else {
          await ctx.api.post('/templates', body);
        }
        toast('Template saved.', { variant: 'success', duration: 3000 });
        await load();
      },
    });
  }

  load();
  return { el };
}
