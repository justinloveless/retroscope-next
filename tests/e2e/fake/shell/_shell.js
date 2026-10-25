// Shared fixtures for the shell screen specs: principals and their teams, a switchable boot JSON, default HTTP
// fixtures for every shell API and the team page, header helpers, and a tiny PNG reader for first-paint
// screenshots.
import { inflateSync } from 'node:zlib';
import { json, problem } from '../../../web-fake-server/http.js';

export const TEAM_ID = '01900000-0000-7000-8000-0000000000a1';
export const APPS_ID = '01900000-0000-7000-8000-0000000000a2';

// Ada owns Platform and is a member of Apps; Platform is the team she used last, so '/' leads there although
// Apps comes first by name.
export const USER = Object.freeze({
  principal: { id: '01900000-0000-7000-8000-000000000001', kind: 'user', name: 'Ada Lovelace' },
  user: {
    email: 'ada@example.com',
    emailVerified: true,
    fullName: 'Ada Lovelace',
    nickname: 'Ada',
    theme: 'system',
    isAdmin: false,
    hasPassword: true,
  },
  teams: [
    { id: APPS_ID, name: 'Apps', role: 'member', openActions: 0 },
    { id: TEAM_ID, name: 'Platform', role: 'owner', openActions: 3 },
  ],
  lastTeamId: TEAM_ID,
  unreadMentions: 2,
});

export const ADMIN = Object.freeze({ ...USER, user: { ...USER.user, isAdmin: true } });

/** A signed-in user in no team yet, with an address still to verify: '/' leads to the create-or-join screen. */
export const NEWCOMER = Object.freeze({
  ...USER,
  user: { ...USER.user, emailVerified: false },
  teams: [],
  lastTeamId: null,
  unreadMentions: 0,
});

const principalOf = (me) => ({ pid: me.principal.id, kind: me.principal.kind, name: me.principal.name });

/**
 * Serves `me` in the shell and /api/boot (and /api/me, which pages reload after a team change), and makes the
 * socket speak for the same user; returns setMe(next) for sign-in flows.
 */
export function useSession(fake, me = null, { theme } = {}) {
  let current = me;
  fake.boot = (path) => ({ build: 'fake', theme: theme ?? current?.user?.theme ?? 'system', me: current, path });
  fake.me = () => (current ? principalOf(current) : null);
  fake.route('GET', '/api/me', () => (current ? json(current) : problem(401, 'unauthenticated')));
  return (next) => {
    current = next;
  };
}

const TEAM_INFO = {
  [TEAM_ID]: { name: 'Platform', description: 'The platform crew', role: 'owner' },
  [APPS_ID]: { name: 'Apps', description: 'Mobile and web apps', role: 'member' },
};

/** The team page's API and rooms for Platform and Apps, with empty lists: enough for its sections to show. */
export function installTeams(fake, me = USER) {
  for (const [id, info] of Object.entries(TEAM_INFO)) {
    fake.route('GET', `/api/teams/${id}`, json({
      id,
      ...info,
      pointDescriptions: {},
      maxEndorsements: 3,
      jira: { configured: false, status: 'ok', membersCanEdit: true },
      counts: { boards: 0, members: 1 },
    }));
    for (const list of ['poker-sessions', 'boards', 'action-items', 'invites', 'templates']) {
      fake.route('GET', `/api/teams/${id}/${list}`, json({ items: [], nextCursor: null }));
    }
    fake.route('GET', `/api/teams/${id}/endorsements/leaderboard`, json({ entries: [] }));
    fake.room(`team:${id}`, {
      snapshot: {
        teamId: id,
        name: info.name,
        members: [{ pid: me.principal.id, name: me.principal.name, role: info.role }],
        actionItems: [],
        endorsementTypes: [],
        pointDescriptions: {},
        maxEndorsements: 3,
      },
    });
  }
}

