# UP1 decisions: local preview

UP1 owns `tests/web-fake-server/demo/**`, the demo hooks in `tests/web-fake-server/server.js`, the `preview` and
`screenshots` verbs in `build.sh`, `tests/e2e/screenshots/**`, `deploy/compose.preview.yml`, `docs/PREVIEW.md`
and this file.

## Hooks added to the fake server

These are the smallest changes that let a demo run on the fake server. Existing behaviour is unchanged when they
are not used, and `self.test.js` passes as before.

1. **`aliases`** (constructor option). Other host names for the same port, such as `localhost` next to
   `127.0.0.1`. A request whose `Host` is an alias is checked against that origin: the CSRF `Origin` check, the
   WebSocket `Origin` check and the CSP `connect-src`. The two names keep separate cookie jars, so one browser can
   be two people.
2. **`shellHeaders(req, url)`**. Extra headers on shell pages. The demo uses it to set its identity cookie from
   `?as=` on the same response that renders the boot JSON.
3. **`bytes`** on the request record. The raw body, so image and avatar uploads round-trip. `text` decodes the
   body as UTF-8, which loses binary data.
4. **`subscribe` / `unsubscribe` events** (conn, room). `subscribe` fires after the connection's first snap or
   replay for the room. A presence join sent from the handler therefore arrives after the snapshot, never before
   it.

## Decisions where the spec was silent or did not fit

1. **Identity is a cookie, chosen by `?as=`.** The WebSocket upgrade carries cookies but no query, so `?as=`
   alone cannot tell the socket who is speaking. The shell response sets `rs_demo_as=<key>`, and the boot JSON,
   `/api/*` and the socket all read it. Without a cookie the visitor is Demo User. `?as=signed-out` (or Sign out)
   gives a signed-out visitor. A guest made through `POST /api/guest` gets a second cookie.

   Two windows of one browser share cookies. So the second person opens the other host name (`127.0.0.1` next to
   `localhost`), which hook 1 accepts. A private window works too.
2. **Simulated teammates act only while someone real is in the room**, and never for a person a real window is
   playing. With nobody watching, nothing burns through the ticket queue. With Priya open in a second window, the
   demo does not vote for her. Peers run on a 1.2 s tick from a seeded random source. Tests turn the timer off and
   call `demo.tick()` themselves.
3. **The facilitator's loop (poker).** After a reveal, Priya previews the next queued ticket as a pending round
   (spotlight followers see it). She then ends the revealed round and commits the preview, which every client
   follows (`poker.pointer` with `nav: true`). This uses only §c.4 commands, so the client sees exactly what the
   real server would send. Pending rounds move the pointer with `nav: false`, as §c.4 says. The peers keep voting
   on the last active, non-pending round.
4. **Presence grace is 3 s**, not the server's 10 s, so closing a demo window is visible while you watch.
5. **Response shapes** follow `docs/decisions/U29.md` and `U30.md`. Where those are silent:
   - `/api/me/endorsements` items also carry `createdAt`.
   - Admin stats are a flat object.
   - Board and session lists are a single page (`nextCursor: null`).

   Mentions are computed live from board cards and comments that contain `[[mention:<pid>:…]]`.
6. **What the demo refuses.** Deleting Demo Team, or one of its four stage boards, answers `409 demo_protected`
   with a sentence the screens show. Linking Google or Microsoft answers `409 provider_disabled`. The OIDC start
   links go to `/login?error=provider_disabled`. A demo is easy to break otherwise, and every other action works.
7. **Sign-in in the demo** accepts any password for a known email. Mails are printed to the demo's terminal with
   the link, rather than sent.
8. **Fixtures.** The Jira fixture is `demo/jira.json` (project `DEMO`, sprints "Sprint 41 · Checkout polish" and
   "Sprint 42 · Onboarding"). It is served by the existing `jira.js` proxy for Demo Team. Platform Guild has no
   Jira, so its proxy routes answer `jira_not_configured`. Avatars are pixel identicons and the burndown image is
   a hand-drawn chart, all SVG from `/blob/{id}`.
