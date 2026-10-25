// WebSocket idle check through the proxy (RUNBOOK "Measure on the target VPS"): a signed-in socket that sends nothing
// after hello, answering only the server's protocol PINGs (every 15 s), must stay open past the proxy's idle and read
// timeouts. Exits 0 when the socket is still open after --seconds (default 180), printing every PING it answered.
// Only signed-in users open sockets (UX18), and the drill never signs up, which would leave an account behind on every
// run and spend the per-address sign-up limit. Against another stack, such as production, it signs in as an account
// made beforehand for this check (any account will do: it needs no team and no verified address), and signs that
// session out at the end:
//   RS_IDLE_EMAIL      the account's email address
//   RS_IDLE_PASSWORD   its password
// Without them, against compose.test.yml on localhost, it seeds an account through the stack's database instead.
//   node deploy/drills/ws-idle.mjs [--seconds 180]
//   RS_E2E_BASE_URL=https://retro.example.com RS_IDLE_EMAIL=… RS_IDLE_PASSWORD=… node deploy/drills/ws-idle.mjs --seconds 300
import crypto from 'node:crypto';
import { BASE, Socket, parseArgs, request, seedAccounts, signIn, sleep } from './client.mjs';

const SECONDS = Number(parseArgs(process.argv.slice(2)).seconds ?? 180);
const { RS_IDLE_EMAIL: email, RS_IDLE_PASSWORD: password } = process.env;

let account;
if (email && password) {
  account = await signIn({ email, name: 'Idle check' }, password);
} else if (['localhost', '127.0.0.1'].includes(BASE.hostname)) {
  [account] = await seedAccounts(['Idle check']);
} else {
  console.error(`ws-idle: set RS_IDLE_EMAIL and RS_IDLE_PASSWORD to an account on ${BASE.origin}; this drill does not sign up`);
  process.exit(2);
}
if (!account.cookie) {
  throw new Error('signing in set no session cookie');
}

const started = Date.now();
let closedWith = null;
const pings = [];
const socket = await Socket.open(account, {
  onFrame: () => {},
  onClose: (code) => { closedWith = { code, afterS: Math.round((Date.now() - started) / 1000) }; },
});
const write = socket.write.bind(socket);
socket.write = (opcode, payload) => {
  if (opcode === 0xa) pings.push(Math.round((Date.now() - started) / 1000));
  write(opcode, payload);
};
socket.send({ t: 'hello', v: 1, cid: crypto.randomUUID(), subs: [] });

while (closedWith === null && Date.now() - started < SECONDS * 1000) await sleep(1000);
const open = closedWith === null;
console.log(JSON.stringify({ seconds: SECONDS, closed: closedWith, pingsAnsweredAtS: pings }));
socket.finish(1000);
const signedOut = await request('POST', '/api/auth/logout', { cookie: account.cookie, ip: account.ip });
if (signedOut.status >= 300) {
  console.error(`ws-idle: signing out answered ${signedOut.status}`);
}
process.exit(open ? 0 : 1);
