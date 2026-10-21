import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { toast } from '../ui/toast.js';
import { currentUser, navigate } from './app.js';
import { ago, card, createFrame, errorText, link, muted, notice, row, stack } from './frame.js';

const pokerPath = (s) => (s.teamId ? `/teams/${s.teamId}/poker/${encodeURIComponent(s.code)}` : `/poker/${encodeURIComponent(s.code)}`);

function list(items, empty, render) {
  if (!items?.length) {
    return muted(empty);
  }
  return h('ul', { style: { ...stack('0.5rem'), margin: '0', padding: '0', 'list-style': 'none' } },
    items.map((item) => h('li', { style: { ...row('0.5rem'), 'justify-content': 'space-between' } }, render(item))));
}

const when = (item) => (item.lastAt ? h('span', { style: { color: 'hsl(var(--muted-foreground))', 'font-size': '0.8125rem' }, text: ago(item.lastAt) }) : null);
const where = (item) => (item.teamName ? h('span', { style: { color: 'hsl(var(--muted-foreground))', 'font-size': '0.8125rem' }, text: ` · ${item.teamName}` }) : null);

async function createQuick(kind, button) {
  button.disabled = true;
  try {
    const created = await http.post(kind === 'retro' ? '/api/retro' : '/api/poker', {});
    await navigate(`/${kind}/${encodeURIComponent(created.code)}`);
  } catch (error) {
    toast(errorText(error), { variant: 'error', duration: 6000 });
  } finally {
    button.disabled = false;
  }
}

function render(data) {
  const favorites = data.favorites ?? [];
  const teams = favorites.length ? favorites : data.teams ?? [];
  return h('div', { style: { ...stack('1.5rem'), 'grid-template-columns': 'repeat(auto-fit, minmax(16rem, 1fr))' } },
    card(favorites.length ? 'Favorite teams' : 'Recent teams',
      list(teams, 'You have not opened a team yet.', (t) => [h('span', null, link(`/teams/${t.id}`, t.name)), when(t)]),
      link('/teams', 'All teams', { class: 'btn btn-outline btn-sm' })),
    card('Recent retros',
      list(data.boards, 'No retro boards yet.', (b) => [h('span', null, link(`/retro/${encodeURIComponent(b.code)}`, b.title || b.code), where(b)), when(b)])),
    card('Recent poker sessions',
      list(data.sessions, 'No poker sessions yet.', (s) => [h('span', null, link(pokerPath(s), s.title || s.code), where(s)), when(s)])));
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Dashboard' });
  const actions = h('div', { style: row() });
  const body = h('div');
  frame.main.append(actions, body);
  for (const [kind, label, variant] of [['retro', 'New retro', 'indigo'], ['poker', 'New poker session', 'green']]) {
    const button = h('button', { type: 'button', class: `btn btn-${variant}` }, label);
    button.addEventListener('click', () => createQuick(kind, button));
    actions.append(button);
  }
  const controller = new AbortController();
  let loaded = false;

  async function load() {
    replaceContent(body, notice('Loading…'));
    try {
      replaceContent(body, render(await http.get('/api/dashboard', { signal: controller.signal })));
    } catch (error) {
      if (error.name !== 'AbortError') {
        const retry = h('button', { type: 'button', class: 'btn btn-outline btn-sm', on: { click: load } }, 'Try again');
        replaceContent(body, notice(errorText(error), retry));
      }
    }
  }

  return {
    update() {
      if (!currentUser()) {
        navigate(`/login?return=${encodeURIComponent('/dashboard')}`, { replace: true });
        return;
      }
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