9. **Screenshots run on Chromium only**, iPhone 13 included: Chromium emulates the device, so only
   `npx playwright install chromium` is needed. They are captured with `reducedMotion: 'reduce'` and with the
   peers paused (`--quiet-peers`), so each shot shows the seeded state. `./build.sh screenshots` first runs
   `smoke.spec.js` against a second demo with the peers on. That run covers the two-context realtime check, the
   peers locking in, and every route opening without a console error or a demo-side protocol violation.
10. **Key states without a UI yet.** U32, U33, U35 and U36 have not landed, so some panels do not exist yet: the
    round strip and spotlight controls, the Jira browse panel and issue drawer, the stage bar, the timer, focus,
    previous action items, and the kudos celebration. The demo already serves their data. The capture records
    each retro stage, the running timer (`retro-thinking-timer-running`) and the celebration event
    (`retro-kudos-celebration`, after `POST /__demo/celebrate`), and these shots fill in once the units merge.
    The Jira browse panel and issue drawer have no shot yet. Their data is checked in `demo.test.js`, and the Jira
    connection shows in `team-settings-jira`. Whoever lands U33 should add a `poker-jira-panel` and a
    `poker-jira-drawer` state to `capture.spec.js`.
11. **Real mode** runs `dotnet run` (JIT) on the host and only Postgres and Mailpit in Docker
    (`deploy/compose.preview.yml`). It seeds the account through the real signup route, reads the verification
    mail from Mailpit's API (`/api/v1/search`, `/api/v1/message/{id}`), signs in, and posts `verify/consume` with
    that session. `APP_KEYS` and `ANON_TAG_KEY` are new random keys on every run, and Ctrl-C removes the volume,
    so nothing carries over.
12. **Ports.** Both modes refuse a taken port up front and name the variable to change (`PREVIEW_PORT`,
    `PREVIEW_PG_PORT`, `PREVIEW_SMTP_PORT`, `PREVIEW_MAILPIT_PORT`). On the machine this was built on, 5173, 8080,
    8025 and 1025 were already in use by other projects, so the checks were run on other ports (below).

## Pinned images (checked against the registries on 2026-10-21)

| Image | Pin | Source |
|---|---|---|
| `postgres:18-alpine` | `sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` | same as `build.sh` (U0) |
| `axllent/mailpit:v1.31.6` | `sha256:23fe39e81bde596a918000b3135c0f85dc8bb4c9ea6be274b051143f3e300387` | Docker Hub tags API and `docker buildx imagetools inspect` |

## Verification

- `node --test tests/web-fake-server/demo/demo.test.js` passes 21 of 21. It covers the hooks, identity, vote
  secrecy, auto-reveal, peers, retro projections and gates, action items, and the HTTP shapes.
- `PREVIEW_PORT=5199 ./build.sh preview` in the background. `curl` gets 200 on `/`,
  `/teams/0d3e0000-0001-7000-8000-000000000001`, `/teams/…/poker/PKR41CHKT1`, `/poker/QCKPKR2026` and
  `/retro/SPR42THNK1`. `smoke.spec.js` against it passes. Its two-context test sees Demo User's lock as
  `data-status="locked"` in Priya's window, which is on the other host name.
- `PREVIEW_PORT=18731 PREVIEW_SMTP_PORT=11025 PREVIEW_MAILPIT_PORT=18025 ./build.sh preview real`: the stack came
  up and the verification mail landed in Mailpit. Signup, login and verify passed, and a Chromium sign-in through
  the login page reached `/dashboard` (whose API is U24's). SIGTERM removed the containers and the volume and
  stopped the server.
- `./build.sh screenshots`: the smoke passed 16 of 16. The capture passed 112 of 112 (28 routes and states × 2
  themes × 2 viewports), and the contact sheet was written.
