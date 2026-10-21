# web-fake-server

The server that front-end units (U28–U36) check against instead of the C# server (DESIGN §k.1). It needs
Node built-ins only and speaks the real wire protocol, checked frame by frame against
`web/src/net/protocol.gen.js`.

## Playwright specs (`tests/e2e/fake/**`)

Import `test` and `expect` from `tests/e2e/fake/_harness/fixtures.js`. Each test gets its own server as
the `fake` fixture, and `baseURL` points at it. Run specs with `./build.sh fake-e2e "<dir>/*.spec.js"`;
it builds `web/dist` first. The projects are `chromium`, `firefox`, `webkit` and `mobile-webkit`.

A test fails when the page reports a CSP violation, when a fake script throws, or when the client sends
a frame the protocol refuses. A spec that sends bad frames on purpose sets
`test.use({ allowProtocolViolations: true })`.

```js
import { expect, test } from '../_harness/fixtures.js';

test('vote', async ({ page, fake }) => {
  fake.boot = { build: 'fake', theme: 'light', me: { /* … */ } };
  fake.room('poker:<uuid>', { snapshot: {/* PokerSnapshot */}, overlay: { own: [], heldByThisTab: false } });
  fake.onCommand('poker.vote.set', (ctx) => ctx.commit([{ k: 'poker.vote.state', d: { /* … */ } }]));
  await page.goto('/poker/ABCDEFGHJK');
  // …
});
```

## Node tests

```js
import { startFakeServer } from '../web-fake-server/server.js';
import { TestClient } from '../web-fake-server/client.js';
const fake = await startFakeServer({ dist });   // dist defaults to web/dist
const client = await TestClient.connect(fake);   // a protocol client for node --test
```

## API

| Member | What it does |
|---|---|
| `fake.url`, `fake.origin`, `fake.wsUrl`, `fake.host` | Where it listens (127.0.0.1, free port) |
| `fake.boot` | Boot JSON object, or `(pathAndQuery, req) => object`. Used for the shell's `#boot` and for `GET /api/boot?path=` |
| `fake.route(method, pattern, response)` | HTTP fixture. `pattern` takes `:name` segments and a trailing `*`. `response` is `{status, body, headers, delayMs}` or `(req) => response` with `req.params`, `req.query`, `req.body` (parsed JSON), `req.headers`. Later routes win. Returns a remover |
| `json(body, status)`, `problem(status, code, extra)` | Response helpers from `http.js`. Error bodies with a `code` go out as `application/problem+json` |
| `fake.requests` | Every HTTP request: `{method, path, query, headers, text, json}` |
| `fake.jira({teamId, fixture?, configured?})` | Installs the fake Jira proxy under `/api/teams/{teamId}/jira/...` (see `jira.js` and `fixtures/jira.json`). Returns `{issues, config, calls, failNext(problem, count), touch(key), uninstall()}` |
| `fake.me` | The `welcome.me`, or `(req) => me` |
| `fake.clockOffsetMs` | Added to the `welcome.n` and `pong.n` server clock (clock-skew tests) |
| `fake.room(key, {snapshot, overlay, epoch, seq})` | Creates or updates a room. `snapshot` and `overlay` may be functions of the connection (per-viewer projections) |
| `fake.onCommand(kind or '*', handler)` | `handler(ctx)` gets `{frame, conn, room, commit(events), ack(), nack(code, {msg, d})}`. `commit` sends the tx, then the ack. A handler that answers nothing leaves the command unanswered. Without a handler, commands are acked at the current seq (NoOp). Resent `q`s at or below the cid's cursor get `ack {dup:true}` automatically, or `nack cid_reuse` when the payload differs from the one recorded for that `(cid, q)` (DESIGN §c.6) |
| `fake.seedCommands(room, cid, [{q, k, d}])` | Records commands as if another tab with the same cid had sent them and had them acked. The cid's cursor (and so `lastQ`) moves to the highest `q`, and a resend of one of those `q`s with a different payload gets `nack cid_reuse`. `fake.bye({newEpoch:true})` forgets the recorded payloads, as a restart empties the server's LRU |
| `fake.tx(room, events)` | Appends and fans out one tx. `events` is a list of `{k, d}` or `(conn) => list` |
| `fake.gap(room, n)` | Advances the seq silently, so the next tx arrives as a gap. A resubscribe replays the skipped entries |
| `fake.x(room, k, d)`, `fake.kick(room, code)` | Unsequenced cache fill, and room kick |
| `fake.bye({retryMs, newEpoch})` | `bye` and close 1012 on every socket. `newEpoch` makes resubscribes get snapshots |
| `fake.closeAll(code)`, `conn.close(code)`, `conn.sendRaw(text)` | Arbitrary close codes (4008, 4401, …) and deliberately invalid frames |
| `fake.rejectNextHello(code)` | The next hello is closed with `code`, e.g. 4409. A cid that is already live is refused with 4409 anyway |
| `fake.replayTranscript(pathOrObject, {step})` | Replays a golden transcript to whoever subscribes to its room. With `step:true`, `player.next(n)` and `player.rest()` release the frames |
| `fake.connections`, `fake.received`, `fake.violations` | Connections; every client frame `{conn, frame}`; client frames the protocol refused |
| `fake.waitForConnection()`, `fake.waitForFrame(pred, {after, timeout})` | Waits for a hello'd connection or a matching client frame |

`sub` answers the way the real server does: the current `{e, s}` gets an empty replay, an older `s` in the
same epoch gets the missed txs, and anything else gets a snapshot. `lastQ` is the highest acked `q` of
that cid. An unknown room gets `kick not_found`. A cmd without a sub gets `nack not_subscribed`, and a
cmd with a valid envelope but a bad kind or payload gets `nack invalid`. Other protocol violations close
with 4400. A missing hello closes with 4400 after 5 s, a frame over 64 KiB with 4413 and a foreign
Origin with 4403.

## Browser notes

- **WebKit reports close code 1012 as 1005.** When the fake closes with 1012 after `bye` (`fake.bye()`),
  desktop and mobile WebKit give `CloseEvent.code === 1005`, while Chromium and Firefox give 1012. Codes
  in the 4000 range (4409, 4401, …) come through unchanged everywhere. Front-end code (U28) must act on
  the `bye` frame and treat 1005 like 1012, never on the close code alone. `_harness/ws.spec.js` pins
  this behaviour.
- **Firefox does not emulate `prefers-color-scheme: dark`** under Playwright: `colorScheme: 'dark'`
  leaves `matchMedia` false, so theme specs skip that one combination.
