# UF7 decisions: server runtime hardening

Items are numbered as in `docs/followups/UF7.md`. The per-area decisions are appended to the owning units' records:
U13 (items 1–5), U14 (item 6), U20 (item 9) and U16 (item 10). This file records the calls UF7 made where the follow-up
left room, and items 7 and 8, whose owners (U11's `SecurityHeaders`, U0's `build.sh`) have no amendment section here.

1. **What "a clean not-found result" is.** A query on a missing or deleted room gets a `RoomView` with `Found` false
   and delegates that never touch actor state; `SnapFrameAsync` on it keeps throwing `InvalidOperationException`, as
   the existing contract says (callers check `Found` first). Changing it to return null would have changed the public
   signature U25 builds on.
2. **The wait bound for a purged key** is `ActorOptions.IncarnationWait`, 5 s by default. A retention purge deletes
   one room's rows; anything slower is better answered `server_busy` and retried than held on a socket's receive loop.
3. **HSTS (item 7)** is sent on every response, not only HTML, when `PUBLIC_URL` is https, and on none when it is http.
   Every response already carried the full §e header set, and the existing test asserts that for non-HTML paths, so
   restricting HSTS to HTML would only have added a content-type branch. Over plain http browsers ignore the header,
   and a local http deployment must not send it.
4. **`aot-smoke` (item 8)** runs a small Node client (inline in `build.sh`, no new file or dependency; Node is already
   required by `build.sh` and installed in CI) against the native server binary: signup and login through the real
   routes (`X-RS: 1` and `Origin`, cookie read from `Set-Cookie`), then a raw-socket WebSocket so that control frames
   are visible: `hello` → `welcome` as the signed-up user, a client PING answered by a server PONG, a server
   keep-alive PING (the production 15 s interval, so this step waits up to 25 s) answered with a PONG, and an app
   `ping` → `pong` on the same socket. The server now gets a throwaway `APP_KEYS`, which signup and login need.
5. **Signup audit rows** (item 10) are new: they are what makes both outcomes commit a write. See U16's amendment.

## Verification

See the unit report for the Check and full-suite results. Each new test was also run against the production code it
covers with the fix reverted. Actors: both purge-refusal tests, the access recheck and the poisoned view failed, and
the purge-wait test hung until the run was killed. Transport (three runs): the hello race failed in two, the
close-after-end timer test in three, the end-of-connection timer test in one. AuthCore: the database-command parity
test failed (the taken path ran two commands); the timing test passed against the old code in that run, since bcrypt
dominates both paths. Blobs: the read-limit tests cover a limit that was already right; they first failed only because
the buckets refill during the 600 requests, which the frozen clock fixes. `./build.sh aot-smoke` printed every step of
the `/ws` smoke on osx-arm64.
