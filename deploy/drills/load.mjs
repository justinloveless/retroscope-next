// Throughput and command latency against deploy/compose.test.yml (recorded, not a gate; DESIGN §j). The default
// shape is the soak's steady load: 20 rooms × 8 players × 0.3 commands/s, plus a burst every 30 s in which every
// player votes within one second. Prints acks/s, ack latency percentiles and the app's and db's peak memory and CPU.
//   node deploy/drills/load.mjs [--rooms 20] [--players 8] [--rate 0.3] [--seconds 60]
import { execFileSync } from 'node:child_process';
import { parseArgs, percentile, room, seedAccounts, sleep, team, user } from './client.mjs';
import { Tab } from './tab.mjs';

const args = parseArgs(process.argv.slice(2));
const ROOMS = Number(args.rooms ?? 20);
const PLAYERS = Number(args.players ?? 8);
const RATE = Number(args.rate ?? 0.3);
const SECONDS = Number(args.seconds ?? 60);
const BURST_EVERY_S = 30;
const POINTS = [1, 2, 3, 5, 8, 13, 21];

const log = (message) => console.log(`[${new Date().toISOString().slice(11, 23)}] ${message}`);

function stats() {
  const lines = execFileSync('docker', ['stats', '--no-stream', '--format', '{{.Name}} {{.CPUPerc}} {{.MemUsage}}',
    'retroscope-test-app-1', 'retroscope-test-db-1']).toString().trim().split('\n');
  return Object.fromEntries(lines.map((line) => {
    const [name, cpu, mem] = line.split(' ');
    const mib = mem.endsWith('GiB') ? parseFloat(mem) * 1024 : mem.endsWith('KiB') ? parseFloat(mem) / 1024 : parseFloat(mem);
    return [name.replace('retroscope-test-', '').replace('-1', ''), { cpu: parseFloat(cpu), mib }];
  }));
}

// Every room is a session of a team (UX18): its first player owns the team, seeded verified through the database;
// the others sign up through the route and join through the team's link invite.
log(`seeding ${ROOMS} team owners and signing up ${ROOMS * (PLAYERS - 1)} players`);
const owners = await seedAccounts(Array.from({ length: ROOMS }, (_, r) => `Load ${r + 1}.1`));
const tabs = [];
for (let r = 0; r < ROOMS; r++) {
  const others = await Promise.all(Array.from({ length: PLAYERS - 1 }, (_, p) => user(`Load ${r + 1}.${p + 2}`)));
  const { id: teamId } = await team(owners[r], `Load ${r + 1}`, others);
  const { id } = await room(owners[r], teamId, 'poker');
  tabs.push(...[owners[r], ...others].map((u) => Object.assign(new Tab(u, `poker:${id}`), { roomIndex: r })));
}
await Promise.all(tabs.map((tab) => tab.connect()));
const rounds = new Map(tabs.map((tab) => [tab.room, tab.snapshot.currentRoundId ?? tab.snapshot.rounds[0].id]));
log(`${tabs.length} sockets in ${ROOMS} rooms; running ${SECONDS} s`);

const peak = { app: { cpu: 0, mib: 0 }, db: { cpu: 0, mib: 0 } };
let running = true;
const sampler = (async () => {
  while (running) {
    for (const [name, value] of Object.entries(stats())) {
      peak[name].cpu = Math.max(peak[name].cpu, value.cpu);
      peak[name].mib = Math.max(peak[name].mib, value.mib);
    }
    await sleep(1000);
  }
})();

const started = Date.now();
const vote = (tab, i) => tab.command('poker.vote.set', { roundId: rounds.get(tab.room), points: POINTS[i % POINTS.length] });
const players = tabs.map(async (tab, n) => {
  let i = n;
  let nextBurst = started + BURST_EVERY_S * 1000;
  while (Date.now() - started < SECONDS * 1000) {
    if (Date.now() >= nextBurst) {
      await sleep(Math.random() * 1000);
      nextBurst += BURST_EVERY_S * 1000;
    } else {
      await sleep(-Math.log(1 - Math.random()) * (1000 / RATE));
    }
    if (tab.connected) vote(tab, i++);
  }
});
await Promise.all(players);
const elapsed = (Date.now() - started) / 1000;
const unacked = (await Promise.all(tabs.map((tab) => tab.drain(10_000)))).reduce((a, b) => a + b, 0);
running = false;
await sampler;
tabs.forEach((tab) => tab.close());

const latencies = tabs.flatMap((tab) => tab.latencies);
const report = {
  sockets: tabs.length,
  seconds: Math.round(elapsed),
  commandsAcked: latencies.length,
  acksPerSecond: Number((latencies.length / elapsed).toFixed(1)),
  ackLatencyMs: { p50: percentile(latencies, 50), p95: percentile(latencies, 95), p99: percentile(latencies, 99), max: Math.max(...latencies) },
  nacks: tabs.flatMap((tab) => tab.nacks).length,
  unacked,
  reconnects: tabs.flatMap((tab) => tab.reconnects).length,
  peak,
};
console.log(JSON.stringify(report, null, 2));
process.exit(unacked === 0 ? 0 : 1);
