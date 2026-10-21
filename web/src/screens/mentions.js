import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { parse } from '../markup/parse.js';
import { render } from '../markup/render.js';
import { currentMe, currentUser, navigate, setMe } from './app.js';
import { ago, createFrame, errorText, link, muted, notice, row, stack } from './frame.js';

// One mention: where (board), who (null author = anonymous, §d), when, and the card or comment body.
function item(m) {
  const names = m.names ?? {};
  const body = h('div', { style: { 'overflow-wrap': 'anywhere' } });
  try {
    body.append(render(parse(m.body ?? ''), { resolveMention: (id) => names[id] ?? (id === currentMe()?.principal.id ? currentMe().principal.name : null) }));
  } catch {
    body.textContent = m.body ?? '';
  }
  return h('li', { class: 'card', style: { ...stack('0.5rem'), padding: '1rem' } },
    h('div', { style: { ...row('0.5rem'), 'font-size': '0.875rem' } },
      h('strong', { text: m.author?.name ?? 'Anonymous' }),
      h('span', { text: m.sourceKind === 'comment' ? 'mentioned you in a comment on' : 'mentioned you on' }),
      link(`/retro/${encodeURIComponent(m.boardCode)}`, m.boardTitle || m.boardCode),
      m.createdAt ? h('span', { style: { color: 'hsl(var(--muted-foreground))' }, text: `· ${ago(m.createdAt)}` }) : null),
    body);
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Mentions' });
  const listEl = h('ul', { style: { ...stack('0.75rem'), margin: '0', padding: '0', 'list-style': 'none' } });
  const more = h('button', { type: 'button', class: 'btn btn-outline', hidden: true }, 'Load more');
  const status = h('div');
  frame.main.append(listEl, status, more);
  const controller = new AbortController();
  let cursor = null;
  let loaded = false;

  async function load() {
    more.disabled = true;
    replaceContent(status, cursor === null ? notice('Loading…') : '');
    try {
      const query = cursor === null ? '' : `?cursor=${encodeURIComponent(cursor)}`;
      const page = await http.get(`/api/me/mentions${query}`, { signal: controller.signal });
      listEl.append(...(page.items ?? []).map(item));
      cursor = page.next ?? null;
      more.hidden = cursor === null;
      replaceContent(status, listEl.childElementCount === 0 ? muted('Nobody has mentioned you yet.') : '');
    } catch (error) {
      if (error.name !== 'AbortError') {
        replaceContent(status, notice(errorText(error)));
      }
    } finally {
      more.disabled = false;
    }
  }

  async function markSeen() {
    try {
      await http.post('/api/me/mentions/seen', {});
      const me = currentMe();
      if (me) {
        setMe({ ...me, unreadMentions: 0 });
      }
    } catch {
      // The badge simply stays until the next visit.
    }
  }

  more.addEventListener('click', load);

  return {
    update() {
      if (!currentUser()) {
        navigate(`/login?return=${encodeURIComponent('/mentions')}`, { replace: true });
        return;
      }
      if (!loaded) {
        loaded = true;
        load().then(markSeen);
      }
    },
    unmount() {
      controller.abort();
      frame.destroy();
    },
  };
}
