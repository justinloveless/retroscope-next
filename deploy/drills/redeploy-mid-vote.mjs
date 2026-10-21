// Redeploy-mid-vote drill (DESIGN §j, U43): six players vote and add rounds in one poker session while the app
// container is stopped and recreated, as Coolify redeploys a compose app. Passes when no acked command is lost,
// nothing is applied twice, and every tab is back in the room within 15 s.
//   docker compose -f deploy/compose.test.yml up -d --build --wait
//   node deploy/drills/redeploy-mid-vote.mjs [--players 6] [--before 8] [--after 10]
import { execFileSync, spawn } from 'node:child_process';
import crypto from 'node:crypto';
import { parseArgs, request, sleep, user } from './client.mjs';
import { Tab } from './tab.mjs';

const args = parseArgs(process.argv.slice(2));
const PLAYERS = Number(args.players ?? 6);
const BEFORE_S = Number(args.before ?? 8);
const AFTER_S = Number(args.after ?? 10);
const RECONNECT_BUDGET_MS = 15_000;
const POINTS = [1, 2, 3, 5, 8, 13, 21];
const COMPOSE = ['compose', '-f', new URL('../compose.test.yml', import.meta.url).pathname];

const log = (message) => console.log(`[${new Date().toISOString().slice(11, 23)}] ${message}`);

function psql(sql) {
  return execFileSync('docker', [...COMPOSE, 'exec', '-T', 'db', 'psql', '-U', 'retroscope_owner', '-d', 'retroscope', '-Atc', sql])
    .toString().trim().split('\n').filter(Boolean);
}

function redeploy() {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const child = spawn('docker', [...COMPOSE, 'up', '-d', '--no-deps', '--force-recreate', '--wait', 'app'], { stdio: 'ignore' });
    child.on('exit', (code) => (code === 0 ? resolve(Date.now() - started) : reject(new Error(`redeploy exited ${code}`))));
  });
}

const users = [];
for (let i = 0; i < PLAYERS; i++) users.push(await user(`Drill ${i + 1}`));
const created = await request('POST', '/api/poker', { body: {}, cookie: users[0].cookie, ip: users[0].ip });
if (created.status !== 201 && created.status !== 200) throw new Error(`create ${created.status} ${created.text}`);
const { id, code } = JSON.parse(created.text);
for (const u of users.slice(1)) {
  const lookup = await request('GET', `/api/poker/by-code/${code}`, { cookie: u.cookie, ip: u.ip });
  if (lookup.status !== 200) throw new Error(`by-code ${lookup.status}`);
}
const room = `poker:${id}`;
const tabs = users.map((u) => new Tab(u, room));
await Promise.all(tabs.map((tab) => tab.connect()));
const roundId = tabs[0].snapshot.currentRoundId ?? tabs[0].snapshot.rounds[0].id;
log(`${PLAYERS} players in session ${code}`);

let running = true;
const loops = tabs.map(async (tab, n) => {
  let i = 0;
  while (running) {
    if (tab.connected) {
      if (i % 5 === 4) {
        tab.command('poker.round.create', { roundId: crypto.randomUUID(), ticketKey: `D${n + 1}-${i + 1}` });
      } else {
        tab.lastPoints = POINTS[(i + n) % POINTS.length];
        tab.command('poker.vote.set', { roundId, points: tab.lastPoints });
      }
      i++;
    }
    await sleep(200);
  }
});

await sleep(BEFORE_S * 1000);
log('redeploying app (stop and recreate) mid-vote');
const redeployMs = await redeploy();
log(`app healthy again after ${redeployMs} ms`);
await sleep(AFTER_S * 1000);
running = false;
await Promise.all(loops);
const pending = (await Promise.all(tabs.map((tab) => tab.drain(20_000)))).reduce((a, b) => a + b, 0);
tabs.forEach((tab) => tab.close());

const applied = tabs.flatMap((tab) => tab.applied);
const ackedRounds = applied.filter((x) => x.frame.k === 'poker.round.create').map((x) => x.frame.d.roundId);
const rows = new Set(psql(`select id from poker_rounds where session_id = '${id}'`));
const lostRounds = ackedRounds.filter((r) => !rows.has(r));
const duplicates = psql(`select ticket_key from poker_rounds where session_id = '${id}' and ticket_key is not null group by ticket_key having count(*) > 1`);
const votes = new Map(psql(`select principal_id || ' ' || points from poker_votes where round_id = '${roundId}'`).map((x) => x.split(' ')));
const wrongVotes = tabs.filter((tab) => tab.lastPoints !== undefined && Number(votes.get(tab.me.pid)) !== tab.lastPoints);
const reconnects = tabs.flatMap((tab) => tab.reconnects);
const slowest = Math.max(0, ...reconnects.map((x) => x.ms));
const nacks = tabs.flatMap((tab) => tab.nacks);

const report = {
  players: PLAYERS,
  commandsApplied: applied.length,
  appliedVia: Object.fromEntries(['ack', 'dup', 'cursor'].map((v) => [v, applied.filter((x) => x.via === v).length])),
  roundsAcked: ackedRounds.length,
  lostAckedRounds: lostRounds.length,
  duplicateRounds: duplicates.length,
  wrongFinalVotes: wrongVotes.length,
  stillUnacked: pending,
  nacks,
  redeployMs,
  reconnects: reconnects.length,
  closeCodes: [...new Set(reconnects.map((x) => x.code))],
  slowestReconnectMs: slowest,
};
console.log(JSON.stringify(report, null, 2));
const ok = lostRounds.length === 0 && duplicates.length === 0 && wrongVotes.length === 0 && pending === 0
  && nacks.length === 0 && reconnects.length >= PLAYERS && slowest < RECONNECT_BUDGET_MS;
log(ok ? 'redeploy drill passed' : 'redeploy drill FAILED');
process.exit(ok ? 0 : 1);
