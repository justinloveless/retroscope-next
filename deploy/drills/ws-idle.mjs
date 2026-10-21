// WebSocket idle check through the proxy (RUNBOOK "Measure on the target VPS"): a guest socket that sends nothing
// after hello, answering only the server's protocol PINGs (every 15 s), must stay open past the proxy's idle and read
// timeouts. Exits 0 when the socket is still open after --seconds (default 180), printing every PING it answered.
//   node deploy/drills/ws-idle.mjs [--seconds 180]       RS_E2E_BASE_URL=https://retro.example.com for the VPS
import crypto from 'node:crypto';
import { Socket, parseArgs, request, sleep } from './client.mjs';

const SECONDS = Number(parseArgs(process.argv.slice(2)).seconds ?? 180);
const ip = `10.${crypto.randomInt(256)}.${crypto.randomInt(256)}.${crypto.randomInt(1, 255)}`;
const guest = await request('POST', '/api/guest', { body: { displayName: 'Idle check' }, ip });
if (guest.status !== 200 && guest.status !== 201) throw new Error(`guest ${guest.status} ${guest.text}`);
const cookie = (guest.headers['set-cookie'] ?? []).map((c) => c.split(';')[0]).find((c) => c.startsWith('__Host-rs_guest='));

const started = Date.now();
let closedWith = null;
const pings = [];
const socket = await Socket.open({ cookie, ip }, {
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
process.exit(open ? 0 : 1);
