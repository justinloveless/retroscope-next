import { h, patch, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { reconcile } from '../core/list.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { currentUser, navigate } from './app.js';
import { createFrame, errorText, field, form, link, muted, notice, row, stack } from './frame.js';

const ROLE_LABELS = { owner: 'Owner', admin: 'Admin', member: 'Member' };

const byFavoriteThenName = (a, b) => Number(Boolean(b.favorite)) - Number(Boolean(a.favorite)) || a.name.localeCompare(b.name);

function openCreate() {
  const name = field({ label: 'Team name', name: 'name', required: true, maxlength: 100, autocomplete: 'off' });
  const description = field({ label: 'Description (optional)', name: 'description', type: 'textarea', maxlength: 500, rows: 3 });
  const create = form({
    fields: { name, description },
    submitLabel: 'Create team',
    messages: {
      email_unverified: 'Verify your email address before creating a team.',
      rate_limited: 'You have created many teams today. Try again tomorrow.',
    },
    async submit(values) {
      const team = await http.post('/api/teams', { name: values.name.trim(), description: values.description.trim() || null });
      dialog.close();
      await navigate(`/teams/${team.id}`);
    },
  });
  const dialog = createDialog({ title: 'Create a team', content: create.el, onClose: () => dialog.destroy() });
  dialog.open();
  name.input.focus();
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Teams' });
  const createButton = h('button', { type: 'button', class: 'btn btn-primary' }, icon('plus'), 'Create team');
  createButton.addEventListener('click', openCreate);
  const unverified = muted('Verify your email address to create a team.');
  const listEl = h('ul', { style: { ...stack('0.75rem'), margin: '0', padding: '0', 'list-style': 'none' } });
  const body = h('div');
  frame.main.append(h('div', { style: row() }, createButton, unverified), body);
  const controller = new AbortController();
  let teams = [];
  let loaded = false;

  function showTeam(el, t) {
    el.querySelector('[data-role]').textContent = ROLE_LABELS[t.role] ?? t.role ?? '';
    const star = el.querySelector('[data-action=favorite]');
    patch(star, { 'aria-pressed': String(Boolean(t.favorite)), 'aria-label': t.favorite ? `Unfavorite ${t.name}` : `Favorite ${t.name}` });
    star.firstChild.style.setProperty('fill', t.favorite ? 'var(--amber)' : 'none');
  }

  function renderList() {
    if (teams.length === 0) {
      replaceContent(body, notice('You are not in any team yet. Create one, or ask a teammate for an invite link.'));
      return;
    }
    if (listEl.parentNode !== body) {
      replaceContent(body, listEl);
    }
    reconcile(listEl, [...teams].sort(byFavoriteThenName), (t) => t.id, (t) => {
      const el = h('li', { class: 'card', style: { ...row('1rem'), padding: '1rem', 'justify-content': 'space-between' } },
        h('div', { style: stack('0.25rem') },
          link(`/teams/${t.id}`, t.name, { style: { 'font-weight': '600' } }),
          t.description ? muted(t.description) : null),
        h('div', { style: row('0.5rem') },
          h('span', { class: 'badge', dataset: { role: '' } }),
          h('button', { type: 'button', class: 'btn btn-ghost btn-icon', dataset: { action: 'favorite', id: t.id } }, icon('star'))));
      showTeam(el, t);
      return el;
    }, showTeam, { flip: true });
  }

  async function toggleFavorite(id) {
    const team = teams.find((t) => t.id === id);
    const favorite = !team.favorite;
    teams = teams.map((t) => (t.id === id ? { ...t, favorite } : t));
    renderList();
    try {
      await (favorite ? http.put(`/api/teams/${id}/favorite`) : http.del(`/api/teams/${id}/favorite`));
    } catch (error) {
      teams = teams.map((t) => (t.id === id ? { ...t, favorite: !favorite } : t));
      renderList();
      toast(errorText(error), { variant: 'error', duration: 5000 });
    }
  }

  listEl.addEventListener('click', (event) => {
    const button = event.target.closest('[data-action=favorite]');
    if (button) {
      toggleFavorite(button.dataset.id);
    }
  });

  async function load() {
    replaceContent(body, notice('Loading…'));
    try {
      const result = await http.get('/api/teams', { signal: controller.signal });
      teams = Array.isArray(result) ? result : result?.items ?? [];
      renderList();
    } catch (error) {
      if (error.name !== 'AbortError') {
        replaceContent(body, notice(errorText(error), h('button', { type: 'button', class: 'btn btn-outline btn-sm', on: { click: load } }, 'Try again')));
      }
    }
  }

  return {
    update() {
      const user = currentUser();
      if (!user) {
        navigate(`/login?return=${encodeURIComponent('/teams')}`, { replace: true });
        return;
      }
      createButton.disabled = !user.emailVerified;
      unverified.hidden = user.emailVerified;
      if (!loaded) {
        loaded = true;
        load();
      }
    },
    unmount() {
      controller.abort();
      frame.destroy();
    },
  };
}
