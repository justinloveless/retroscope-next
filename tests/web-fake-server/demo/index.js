// The demo scenario on top of the fake server (docs/followups/UP1.md): people and identity (`?as=<name>`
// and cookies), Demo Team with its boards, poker sessions and Jira, the HTTP API the screens read, and the
// simulated teammates. State lives in memory and is shared by every window connected to the server.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { installJira } from '../jira.js';
import { CODES, DEMO_TEAM, actionItems, boards, pokerSessions, teams } from './data.js';
import { CROWD, DEMO, GUESTS, PEOPLE, personByKey, principalInfo } from './people.js';
import { PokerSession } from './poker.js';
import { RetroBoard } from './retro.js';
import { installRoutes } from './routes.js';
import { Team } from './team.js';
import { AS_COOKIE, GUEST_COOKIE, SIGNED_OUT, cookie, parseCookies, seededRandom } from './util.js';

const here = dirname(fileURLToPath(import.meta.url));
export const DEMO_JIRA = join(here, 'jira.json');

const YEAR_SECONDS = 365 * 24 * 3600;

export { CODES, DEMO_TEAM };

export class Demo {
  /**
   * `peers: false` keeps the simulated teammates still (tests drive `tick()` themselves); `seed` fixes the
   * random source, `tickMs` the simulation step.
   */
  constructor(fake, { peers = true, seed = 20261021, tickMs = 1200, now = () => Date.now() } = {}) {
    this.fake = fake;
    this.random = seededRandom(seed);
    this.peersOn = peers;
    this.tickMs = tickMs;
    this.now = now;
    this.rooms = new Map();
    this.teams = new Map();
    this.boards = new Map();
    this.sessions = new Map();
    this.extraPeople = new Map();
    this.guests = new Map([...GUESTS, ...CROWD].map((g) => [g.pid, g]));
    this.blobs = new Map();
    this.tokens = new Map();
    this.mentionsSeenAt = new Map();
    this.problems = [];
    this.log = (line) => console.log(line);
  }

  install() {
    const start = this.now();
    this.jira = installJira(this.fake, { teamId: DEMO_TEAM, fixture: JSON.parse(readFileSync(DEMO_JIRA, 'utf8')) });
    for (const seed of teams(start)) {
      this.addRoom(new Team(this, seed));
    }
    for (const seed of boards(start)) {
      this.addBoard(new RetroBoard(this, { ...seed, team: this.teams.get(seed.team) ?? null }));
    }
    for (const seed of pokerSessions(start)) {
      this.addSession(new PokerSession(this, { ...seed, team: this.teams.get(seed.team) ?? null }));
    }
    this.seedActionItems(start);

    this.fake.boot = (path, req) => this.boot(path, req);
    this.fake.me = (req) => this.welcomeMe(req);
    this.fake.shellHeaders = (req, url) => this.shellHeaders(url);
    this.removeHandler = this.fake.onCommand('*', (ctx) => {
      const room = this.rooms.get(ctx.room.key);
      if (room) {
        room.handle(ctx);
      } else {
        ctx.ack();
      }
    });
    this.onSubscribe = (conn, room) => this.rooms.get(room.key)?.onSubscribe(conn);
    this.onUnsubscribe = (conn, room) => this.rooms.get(room.key)?.onUnsubscribe(conn);
    this.onError = (error) => this.problems.push(`fake server: ${error.message}`);
    this.onViolation = ({ errors }) => this.problems.push(`refused client frame: ${errors.join('; ')}`);
    this.fake.on('subscribe', this.onSubscribe);
    this.fake.on('unsubscribe', this.onUnsubscribe);
    this.fake.on('error', this.onError);
    this.fake.on('violation', this.onViolation);
    installRoutes(this);
    if (this.peersOn) {
      this.startPeers();
    }
    return this;
  }

  stop() {
    this.stopPeers();
    this.removeHandler?.();
    this.fake.off('subscribe', this.onSubscribe);
    this.fake.off('unsubscribe', this.onUnsubscribe);
    this.fake.off('error', this.onError);
    this.fake.off('violation', this.onViolation);
    for (const room of this.rooms.values()) {
      room.stop();
    }
  }

  startPeers() {
    this.stopPeers();
    this.peersOn = true;
    this.timer = setInterval(() => this.tick(), this.tickMs);
    this.timer.unref?.();
  }

  stopPeers() {
    this.peersOn = false;
    clearInterval(this.timer);
    this.timer = null;
  }

  /** One simulation step for every room with someone real in it. */
  tick() {
    for (const room of this.rooms.values()) {
      room.simTick?.(this.random);
    }
  }

  // ---- rooms ------------------------------------------------------------------------------------

  addRoom(room) {
    room.install();
    this.rooms.set(room.key, room);
    if (room instanceof Team) {
      this.teams.set(room.id, room);
    }
    return room;
  }

  addBoard(board) {
    this.boards.set(board.id, board);
    return this.addRoom(board);
  }

  addSession(session) {
    this.sessions.set(session.id, session);
    return this.addRoom(session);
  }

  boardsOf(team) {
    return [...this.boards.values()].filter((b) => b.team === team && !b.deleted);
  }

  sessionsOf(team) {
    return [...this.sessions.values()].filter((s) => s.team === team && !s.deleted);
  }

