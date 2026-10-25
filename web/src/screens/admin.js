// /admin/users, /admin/teams, /admin/stats (§d, app admins only): user search with disable and admin
// toggles, team membership repair, and process stats.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { confirmDialog } from '../ui/confirm.js';
import { toast } from '../ui/toast.js';
import { currentUser, navigate } from './app.js';
import { card, createFrame, errorText, field, form, link, muted, notice, row, stack } from './frame.js';

const SECTIONS = [['users', 'Users'], ['teams', 'Teams'], ['stats', 'Stats']];

const LIST = { ...stack('0.5rem'), margin: '0', padding: '0', 'list-style': 'none' };
const ITEM = { ...row('0.75rem'), 'justify-content': 'space-between', padding: '0.5rem 0', 'border-bottom': '1px solid hsl(var(--border))' };

const asList = (result) => (Array.isArray(result) ? result : result?.items ?? []);

function searchForm(label, run) {
  const q = field({ label, name: 'q', type: 'search', autocomplete: 'off' });
  return form({ fields: { q }, submitLabel: 'Search', submit: (values) => run(values.q.trim()) }).el;
}

function usersView(signal) {
  const results = h('ul', { style: LIST });

  async function setFlag(user, patch, button) {
    if (patch.disabled && !(await confirmDialog({
      title: `Disable ${user.name}?`,
      message: 'They are signed out everywhere and cannot sign in until re-enabled.',
      confirmLabel: 'Disable',
      destructive: true,
    }))) {
      return;
    }
    button.disabled = true;
    try {
      Object.assign(user, await http.patch(`/api/admin/users/${user.id}`, patch) ?? patch);
      replaceContent(button.closest('li'), ...userRow(user));
    } catch (error) {
      toast(errorText(error, { last_admin: 'At least one app admin must remain.' }), { variant: 'error', duration: 6000 });
      button.disabled = false;
    }
  }

  function userRow(user) {
    const admin = h('button', { type: 'button', class: 'btn btn-outline btn-sm' }, user.isAdmin ? 'Remove admin' : 'Make admin');
    const disable = h('button', { type: 'button', class: `btn btn-sm ${user.disabled ? 'btn-outline' : 'btn-destructive'}` }, user.disabled ? 'Enable' : 'Disable');
    admin.addEventListener('click', () => setFlag(user, { isAdmin: !user.isAdmin }, admin));
    disable.addEventListener('click', () => setFlag(user, { disabled: !user.disabled }, disable));
    return [
      h('div', { style: stack('0.125rem') },
        h('span', null,
          h('strong', { text: user.name }),
          user.isAdmin ? h('span', { class: 'badge', text: 'Admin' }) : null,
          user.disabled ? h('span', { class: 'badge', text: 'Disabled' }) : null),
        muted(user.email ?? '')),
      h('div', { style: row('0.5rem') }, admin, disable),
    ];
  }

  const search = searchForm('Email prefix or name', async (q) => {
    const users = asList(await http.get(`/api/admin/users?q=${encodeURIComponent(q)}`, { signal }));
    replaceContent(results, users.length ? users.map((u) => h('li', { style: ITEM }, ...userRow(u))) : h('li', null, muted('No users match.')));
  });
  return card('Users', search, results);
}

function teamsView(signal) {
  const results = h('ul', { style: LIST });

  function memberForm(team) {
    const userId = field({ label: 'User id', name: 'userId', autocomplete: 'off', required: true });
    const role = field({ label: 'Role (member, admin or owner)', name: 'role', autocomplete: 'off', value: 'member' });
    const holder = h('div', { style: stack('0.5rem') });
    const repair = form({
      fields: { userId, role },
      submitLabel: 'Add or set member',
      async submit(values) {
        await http.post(`/api/admin/teams/${team.id}/members`, { userId: values.userId.trim(), role: values.role.trim() || 'member' });
        toast('Membership updated.', { variant: 'success' });
      },
      extra: h('button', {
        type: 'button',
        class: 'btn btn-destructive',
        on: {
          async click() {
            const id = userId.value.trim();
            const confirmed = id && await confirmDialog({
              title: 'Remove this member?',
              message: `User ${id} loses access to ${team.name}.`,
              confirmLabel: 'Remove',
              destructive: true,
            });
            if (!confirmed) {
              return;
            }
            try {
              await http.del(`/api/admin/teams/${team.id}/members/${encodeURIComponent(id)}`);
              toast('Member removed.', { variant: 'success' });
            } catch (error) {
              toast(errorText(error), { variant: 'error', duration: 6000 });
            }
          },
        },
      }, 'Remove member'),
    });
    holder.append(repair.el);
    return holder;
  }

  const search = searchForm('Team name', async (q) => {
    const teams = asList(await http.get(`/api/admin/teams?q=${encodeURIComponent(q)}`, { signal }));
    replaceContent(results, teams.length
      ? teams.map((team) => {
        const details = h('details', null, h('summary', { text: 'Repair membership' }), memberForm(team));
        return h('li', { style: { ...stack('0.5rem'), ...ITEM, display: 'grid' } },
          h('div', null, h('strong', { text: team.name }), team.memberCount == null ? null : muted(`${team.memberCount} members`)),
          details);
      })
      : h('li', null, muted('No teams match.')));
  });
  return card('Teams', search, results);
}

function statsView(signal) {
  const body = h('dl', { style: { display: 'grid', 'grid-template-columns': 'max-content 1fr', gap: '0.25rem 1rem' } });
  const holder = card('Stats', body);
  http.get('/api/admin/stats', { signal }).then((stats) => {
    replaceContent(body, Object.entries(stats ?? {}).flatMap(([key, value]) => [
      h('dt', { text: key, style: { 'font-weight': '600' } }),
      h('dd', { text: typeof value === 'object' ? JSON.stringify(value) : String(value) }),
    ]));
  }, (error) => {
    if (error.name !== 'AbortError') {
      replaceContent(body, muted(errorText(error)));
    }
  });
  return holder;
}

const VIEWS = { users: usersView, teams: teamsView, stats: statsView };

export function mount(root) {
  const frame = createFrame(root, { title: 'Admin' });
  const nav = h('nav', { 'aria-label': 'Admin sections', style: row('0.5rem') });
  const body = h('div', { style: stack('1rem') });
  let controller = null;
  let section = null;

  return {
    update() {
      const user = currentUser();
      if (!user) {
        navigate(`/login?return=${encodeURIComponent(location.pathname)}`, { replace: true });
        return;
      }
      if (!user.isAdmin) {
        replaceContent(frame.main, notice('This area is for app admins.', link('/', 'Back to your team', { class: 'btn btn-primary' })));
        return;
      }
      const name = location.pathname.split('/')[2];
      if (!VIEWS[name]) {
        navigate('/admin/users', { replace: true });
        return;
      }
      if (name === section) {
        return;
      }
      section = name;
      controller?.abort();
      controller = new AbortController();
      replaceContent(nav, SECTIONS.map(([id, label]) => link(`/admin/${id}`, label, {
        class: `btn btn-sm ${id === name ? 'btn-secondary' : 'btn-ghost'}`,
        'aria-current': id === name ? 'page' : null,
      })));
      replaceContent(body, VIEWS[name](controller.signal));
      if (!nav.isConnected) {
        frame.main.append(nav, body);
      }
    },
    unmount() {
      controller?.abort();
      frame.destroy();
    },
  };
}
