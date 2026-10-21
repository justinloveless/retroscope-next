// The HTTP API of the demo: §d routes with the response shapes of docs/decisions/U29.md and U30.md,
// answered from the live demo state so a change made over HTTP shows up in the rooms and the other way round.
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { json, problem } from '../http.js';
import { BURNDOWN_BLOB, CODES, DEMO_TEAM } from './data.js';
import { DEMO, avatarSvg, burndownSvg, personByKey } from './people.js';
import { PokerSession } from './poker.js';
import { RetroBoard } from './retro.js';
import { Team } from './team.js';
import { AS_COOKIE, DAY, GUEST_COOKIE, SIGNED_OUT, compact, cookie, maskEmail, newId } from './util.js';

const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const MAX_IMAGE_BYTES = 2 * 1024 * 1024;
const MAX_AVATAR_BYTES = 256 * 1024;
const IMAGE_TYPES = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);

// The files browsers and crawlers ask for at the site root, as the real server's StaticAssets serves them:
// the manifest's `public` list, cached for a day and revalidated by ETag.
const ROOT_FILES = {
  '/favicon.ico': 'image/x-icon',
  '/manifest.webmanifest': 'application/manifest+json',
  '/robots.txt': 'text/plain; charset=utf-8',
};
const ROOT_CACHE = 'public, max-age=86400';

const noContent = { status: 204 };
const page = (items) => json({ items, nextCursor: null });
const unauthenticated = () => problem(401, 'unauthenticated');
const forbidden = () => problem(403, 'forbidden');
const notFound = () => problem(404, 'not_found');

const DEFAULT_COLUMNS = [
  { title: 'Went well', color: 'green', actionItems: false },
  { title: 'To improve', color: 'red', actionItems: false },
  { title: 'Kudos', color: 'pink', actionItems: false },
  { title: 'Action items', color: 'yellow', actionItems: true },
];
const DEFAULT_CONFIG = {
  allowAnonymous: true,
  votingEnabled: true,
  showAuthorNames: true,
  stagesEnabled: true,
  enforceReadiness: false,
  allowSelfVotes: false,
  voteEmoji: '👍',
  sortChronologically: false,
  maxVotes: 3,
};

