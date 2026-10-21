// Shared fixtures for the shell screen specs: principals, a switchable boot JSON, default HTTP
// fixtures for every shell API, and a tiny PNG reader for first-paint screenshots.
import { inflateSync } from 'node:zlib';
import { json } from '../../../web-fake-server/http.js';

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
  teams: [{ id: '01900000-0000-7000-8000-0000000000a1', name: 'Platform', role: 'owner' }],
  unreadMentions: 2,
});

export const ADMIN = Object.freeze({ ...USER, user: { ...USER.user, isAdmin: true } });

export const TEAM_ID = USER.teams[0].id;

/** Serves `me` in the shell and /api/boot; returns setMe(next) for sign-in flows. */
export function useSession(fake, me = null, { theme } = {}) {
  let current = me;
  fake.boot = (path) => ({ build: 'fake', theme: theme ?? current?.user?.theme ?? 'system', me: current, path });
  return (next) => {
    current = next;
  };
}

export function installApi(fake) {
  fake.route('GET', '/api/dashboard', json({
    teams: [{ id: TEAM_ID, name: 'Platform', lastAt: '2026-10-20T10:00:00Z' }],
    favorites: [],
    boards: [{ id: 'b1', code: 'BOARDCODE1', title: 'Sprint 42 retro', teamName: 'Platform', lastAt: '2026-10-20T09:00:00Z' }],
    sessions: [{ id: 's1', code: 'POKERCODE1', teamId: TEAM_ID, teamName: 'Platform', lastAt: '2026-10-19T09:00:00Z' }],
  }));
  fake.route('GET', '/api/teams', json([
    { id: TEAM_ID, name: 'Platform', description: 'The platform crew', role: 'owner', favorite: false },
    { id: '01900000-0000-7000-8000-0000000000a2', name: 'Apps', role: 'member', favorite: true },
  ]));
  fake.route('PUT', '/api/teams/:id/favorite', { status: 204 });
  fake.route('DELETE', '/api/teams/:id/favorite', { status: 204 });
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

/**
 * A main header link by name: in the header's nav on wide screens, in the slide-out menu below 768 px
 * (which this opens).
 */
export async function mainLink(page, name) {
  const burger = page.getByRole('button', { name: 'Menu', exact: true });
  if (await burger.isVisible()) {
    await burger.click();
    return page.getByRole('dialog', { name: 'Menu' }).getByRole('link', { name, exact: true });
  }
  return page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name, exact: true });
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