  boardByCode(code) {
    const wanted = String(code).toUpperCase();
    return [...this.boards.values()].find((b) => b.code === wanted && !b.deleted) ?? null;
  }

  sessionByCode(code) {
    const wanted = String(code).toUpperCase();
    return [...this.sessions.values()].find((s) => s.code === wanted && !s.deleted) ?? null;
  }

  seedActionItems(start) {
    const team = this.teams.get(DEMO_TEAM);
    for (const seed of actionItems(start)) {
      const board = this.boardByCode(seed.board);
      const index = Number(seed.card.split(':')[2]);
      const card = board.orderedCards.filter((c) => board.column(c.columnId)?.actionItems)[index];
      team.items.push({
        id: seed.id,
        text: seed.text,
        sourceVersion: card?.version ?? 1,
        createdAt: seed.createdAt,
        board,
        cardId: card?.id,
        assignedTo: seed.assignedTo,
        doneAt: seed.doneAt,
        doneBy: seed.doneBy,
        createdBy: card?.author,
      });
    }
  }

  jiraMeta(key) {
    const issue = this.jira.issues.get(key);
    if (!issue) {
      return null;
    }
    const parent = issue.parentKey ? this.jira.issues.get(issue.parentKey) : null;
    const meta = { key, summary: issue.summary, issueType: issue.issueType, status: issue.status };
    return parent ? { ...meta, parentKey: parent.key, parentSummary: parent.summary } : meta;
  }

  // ---- identity ---------------------------------------------------------------------------------

  principal(pid) {
    return PEOPLE.find((p) => p.pid === pid) ?? this.extraPeople.get(pid) ?? this.guests.get(pid) ?? null;
  }

  people() {
    return [...PEOPLE, ...this.extraPeople.values()];
  }

  personByEmail(email) {
    const wanted = String(email ?? '').trim().toLowerCase();
    return this.people().find((p) => p.email === wanted) ?? null;
  }

  infoOf(who) {
    return principalInfo(this.principal(who.pid) ?? who);
  }

  /** Who a request speaks for: `?as=` on a page load, then the cookies; Demo User by default. */
  identify(req, url = new URL(req.url ?? '/', 'http://demo')) {
    const cookies = parseCookies(req.headers?.cookie);
    const as = url.searchParams.get('as') ?? cookies[AS_COOKIE];
    if (as === SIGNED_OUT) {
      return this.guests.get(cookies[GUEST_COOKIE]) ?? null;
    }
    return personByKey(as) ?? this.extraPeople.get(as) ?? DEMO;
  }

  shellHeaders(url) {
    const as = url.searchParams.get('as');
    if (as === null) {
      return {};
    }
    const who = as === SIGNED_OUT ? SIGNED_OUT : personByKey(as)?.key;
    return who ? { 'Set-Cookie': cookie(AS_COOKIE, who, { maxAge: YEAR_SECONDS }) } : {};
  }

  meBody(who) {
    if (!who) {
      return null;
    }
    const principal = { id: who.pid, kind: who.kind, name: who.name, ...(who.avatar ? { avatar: who.avatar } : {}) };
    if (who.kind !== 'user') {
      return { principal, teams: [], unreadMentions: 0 };
    }
    return {
      principal,
      user: {
        email: who.email,
        emailVerified: who.emailVerified,
        fullName: who.name,
        nickname: who.nickname,
        theme: who.theme,
        isAdmin: who.isAdmin,
        hasPassword: true,
      },
      teams: [...this.teams.values()].filter((t) => !t.deleted && t.roleOf(who.pid)).map((t) => ({ id: t.id, name: t.name, role: t.roleOf(who.pid) })),
      unreadMentions: this.mentionsOf(who).filter((m) => m.createdAt > (this.mentionsSeenAt.get(who.pid) ?? 0)).length,
    };
  }

  boot(path, req) {
    const url = new URL(path, 'http://demo');
    const who = this.identify(req, url);
    return { build: 'demo', theme: who?.theme ?? 'system', me: this.meBody(who), path: url.pathname + url.search };
  }

  welcomeMe(req) {
    const who = this.identify(req) ?? { pid: '0d3e0000-0009-7000-8000-ffffffffffff', kind: 'guest', name: 'Visitor' };
    return principalInfo(who);
  }

  // ---- mentions ---------------------------------------------------------------------------------

  mentionsOf(who) {
    const marker = `[[mention:${who.pid}:`;
    const found = [];
    for (const board of this.boards.values()) {
      if (board.deleted || (board.team && !board.team.roleOf(who.pid))) {
        continue;
      }
      for (const [sourceKind, list] of [['card', board.orderedCards], ['comment', board.orderedComments]]) {
        for (const entity of list.filter((e) => e.body.includes(marker) && e.author !== who.pid)) {
          const author = entity.anonymous ? null : this.principal(entity.author);
          found.push({
            sourceKind,
            sourceId: entity.id,
            boardCode: board.code,
            boardTitle: board.meta.title,
            author: author ? { name: author.name, ...(author.avatar ? { avatar: author.avatar } : {}) } : null,
            body: entity.body,
            createdAt: entity.createdAt,
          });
        }
      }
    }
    return found.sort((a, b) => b.createdAt - a.createdAt);
  }

  publicOrigin() {
    return this.fake.origin;
  }
}

export function installDemo(fake, options) {
  return new Demo(fake, options).install();
}