export function installRoutes(demo) {
  const fake = demo.fake;
  const route = (method, pattern, handler) => fake.route(method, pattern, (req) => handler(req, demo.identify(req)));
  const user = (handler) => (req, who) => (who?.kind === 'user' ? handler(req, who) : unauthenticated());
  const member = (handler) => user((req, who) => {
    const team = demo.teams.get(req.params.id);
    if (!team || team.deleted || !team.roleOf(who.pid)) {
      return notFound();
    }
    return handler(req, who, team);
  });
  const admin = (handler) => member((req, who, team) => (team.isAdmin(who.pid) ? handler(req, who, team) : forbidden()));
  const appAdmin = (handler) => user((req, who) => (who.isAdmin ? handler(req, who) : forbidden()));

  const code = () => {
    let value;
    do {
      value = Array.from({ length: 10 }, () => CROCKFORD[Math.floor(demo.random() * CROCKFORD.length)]).join('');
    } while (demo.boardByCode(value) || demo.sessionByCode(value));
    return value;
  };

  function mail(to, subject, path) {
    const token = newId();
    demo.tokens.set(token, { email: to, path });
    demo.log(`  [mail] to ${to}: ${subject}: ${demo.publicOrigin()}${path}#t=${token}`);
  }

  // ---- static-ish ---------------------------------------------------------------------------------

  route('GET', '/blob/:id', (req) => {
    const stored = demo.blobs.get(req.params.id);
    const headers = { 'Cache-Control': 'private, max-age=3600', 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': "default-src 'none'; style-src 'unsafe-inline'; sandbox" };
    if (stored) {
      return { body: stored.bytes, headers: { ...headers, 'Content-Type': stored.type } };
    }
    if (req.params.id === BURNDOWN_BLOB) {
      return { body: burndownSvg(), headers: { ...headers, 'Content-Type': 'image/svg+xml' } };
    }
    const owner = demo.people().find((p) => p.avatar === req.params.id);
    return owner ? { body: avatarSvg(owner.pid, owner.hue ?? 210), headers: { ...headers, 'Content-Type': 'image/svg+xml' } } : notFound();
  });

  for (const [path, type] of Object.entries(ROOT_FILES)) {
    route('GET', path, async (req) => {
      const listed = fake.dist.manifest.public?.[path];
      if (!listed) {
        return notFound();
      }
      const headers = { 'Cache-Control': ROOT_CACHE, ETag: listed.etag, 'X-Content-Type-Options': 'nosniff' };
      if (req.headers['if-none-match'] === listed.etag) {
        return { status: 304, headers };
      }
      return { body: await readFile(join(fake.dist.dir, path)), headers: { ...headers, 'Content-Type': type } };
    });
  }

  route('GET', '/auth/oidc/:provider/start', () => ({ status: 302, headers: { Location: '/login?error=provider_disabled' } }));

  // ---- demo control (scripts and screenshots) ------------------------------------------------------

  route('GET', '/__demo/state', () => json({
    codes: CODES,
    team: DEMO_TEAM,
    peers: demo.peersOn,
    people: demo.people().map((p) => ({ key: p.key, name: p.name, pid: p.pid })),
    problems: demo.problems,
  }));
  route('POST', '/__demo/peers', (req) => {
    if (req.body?.on === false) {
      demo.stopPeers();
    } else {
      demo.startPeers();
    }
    return json({ peers: demo.peersOn });
  });
  route('POST', '/__demo/tick', (req) => {
    for (let i = 0; i < (req.body?.times ?? 1); i++) {
      demo.tick();
    }
    return noContent;
  });
  route('POST', '/__demo/celebrate', () => {
    const board = demo.boardByCode(CODES.discussing);
    const types = board.team.liveEndorsementTypes();
    const giver = board.peers.find((p) => !board.endorsements.some((e) => e.from === p.pid && e.to === DEMO.pid && e.typeId === types[1].id));
    const result = giver ? board.act(giver, 'retro.endorse.give', { endorsementId: newId(), toUserId: DEMO.pid, typeId: types[1].id }) : { nack: 'endorse_limit' };
    return result.nack ? problem(409, result.nack) : noContent;
  });

  // ---- auth -----------------------------------------------------------------------------------------

  const signIn = (who) => ({ status: 200, body: demo.meBody(who), headers: { 'Set-Cookie': cookie(AS_COOKIE, who.key, { maxAge: 365 * 24 * 3600 }) } });

  route('POST', '/api/auth/login', (req) => {
    const who = demo.personByEmail(req.body?.email);
    return who ? signIn(who) : problem(401, 'invalid_credentials');
  });
  route('POST', '/api/auth/signup', (req) => {
    const email = String(req.body?.email ?? '').trim().toLowerCase();
    const fullName = String(req.body?.fullName ?? '').trim();
    if (String(req.body?.password ?? '').length < 10) {
      return problem(400, 'validation', { fields: { password: 'too_short' } });
    }
    if (!demo.personByEmail(email) && fullName && email.includes('@')) {
      const n = demo.extraPeople.size + 1;
      const key = `new${n}`;
      const pid = `0d3e0000-0002-7000-8000-${String(900 + n).padStart(12, '0')}`;
      demo.extraPeople.set(key, { pid, key, kind: 'user', name: fullName, nickname: fullName.split(' ')[0], email, emailVerified: false, isAdmin: false, theme: 'system', hue: 190 });
      mail(email, 'Verify your email', '/auth/verify');
    }
    return { status: 202 };
  });
  const signOut = () => ({ status: 204, headers: { 'Set-Cookie': cookie(AS_COOKIE, SIGNED_OUT, { maxAge: 365 * 24 * 3600 }) } });
  route('POST', '/api/auth/logout', signOut);
  route('POST', '/api/auth/logout-all', signOut);
  route('POST', '/api/auth/magic/request', (req) => {
    if (demo.personByEmail(req.body?.email)) {
      mail(req.body.email, 'Your sign-in link', '/auth/magic');
    }
    return { status: 202 };
  });
  route('POST', '/api/auth/reset/request', (req) => {
    if (demo.personByEmail(req.body?.email)) {
      mail(req.body.email, 'Reset your password', '/auth/reset');
    }
    return { status: 202 };
  });
  const tokenPerson = (req) => {
    const entry = demo.tokens.get(req.body?.token);
    return entry ? demo.personByEmail(entry.email) : null;
  };
  route('POST', '/api/auth/magic/preview', (req) => {
    const who = tokenPerson(req);
    return who ? json({ email: maskEmail(who.email) }) : problem(400, 'invalid_token');
  });
  route('POST', '/api/auth/magic/consume', (req) => {
    const who = tokenPerson(req);
    demo.tokens.delete(req.body?.token);
    return who ? signIn(who) : problem(400, 'invalid_token');
  });
  route('POST', '/api/auth/reset/consume', (req) => {
    const who = tokenPerson(req);
    demo.tokens.delete(req.body?.token);
    if (who) {
      who.emailVerified = true;
    }
    return who ? noContent : problem(400, 'invalid_token');
  });
  route('POST', '/api/auth/verify/consume', user((req, who) => {
    const owner = tokenPerson(req);
    if (!owner) {
      return problem(400, 'invalid_token');
    }
    if (owner !== who) {
      return problem(403, 'other_account');
    }
    who.emailVerified = true;
    demo.tokens.delete(req.body.token);
    return noContent;
  }));

  // ---- guests ---------------------------------------------------------------------------------------

  route('POST', '/api/guest', (req) => {
    const name = String(req.body?.displayName ?? '').trim();
    if (name === '' || name.length > 40 || name.includes('@')) {
      return problem(400, 'invalid');
    }
    const guest = { pid: newId(), kind: 'guest', name, hue: 120 };
    demo.guests.set(guest.pid, guest);
    return {
      status: 200,
      body: demo.meBody(guest),
      headers: { 'Set-Cookie': [cookie(GUEST_COOKIE, guest.pid, { maxAge: 30 * 24 * 3600 }), cookie(AS_COOKIE, SIGNED_OUT, { maxAge: 365 * 24 * 3600 })] },
    };
  });
  route('PATCH', '/api/guest', (req, who) => {
    if (who?.kind !== 'guest') {
      return unauthenticated();
    }
    who.name = String(req.body?.displayName ?? who.name).trim() || who.name;
    return json(demo.meBody(who));
  });

  // ---- me -------------------------------------------------------------------------------------------

  route('GET', '/api/me', (req, who) => (who ? json(demo.meBody(who)) : unauthenticated()));
  route('PATCH', '/api/me', user((req, who) => {
    const { fullName, nickname, theme } = req.body ?? {};
    if (typeof fullName === 'string' && fullName.trim()) {
      who.name = fullName.trim();
    }
    if (typeof nickname === 'string') {
      who.nickname = nickname.trim() || who.nickname;
    }
    if (['light', 'dark', 'system'].includes(theme)) {
      who.theme = theme;
    }
    return json(demo.meBody(who));
  }));
  route('PUT', '/api/me/avatar', user((req, who) => {
    const type = req.headers['content-type']?.split(';')[0];
    if (!IMAGE_TYPES.has(type)) {
      return problem(415, 'unsupported_media_type');
    }
    if (req.bytes.length > MAX_AVATAR_BYTES) {
      return problem(413, 'too_large');
    }
    const id = newId();
    demo.blobs.set(id, { type, bytes: req.bytes });
    who.avatar = id;
    return noContent;
  }));
  route('DELETE', '/api/me/avatar', user((req, who) => {
    who.avatar = undefined;
    return noContent;
  }));
  route('PUT', '/api/me/password', user(() => noContent));
  route('POST', '/api/me/identities/:provider/link', user(() => problem(409, 'provider_disabled', {
    detail: 'Google and Microsoft sign-in are not set up in the demo.',
  })));
  route('GET', '/api/me/sessions', user((req) => json([
    { id: '0d3e0000-0010-7000-8000-000000000001', method: 'password', createdAt: demo.now() - 3 * DAY, lastSeenAt: demo.now(), userAgent: browserName(req.headers['user-agent']), current: true },
    { id: '0d3e0000-0010-7000-8000-000000000002', method: 'magic', createdAt: demo.now() - 9 * DAY, lastSeenAt: demo.now() - 2 * DAY, userAgent: 'Safari on iPhone', current: false },
  ])));
  route('DELETE', '/api/me/sessions/:sid', user(() => noContent));
  route('GET', '/api/me/mentions', user((req, who) => json({ items: demo.mentionsOf(who), next: null })));
  route('POST', '/api/me/mentions/seen', user((req, who) => {
    demo.mentionsSeenAt.set(who.pid, demo.now());
    return noContent;
  }));
  route('GET', '/api/me/endorsements', user((req, who) => {
    const received = [];
    for (const team of demo.teams.values()) {
      for (const e of team.allEndorsements().filter((x) => x.to === who.pid)) {
        const type = team.endorsementTypes.find((t) => t.id === e.typeId);
        received.push({ emoji: type?.emoji, typeName: type?.name, fromName: demo.principal(e.from)?.name, teamName: team.name, createdAt: e.createdAt });
      }
    }
    return json(received.sort((a, b) => b.createdAt - a.createdAt));
  }));

  // ---- dashboard and teams --------------------------------------------------------------------------

  route('GET', '/api/dashboard', user((req, who) => {
    const mine = [...demo.teams.values()].filter((t) => !t.deleted && t.roleOf(who.pid));
    const lastAt = (team) => Math.max(team.createdAt, ...demo.boardsOf(team).map((b) => b.createdAt), ...demo.sessionsOf(team).map((s) => s.createdAt));
    const teamRow = (t) => ({ id: t.id, name: t.name, lastAt: lastAt(t) });
    const boards = [...demo.boards.values()]
      .filter((b) => !b.deleted && !b.meta.archived && (b.team ? b.team.roleOf(who.pid) : b.createdBy === who.pid))
      .sort((a, b) => b.createdAt - a.createdAt)
      .slice(0, 5)
      .map((b) => compact({ id: b.id, code: b.code, title: b.meta.title, teamName: b.team?.name, lastAt: b.createdAt }));
    const sessions = [...demo.sessions.values()]
      .filter((s) => !s.deleted && (s.team ? s.team.roleOf(who.pid) : s.createdBy === who.pid))
      .sort((a, b) => b.createdAt - a.createdAt)
      .slice(0, 5)
      .map((s) => compact({ id: s.id, code: s.code, teamId: s.team?.id, teamName: s.team?.name, lastAt: s.createdAt }));
    return json({
      teams: mine.map(teamRow).sort((a, b) => b.lastAt - a.lastAt),
      favorites: mine.filter((t) => t.favoriteOf.has(who.pid)).map(teamRow),
      boards,
      sessions,
    });
  }));

  route('GET', '/api/teams', user((req, who) => json([...demo.teams.values()]
    .filter((t) => !t.deleted && t.roleOf(who.pid))
    .map((t) => ({ id: t.id, name: t.name, description: t.description, role: t.roleOf(who.pid), favorite: t.favoriteOf.has(who.pid) })))));
  route('POST', '/api/teams', user((req, who) => {
    const name = String(req.body?.name ?? '').trim();
    if (!name) {
      return problem(400, 'validation', { fields: { name: 'required' } });
    }
    const team = demo.addRoom(new Team(demo, {
      id: newId(),
      name,
      description: req.body?.description ?? '',
      createdAt: demo.now(),
      members: [{ person: who, role: 'owner' }],
      favoriteOf: [],
      pointDescriptions: {},
      maxEndorsements: 3,
      jiraConfigured: false,
      endorsementTypes: [
        { id: newId(), name: 'Helping Hand', emoji: '🤝', archived: false },
        { id: newId(), name: 'Problem Solver', emoji: '🧠', archived: false },
        { id: newId(), name: 'Shipped It', emoji: '🚀', archived: false },
      ],
      pastEndorsements: [],
      templates: [{ id: newId(), name: 'Went well / To improve / Kudos', isDefault: true, columns: DEFAULT_COLUMNS, config: DEFAULT_CONFIG }],
      invites: [],
    }));
    return json({ id: team.id }, 201);
  }));
  route('PUT', '/api/teams/:id/favorite', member((req, who, team) => {
    team.favoriteOf.add(who.pid);
    return noContent;
  }));
  route('DELETE', '/api/teams/:id/favorite', member((req, who, team) => {
    team.favoriteOf.delete(who.pid);
    return noContent;
  }));

  route('GET', '/api/teams/:id', member((req, who, team) => json(team.info(who.pid))));
  route('PATCH', '/api/teams/:id', admin((req, who, team) => {
    const body = req.body ?? {};
    if (typeof body.name === 'string' && body.name.trim()) {
      team.name = body.name.trim();
    }
    if (typeof body.description === 'string') {
      team.description = body.description;
    }
    const settings = {};
    if (body.pointDescriptions && typeof body.pointDescriptions === 'object') {
      team.pointDescriptions = { ...body.pointDescriptions };
      settings.pointDescriptions = team.pointDescriptions;
      for (const session of demo.sessionsOf(team)) {
        session.tx([{ k: 'team.settings', d: { pointDescriptions: team.pointDescriptions } }]);
      }
    }
    if (Number.isInteger(body.maxEndorsementsPerBoard)) {
      team.maxEndorsements = body.maxEndorsementsPerBoard;
      settings.maxEndorsements = team.maxEndorsements;
      for (const board of demo.boardsOf(team)) {
        board.tx([(viewer) => (board.isMember(viewer) ? [{ k: 'team.settings', d: { maxEndorsements: team.maxEndorsements } }] : [])]);
      }
    }
    if (Object.keys(settings).length > 0) {
      team.tx([{ k: 'team.settings', d: settings }]);
    }
    return json(team.info(who.pid));
  }));
  route('DELETE', '/api/teams/:id', member((req, who, team) => {
    if (team.roleOf(who.pid) !== 'owner') {
      return forbidden();
    }
    if (team.id === DEMO_TEAM) {
      return problem(409, 'demo_protected', { detail: 'The demo keeps Demo Team. Create a team of your own to try deleting one.' });
    }
    if (req.body?.confirmName !== team.name) {
      return problem(400, 'validation', { fields: { confirmName: 'invalid' } });
    }
    team.deleted = true;
    demo.fake.kick(team.key, 'deleted');
    return noContent;
  }));

  route('GET', '/api/teams/:id/members', member((req, who, team) => json(team.memberInfos())));
  route('PATCH', '/api/teams/:id/members/:userId', member((req, who, team) => {
    const target = team.members.find((m) => m.pid === req.params.userId);
    if (team.roleOf(who.pid) !== 'owner') {
      return forbidden();
    }
    if (!target || target.role === 'owner' || !['admin', 'member'].includes(req.body?.role)) {
      return problem(400, 'invalid');
    }
    target.role = req.body.role;
    team.membersChanged();
    return noContent;
  }));
  route('DELETE', '/api/teams/:id/members/:userId', member((req, who, team) => {
    const target = team.members.find((m) => m.pid === req.params.userId);
    if (!target || target.role === 'owner') {
      return problem(400, 'invalid');
    }
    const self = target.pid === who.pid;
    const role = team.roleOf(who.pid);
    if (!self && !(role === 'owner' || (role === 'admin' && target.role === 'member'))) {
      return forbidden();
    }
    team.members = team.members.filter((m) => m !== target);
    team.membersChanged();
    for (const room of [team, ...demo.boardsOf(team), ...demo.sessionsOf(team)]) {
      for (const conn of [...room.room.subscribers].filter((c) => c.me?.pid === target.pid)) {
        conn.send({ t: 'kick', r: room.key, code: 'access_revoked' });
        demo.fake.unsubscribe(conn, room.room);
      }
    }
    return noContent;
  }));
  route('POST', '/api/teams/:id/transfer-ownership', member((req, who, team) => {
    const target = team.members.find((m) => m.pid === req.body?.userId);
    if (team.roleOf(who.pid) !== 'owner') {
      return forbidden();
    }
    if (!target || target.pid === who.pid) {
      return problem(400, 'invalid');
    }
    team.members.find((m) => m.pid === who.pid).role = 'admin';
    target.role = 'owner';
    team.membersChanged();
    return noContent;
  }));

  route('GET', '/api/teams/:id/invites', admin((req, who, team) => page(team.invites.map((i) => team.inviteInfo(i)))));
  route('POST', '/api/teams/:id/invites', admin((req, who, team) => {
    const body = req.body ?? {};
    let invite;
    if (body.kind === 'email') {
      if (!who.emailVerified) {
        return problem(403, 'email_unverified');
      }
      if (!String(body.email ?? '').includes('@')) {
        return problem(400, 'validation', { fields: { email: 'invalid' } });
      }
      invite = { id: newId(), kind: 'email', email: body.email.trim().toLowerCase(), role: body.role === 'admin' ? 'admin' : 'member', createdAt: demo.now(), expiresAt: demo.now() + 7 * DAY };
      demo.log(`  [mail] to ${invite.email}: You are invited to ${team.name}`);
    } else {
      const days = Math.min(30, Math.max(1, Number(body.expiresInDays) || 7));
      invite = { id: newId(), kind: 'link', role: 'member', token: newId(), createdAt: demo.now(), expiresAt: demo.now() + days * DAY };
    }
    team.invites.unshift(invite);
    return json(team.inviteInfo(invite), 201);
  }));
  route('DELETE', '/api/teams/:id/invites/:inviteId', admin((req, who, team) => {
    team.invites = team.invites.filter((i) => i.id !== req.params.inviteId);
    return noContent;
  }));
  const inviteFor = (token) => {
    for (const team of demo.teams.values()) {
      const invite = team.invites.find((i) => i.token === token);
      if (invite && !team.deleted) {
        return { team, invite };
      }
    }
    return null;
  };
  route('POST', '/api/invites/preview', (req) => {
    const found = inviteFor(req.body?.token);
    return found ? json({ teamName: found.team.name, kind: found.invite.kind, expired: found.invite.expiresAt < demo.now() }) : problem(404, 'not_found');
  });
  route('POST', '/api/invites/accept', user((req, who) => {
    const found = inviteFor(req.body?.token);
    if (!found) {
      return problem(404, 'revoked');
    }
    if (found.invite.expiresAt < demo.now()) {
      return problem(410, 'expired');
    }
    if (!found.team.roleOf(who.pid)) {
      found.team.members.push({ pid: who.pid, role: found.invite.role });
      found.team.membersChanged();
    }
    return json({ teamId: found.team.id });
  }));

  // ---- team lists -------------------------------------------------------------------------------------

  route('GET', '/api/teams/:id/boards', member((req, who, team) => page(team.boardList(req.query.get('archived') === 'true'))));
  route('POST', '/api/teams/:id/boards', member((req, who, team) => {
    const body = req.body ?? {};
    const template = team.templates.find((t) => t.id === body.templateId) ?? team.templates.find((t) => t.isDefault) ?? { columns: DEFAULT_COLUMNS, config: DEFAULT_CONFIG };
    const board = createBoard({ team, who, title: String(body.title ?? '').trim() || 'Retro', visibility: body.visibility ?? 'team', password: body.password, template });
    team.boardsChanged(board);
    return json({ id: board.id, code: board.code }, 201);
  }));
  route('GET', '/api/teams/:id/poker-sessions', member((req, who, team) => page(team.sessionList())));
  route('POST', '/api/teams/:id/poker-sessions', member((req, who, team) => {
    const latest = demo.sessionsOf(team).sort((a, b) => b.createdAt - a.createdAt)[0];
    const session = createSession({ team, who, presenceEnabled: latest?.presenceEnabled, followSpotlight: latest?.followSpotlight });
    team.pokerChanged(session);
    return json({ id: session.id, code: session.code }, 201);
  }));
  route('GET', '/api/teams/:id/action-items', member((req, who, team) => page(team.actionItemList(req.query))));
  route('GET', '/api/teams/:id/action-items/:itemId/comments', member((req, who, team) => {
    const item = team.item(req.params.itemId);
    if (!item) {
      return notFound();
    }
    const comments = item.board ? item.board.orderedComments.filter((c) => c.cardId === item.cardId) : [];
    return json({
      items: comments.map((c) => {
        const info = item.board.commentInfo(c, who.pid);
        const { cardId, ...rest } = info;
        return rest;
      }),
    });
  }));
  route('POST', '/api/teams/:id/action-items/:itemId/comments', member((req, who, team) => {
    const item = team.item(req.params.itemId);
    const board = item?.board;
    const card = board?.cards.get(item.cardId);
    const body = String(req.body?.body ?? '');
    if (!card) {
      return notFound();
    }
    if (body.trim() === '' || body.length > 8000) {
      return problem(400, 'invalid');
    }
    const comment = { id: req.body.commentId ?? newId(), cardId: card.id, body, anonymous: false, createdAt: demo.now(), author: who.pid };
    board.comments.set(comment.id, comment);
    board.tx([board.commentAdded(comment)]);
    team.actionCommentsChanged(card);
    return json({ commentId: comment.id, body }, 201);
  }));
  route('GET', '/api/teams/:id/endorsements/leaderboard', member((req, who, team) => json({ entries: team.leaderboard(req.query.get('period') ?? '30d') })));

  route('GET', '/api/teams/:id/endorsement-types', member((req, who, team) => page(team.endorsementTypes.map((t) => ({ ...t })))));
  route('POST', '/api/teams/:id/endorsement-types', admin((req, who, team) => {
    const { name, emoji, description } = req.body ?? {};
    if (!String(name ?? '').trim() || !String(emoji ?? '').trim()) {
      return problem(400, 'validation', { fields: { name: 'required' } });
    }
    const type = compact({ id: newId(), name: name.trim(), emoji: emoji.trim(), archived: false, description: description?.trim() || undefined });
    team.endorsementTypes.push(type);
    return json(type, 201);
  }));
  route('PATCH', '/api/teams/:id/endorsement-types/:typeId', admin((req, who, team) => {
    const type = team.endorsementTypes.find((t) => t.id === req.params.typeId);
    if (!type) {
      return notFound();
    }
    for (const field of ['name', 'emoji', 'description']) {
      if (typeof req.body?.[field] === 'string') {
        type[field] = req.body[field];
      }
    }
    return json({ ...type });
  }));
  route('DELETE', '/api/teams/:id/endorsement-types/:typeId', admin((req, who, team) => {
    const type = team.endorsementTypes.find((t) => t.id === req.params.typeId);
    if (type) {
      type.archived = true;
    }
    return noContent;
  }));

  route('GET', '/api/teams/:id/templates', member((req, who, team) => page(team.templates.map((t) => structuredClone(t)))));
  const saveTemplate = (team, template, body) => {
    if (body.isDefault) {
      team.templates.forEach((t) => {
        t.isDefault = false;
      });
    }
    Object.assign(template, structuredClone({ name: body.name, isDefault: Boolean(body.isDefault), columns: body.columns, config: body.config }));
    return json(structuredClone(template));
  };
  route('POST', '/api/teams/:id/templates', admin((req, who, team) => {
    const template = { id: newId() };
    team.templates.push(template);
    return saveTemplate(team, template, req.body ?? {});
  }));
  route('PUT', '/api/teams/:id/templates/:tid', admin((req, who, team) => {
    const template = team.templates.find((t) => t.id === req.params.tid);
    return template ? saveTemplate(team, template, req.body ?? {}) : notFound();
  }));
  route('DELETE', '/api/teams/:id/templates/:tid', admin((req, who, team) => {
    team.templates = team.templates.filter((t) => t.id !== req.params.tid);
    return noContent;
  }));

  route('GET', '/api/teams/:id/jira', member((req, who, team) => json(team.jiraSettings(who.pid))));
  route('PUT', '/api/teams/:id/jira', admin((req, who, team) => {
    const body = req.body ?? {};
    if (!team.jira.configured && !body.token) {
      return problem(400, 'validation', { fields: { token: 'required' }, detail: 'Paste an API token to connect Jira.' });
    }
    team.jira = compact({
      ...team.jira,
      configured: true,
      status: 'ok',
      siteHost: body.siteHost ?? team.jira.siteHost,
      projectKey: body.projectKey ?? team.jira.projectKey,
      boardId: body.boardId ?? team.jira.boardId,
      ticketPrefix: body.ticketPrefix ?? team.jira.ticketPrefix,
      membersCanEdit: body.membersCanEdit ?? team.jira.membersCanEdit,
    });
    return json(team.jiraSettings(who.pid));
  }));
  route('POST', '/api/teams/:id/jira/test', admin((req, who, team) => (team.jira.configured
    ? json({ ok: true, accountName: 'Demo Retail Jira bot', discovered: { pointsFieldId: 'customfield_10016', sprintFieldId: 'customfield_10020', boardId: team.jira.boardId } })
    : problem(409, 'jira_not_configured'))));
  route('DELETE', '/api/teams/:id/jira', admin((req, who, team) => {
    team.jira = { configured: false, status: 'ok', membersCanEdit: true };
    return noContent;
  }));

  // ---- poker ------------------------------------------------------------------------------------------

  function createSession({ team = null, who, presenceEnabled = true, followSpotlight = true }) {
    const firstRound = { id: newId(), number: 1, version: 1, state: 'voting', active: true, pending: false, autoReveal: true, votes: {} };
    return demo.addSession(new PokerSession(demo, {
      id: newId(),
      code: code(),
      team,
      createdBy: who.pid,
      createdAt: demo.now(),
      presenceEnabled,
      followSpotlight,
      rounds: [firstRound],
      currentRoundId: firstRound.id,
      roster: team ? [] : [who],
      peers: [],
    }));
  }

  route('POST', '/api/poker', (req, who) => {
    if (!who) {
      return unauthenticated();
    }
    const session = createSession({ who });
    return json({ id: session.id, code: session.code }, 201);
  });
  route('GET', '/api/poker/by-code/:code', (req, who) => {
    const session = demo.sessionByCode(req.params.code);
    if (!session || (session.team && !session.team.roleOf(who?.pid))) {
      return notFound();
    }
    return json({ id: session.id, access: 'granted' });
  });
  route('DELETE', '/api/poker/:id', user((req, who) => {
    const session = demo.sessions.get(req.params.id);
    if (!session || session.deleted) {
      return notFound();
    }
    if (session.team ? !session.team.isAdmin(who.pid) : session.createdBy !== who.pid) {
      return forbidden();
    }
    session.tx([{ k: 'poker.session.deleted', d: {} }]);
    session.deleted = true;
    demo.fake.kick(session.key, 'deleted');
    session.team?.pokerChanged(session);
    return noContent;
  }));

  // ---- retro ------------------------------------------------------------------------------------------

  function createBoard({ team = null, who, title, visibility, password, template }) {
    const columns = template.columns.map((c) => ({ id: newId(), title: c.title, color: c.color, actionItems: Boolean(c.actionItems) }));
    const board = demo.addBoard(new RetroBoard(demo, {
      id: newId(),
      code: code(),
      team,
      title,
      visibility,
      createdBy: who.pid,
      createdAt: demo.now(),
      config: { ...DEFAULT_CONFIG, ...template.config },
      columns,
      peers: [],
    }));
    board.password = password || undefined;
    return board;
  }

  route('POST', '/api/retro', (req, who) => {
    if (!who) {
      return unauthenticated();
    }
    const board = createBoard({ who, title: String(req.body?.title ?? '').trim() || 'Quick retro', visibility: 'link', template: { columns: DEFAULT_COLUMNS, config: { ...DEFAULT_CONFIG, stagesEnabled: false } } });
    return json({ id: board.id, code: board.code }, 201);
  });
  route('GET', '/api/retro/by-code/:code', (req, who) => {
    const board = demo.boardByCode(req.params.code);
    if (!board) {
      return notFound();
    }
    return json({ id: board.id, access: boardAccess(board, who) });
  });
  route('POST', '/api/retro/:id/unlock', (req, who) => {
    const board = demo.boards.get(req.params.id);
    if (!board || !who) {
      return notFound();
    }
    if (board.password !== req.body?.password) {
      return problem(403, 'wrong_password');
    }
    board.grants = new Set([...(board.grants ?? []), who.pid]);
    return noContent;
  });
  const manage = (handler) => user((req, who) => {
    const board = demo.boards.get(req.params.id);
    if (!board || board.deleted) {
      return notFound();
    }
    const allowed = board.team ? board.team.isAdmin(who.pid) || board.createdBy === who.pid : board.createdBy === who.pid;
    return allowed ? handler(req, who, board) : forbidden();
  });
  route('PATCH', '/api/retro/:id', manage((req, who, board) => {
    if (['link', 'team', 'password'].includes(req.body?.visibility)) {
      board.meta.visibility = req.body.visibility;
    }
    if (typeof req.body?.password === 'string') {
      board.password = req.body.password;
      board.grants = new Set();
    }
    board.tx([{ k: 'retro.board.meta', d: { ...board.meta } }]);
    return noContent;
  }));
  const setArchived = (archived) => manage((req, who, board) => {
    if (board.meta.archived !== archived) {
      const shownBefore = board.showsVoters();
      board.meta.archived = archived;
      board.archivedAt = archived ? demo.now() : undefined;
      board.tx([{ k: 'retro.board.meta', d: { ...board.meta } }, ...board.votesIfVisibilityChanged(shownBefore)]);
      board.team?.boardsChanged(board);
    }
    return noContent;
  });
  route('POST', '/api/retro/:id/archive', setArchived(true));
  route('POST', '/api/retro/:id/unarchive', setArchived(false));
  route('DELETE', '/api/retro/:id', manage((req, who, board) => {
    if ([CODES.thinking, CODES.voting, CODES.discussing, CODES.closed].includes(board.code)) {
      return problem(409, 'demo_protected', { detail: 'The demo keeps its four stage boards. Create a board to try deleting one.' });
    }
    board.deleted = true;
    demo.fake.kick(board.key, 'deleted');
    board.team?.boardsChanged(board);
    return noContent;
  }));
  route('POST', '/api/retro/:id/images', (req, who) => {
    const board = demo.boards.get(req.params.id);
    const type = req.headers['content-type']?.split(';')[0];
    if (!board || !who) {
      return notFound();
    }
    if (!IMAGE_TYPES.has(type)) {
      return problem(415, 'unsupported_media_type');
    }
    if (req.bytes.length > MAX_IMAGE_BYTES) {
      return problem(413, 'too_large');
    }
    const blobId = newId();
    demo.blobs.set(blobId, { type, bytes: req.bytes });
    return json({ blobId }, 201);
  });

  function boardAccess(board, who) {
    if (!board.team || board.team.roleOf(who?.pid)) {
      return board.meta.visibility === 'password' && board.createdBy !== who?.pid && !board.grants?.has(who?.pid) && !board.team ? 'password' : 'granted';
    }
    if (board.meta.visibility === 'team') {
      return who ? 'denied' : 'signin';
    }
    if (board.meta.visibility === 'password' && !board.grants?.has(who?.pid)) {
      return who ? 'password' : 'signin';
    }
    return 'granted';
  }

  // ---- admin ------------------------------------------------------------------------------------------

  route('GET', '/api/admin/users', appAdmin((req) => {
    const q = (req.query.get('q') ?? '').trim().toLowerCase();
    return json(demo.people()
      .filter((p) => !q || p.name.toLowerCase().includes(q) || p.email.startsWith(q))
      .map((p) => ({ id: p.pid, name: p.name, email: maskEmail(p.email), isAdmin: Boolean(p.isAdmin), disabled: Boolean(p.disabled) })));
  }));
  route('PATCH', '/api/admin/users/:uid', appAdmin((req) => {
    const target = demo.principal(req.params.uid);
    if (!target || target.kind !== 'user') {
      return notFound();
    }
    if (req.body?.isAdmin === false && demo.people().filter((p) => p.isAdmin).length === 1 && target.isAdmin) {
      return problem(409, 'last_admin', { detail: 'At least one admin must remain.' });
    }
    for (const field of ['disabled', 'isAdmin']) {
      if (typeof req.body?.[field] === 'boolean') {
        target[field] = req.body[field];
      }
    }
    return json({ id: target.pid, name: target.name, email: maskEmail(target.email), isAdmin: Boolean(target.isAdmin), disabled: Boolean(target.disabled) });
  }));
  route('GET', '/api/admin/teams', appAdmin((req) => {
    const q = (req.query.get('q') ?? '').trim().toLowerCase();
    return json([...demo.teams.values()]
      .filter((t) => !t.deleted && (!q || t.name.toLowerCase().includes(q)))
      .map((t) => ({ id: t.id, name: t.name, memberCount: t.members.length })));
  }));
  route('POST', '/api/admin/teams/:id/members', appAdmin((req) => {
    const team = demo.teams.get(req.params.id);
    const target = demo.principal(req.body?.userId) ?? personByKey(req.body?.userId);
    if (!team || !target || target.kind !== 'user') {
      return notFound();
    }
    const existing = team.members.find((m) => m.pid === target.pid);
    if (existing) {
      existing.role = ['admin', 'member'].includes(req.body?.role) ? req.body.role : existing.role;
    } else {
      team.members.push({ pid: target.pid, role: req.body?.role === 'admin' ? 'admin' : 'member' });
    }
    team.membersChanged();
    return noContent;
  }));
  route('DELETE', '/api/admin/teams/:id/members/:userId', appAdmin((req) => {
    const team = demo.teams.get(req.params.id);
    if (!team) {
      return notFound();
    }
    team.members = team.members.filter((m) => m.pid !== req.params.userId || m.role === 'owner');
    team.membersChanged();
    return noContent;
  }));
  route('GET', '/api/admin/stats', appAdmin(() => json({
    build: 'demo',
    uptime: `${Math.round(process.uptime() / 60)} min`,
    rssBytes: process.memoryUsage().rss,
    liveRooms: [...demo.rooms.values()].filter((r) => r.room.subscribers.size > 0).length,
    sockets: demo.fake.liveConnections().length,
    poolInUse: 2,
    poolSize: 20,
    ringBytes: [...demo.rooms.values()].reduce((sum, r) => sum + JSON.stringify(r.room.log.map((t) => t.s)).length * 40, 0),
    blobBytes: [...demo.blobs.values()].reduce((sum, b) => sum + b.bytes.length, 0),
    emailBudgetDrops: 0,
    outboxDepth: 0,
  })));
}

function browserName(agent = '') {
  const browser = /Firefox\//.test(agent) ? 'Firefox' : /Edg\//.test(agent) ? 'Edge' : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : 'A browser';
  const os = /Mac OS X/.test(agent) ? 'macOS' : /Windows/.test(agent) ? 'Windows' : /iPhone|iPad/.test(agent) ? 'iOS' : /Android/.test(agent) ? 'Android' : /Linux/.test(agent) ? 'Linux' : null;
  return os ? `${browser} on ${os}` : browser;
}
