import { h, replaceContent } from '../core/h.js';
import { createAvatar } from '../ui/avatar.js';
import { errorMessage, avatarSrc, loading } from './util.js';

const PERIODS = [['30d', 'Last 30 days'], ['90d', 'Last 90 days'], ['all', 'All time']];

export function createKudosTab(ctx) {
  const period = h('select', { class: 'select', name: 'period', 'aria-label': 'Period' },
    ...PERIODS.map(([value, label]) => h('option', { value }, label)));
  const body = h('div');
  const el = h('section', { class: 'team-section', 'aria-label': 'Kudos' },
    h('div', { class: 'team-toolbar' }, h('h2', { class: 'team-section-title', text: 'Kudos leaderboard' }), period),
    body);

  let entries = null;
  let token = 0;

  function selected() {
    const value = ctx.query().get('period');
    return PERIODS.some(([p]) => p === value) ? value : '30d';
  }

  period.value = selected();
  period.addEventListener('change', () => {
    ctx.setQuery({ period: period.value === '30d' ? null : period.value });
    load();
  });

  async function load() {
    const run = ++token;
    replaceContent(body, loading('Loading kudos…'));
    try {
      const page = await ctx.api.get(`/endorsements/leaderboard?period=${period.value}`);
      if (run === token) {
        entries = page.entries;
        render();
      }
    } catch (error) {
      if (run === token) {
        replaceContent(body, h('p', { class: 'team-muted', text: errorMessage(error, 'The leaderboard could not be loaded.') }));
      }
    }
  }

  function render() {
    if (entries === null) {
      return;
    }
    if (entries.length === 0) {
      replaceContent(body, h('p', { class: 'team-muted', text: 'No kudos in this period yet. Give some on a retro board.' }));
      return;
    }
    const types = (ctx.room.view?.endorsementTypes ?? []).filter((t) => !t.archived || entries.some((e) => e.counts?.[t.id]));
    replaceContent(body, h('div', { class: 'team-table-wrap' }, h('table', { class: 'team-table team-leaderboard' },
      h('thead', null, h('tr', null,
        h('th', { text: '#' }),
        h('th', { text: 'Member' }),
        ...types.map((t) => h('th', { title: t.name }, h('span', { 'aria-hidden': 'true', text: t.emoji }), h('span', { class: 'sr-only', text: t.name }))),
        h('th', { text: 'Total' }))),
      h('tbody', null, ...entries.map((entry, i) => h('tr', { dataset: { pid: entry.pid } },
        h('td', { text: String(i + 1) }),
        h('td', null, h('span', { class: 'team-person' }, createAvatar({ name: entry.name, src: avatarSrc(entry.avatar), size: 'sm' }).el, entry.name)),
        ...types.map((t) => h('td', { text: String(entry.counts?.[t.id] ?? 0) })),
        h('td', null, h('strong', { text: String(entry.total) }))))))));
  }

  const stop = ctx.room.select((s) => s?.endorsementTypes, Object.is, render);
  load();

  return {
    el,
    queryChanged() {
      if (period.value !== selected()) {
        period.value = selected();
        load();
      }
    },
    destroy() {
      stop();
      token++;
      el.remove();
    },
  };
}