export function installApi(fake) {
  fake.route('GET', '/api/me/mentions', json({
    items: [{
      sourceKind: 'card',
      sourceId: 'c1',
      boardCode: 'BOARDCODE1',
      boardTitle: 'Sprint 42 retro',
      author: { name: 'Grace Hopper' },
      body: `Thanks [[mention:${USER.principal.id}:Ada]] for the **deploy** fix`,
      createdAt: '2026-10-20T08:00:00Z',
    }, {
      sourceKind: 'comment',
      sourceId: 'm2',
      boardCode: 'BOARDCODE1',
      boardTitle: 'Sprint 42 retro',
      author: null,
      body: 'Anonymous shout-out',
      createdAt: '2026-10-19T08:00:00Z',
    }],
    next: null,
  }));
  fake.route('POST', '/api/me/mentions/seen', { status: 204 });
  fake.route('GET', '/api/me/sessions', json([
    { id: 'aaaaaaaa-0000-0000-0000-000000000001', method: 'password', createdAt: '2026-10-01T08:00:00Z', lastSeenAt: '2026-10-20T08:00:00Z', userAgent: 'Firefox on Linux', current: true },
    { id: 'aaaaaaaa-0000-0000-0000-000000000002', method: 'google', createdAt: '2026-09-01T08:00:00Z', lastSeenAt: '2026-10-10T08:00:00Z', userAgent: 'Safari on iOS', current: false },
  ]));
  fake.route('DELETE', '/api/me/sessions/:id', { status: 204 });
  fake.route('GET', '/api/me/endorsements', json([{ emoji: '🧩', typeName: 'Problem Solver', fromName: 'Grace Hopper', teamName: 'Platform' }]));
  fake.route('POST', '/api/auth/logout', { status: 204 });
  fake.route('GET', '/api/admin/users', json([
    { id: 'u2', name: 'Grace Hopper', email: 'g•••@example.com', isAdmin: false, disabled: false },
  ]));
  fake.route('PATCH', '/api/admin/users/:id', (req) => json({ id: req.params.id, ...req.body }));
  fake.route('GET', '/api/admin/teams', json([{ id: TEAM_ID, name: 'Platform', memberCount: 4 }]));
  fake.route('GET', '/api/admin/stats', json({ rssBytes: 73400320, liveRooms: 3, sockets: 12 }));
  fake.route('POST', '/api/auth/magic/preview', json({ email: 'a•••@example.com' }));
  fake.route('POST', '/api/invites/preview', json({ teamName: 'Platform', kind: 'link', expired: false }));
}

/** The app header. */
export const header = (page) => page.locator('header.app-header');

/** True below 768 px, where the account button opens the slide-out sheet instead of the account menu. */
export const narrow = (page) => page.viewportSize().width < 768;

/** The header's sections of `team` ("<team> sections"). */
export const sections = (page, team = 'Platform') => header(page).getByRole('navigation', { name: `${team} sections` });

/**
 * Opens the account items: the account menu on wide screens, the slide-out sheet below 768 px. Returns the
 * container to look for them in, with `item(name)` for the entry (a menu item or a sheet link or button).
 */
export async function openAccount(page) {
  await header(page).getByRole('button', { name: /^Account menu for / }).filter({ visible: true }).click();
  if (narrow(page)) {
    const sheet = page.getByRole('dialog', { name: 'Menu' });
    return { el: sheet, item: (name) => sheet.getByRole('link', { name, exact: true }).or(sheet.getByRole('button', { name, exact: true })) };
  }
  const menu = page.getByRole('menu', { name: 'Account' });
  return { el: menu, item: (name) => menu.getByRole('menuitem', { name, exact: true }).or(menu.getByRole('menuitemradio', { name, exact: true })) };
}

export const requests = (fake, method, path) => fake.requests.filter((r) => r.method === method && r.path === path);

/** Collects uncaught page errors; assert `toEqual([])` at the end of a test. */
export function pageErrors(page) {
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  return errors;
}

/** Decodes an 8-bit RGB or RGBA, non-interlaced PNG (what Playwright screenshots are). */
export function readPng(buffer) {
  let offset = 8;
  let width = 0;
  let height = 0;
  let channels = 0;
  const data = [];
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('latin1', offset + 4, offset + 8);
    const chunk = buffer.subarray(offset + 8, offset + 8 + length);
    if (type === 'IHDR') {
      width = chunk.readUInt32BE(0);
      height = chunk.readUInt32BE(4);
      const colorType = chunk[9];
      if (chunk[8] !== 8 || (colorType !== 2 && colorType !== 6) || chunk[12] !== 0) {
        throw new Error(`unsupported PNG (bit depth ${chunk[8]}, colour type ${colorType})`);
      }
      channels = colorType === 6 ? 4 : 3;
    } else if (type === 'IDAT') {
      data.push(chunk);
    }
    offset += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(data));
  const stride = width * channels;
  const pixels = Buffer.alloc(height * stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? pixels[y * stride + x - channels] : 0;
      const up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
      const upLeft = y > 0 && x >= channels ? pixels[(y - 1) * stride + x - channels] : 0;
      let value = line[x];
      if (filter === 1) {
        value += left;
      } else if (filter === 2) {
        value += up;
      } else if (filter === 3) {
        value += (left + up) >> 1;
      } else if (filter === 4) {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left);
        const pb = Math.abs(p - up);
        const pc = Math.abs(p - upLeft);
        value += pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
      }
      pixels[y * stride + x] = value & 0xff;
    }
  }
  return {
    width,
    height,
    pixel(x, y) {
      const i = y * stride + x * channels;
      return [pixels[i], pixels[i + 1], pixels[i + 2]];
    },
  };
}

/** Mean relative luminance (0–1) of a screenshot's pixels, sampled on a grid. */
export function meanLuminance(png) {
  let sum = 0;
  let count = 0;
  for (let y = 0; y < png.height; y += 8) {
    for (let x = 0; x < png.width; x += 8) {
      const [r, g, b] = png.pixel(x, y);
      sum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
      count++;
    }
  }
  return sum / count;
}
