import { h, replaceContent } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { toast } from '../ui/toast.js';
import { card, input, loading, toastError } from './util.js';

// Endorsement (kudos) types. Deleting one archives it, so past kudos keep their type.
export function createEndorsementTypesSettings(ctx) {
  const list = h('ul', { class: 'team-list team-endorsement-types' }, h('li', null, loading('Loading kudos types…')));
  const emoji = input({ name: 'emoji', maxlength: '16', value: '🏆', 'aria-label': 'New type emoji' });
  const name = input({ name: 'name', maxlength: '50', placeholder: 'Name', 'aria-label': 'New type name' });
  const description = input({ name: 'description', maxlength: '200', placeholder: 'Description', 'aria-label': 'New type description' });
  const add = createButton({ label: 'Add type', icon: 'plus', size: 'sm', variant: 'secondary', onClick: create });
  const el = card('Kudos types', 'What people can give each other kudos for on retro boards.', list,
    h('div', { class: 'team-type-row team-type-new' }, emoji, name, description, add.el));
  let types = [];

  async function load() {
    try {
      types = (await ctx.api.get('/endorsement-types')).items.filter((t) => !t.archived);
      render();
    } catch (error) {
      toastError(error, 'The kudos types could not be loaded.');
    }
  }

  function render() {
    if (types.length === 0) {
      replaceContent(list, h('li', { class: 'team-muted', text: 'No kudos types yet.' }));
      return;
    }
    list.replaceChildren(...types.map((type) => {
      const typeEmoji = input({ value: type.emoji, maxlength: '16', 'aria-label': `${type.name} emoji` });
      const typeName = input({ value: type.name, maxlength: '50', 'aria-label': `${type.name} name` });
      const typeDescription = input({ value: type.description ?? '', maxlength: '200', 'aria-label': `${type.name} description` });
      return h('li', { class: 'team-type-row', dataset: { id: type.id } }, typeEmoji, typeName, typeDescription,
        createButton({
          label: `Save ${type.name}`,
          icon: 'save',
          iconOnly: true,
          size: 'icon',
          variant: 'ghost',
          async onClick() {
            const changes = {};
            for (const [key, control] of [['emoji', typeEmoji], ['name', typeName], ['description', typeDescription]]) {
              if (control.value.trim() !== (type[key] ?? '')) {
                changes[key] = control.value.trim();
              }
            }
            if (Object.keys(changes).length === 0) {
              return;
            }
            try {
              Object.assign(type, await ctx.api.patch(`/endorsement-types/${type.id}`, changes));
              toast('Kudos type saved.', { variant: 'success', duration: 2500 });
            } catch (error) {
              toastError(error, 'The kudos type could not be saved.');
            }
          },
        }).el,
        createButton({
          label: `Archive ${type.name}`,
          icon: 'trash',
          iconOnly: true,
          size: 'icon',
          variant: 'ghost',
          async onClick() {
            try {
              await ctx.api.del(`/endorsement-types/${type.id}`);
              types = types.filter((t) => t.id !== type.id);
              render();
            } catch (error) {
              toastError(error, 'The kudos type could not be archived.');
            }
          },
        }).el);
    }));
  }

  async function create() {
    if (!name.value.trim()) {
      name.focus();
      return;
    }
    try {
      const type = await ctx.api.post('/endorsement-types', {
        name: name.value.trim(),
        emoji: emoji.value.trim() || '🏆',
        description: description.value.trim(),
      });
      types = [...types, type];
      name.value = '';
      description.value = '';
      render();
    } catch (error) {
      toastError(error, 'The kudos type could not be added.');
    }
  }

  load();
  return { el };
}
