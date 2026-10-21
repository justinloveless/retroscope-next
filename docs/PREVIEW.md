# Local preview

Two ways to look at Retroscope before wave 5. **Fake mode** shows the whole UI with believable data and live
teammates. **Real mode** runs the real server with Postgres and Mailpit, so far for the auth flows only.

## Prerequisites

- **Docker** (Rancher Desktop). Only real mode needs it. `build.sh` finds Rancher Desktop's socket
  (`~/.rd/docker.sock`) by itself.
- **Node** 20.19 or newer (CI uses 22). `build.sh` runs `npm ci` when `node_modules` is missing or stale.
- **.NET SDK** 10.0.302 or a later 10.0 feature band (`global.json`). Only real mode needs it.
- **Playwright Chromium**, for the screenshots: `npx playwright install chromium`.

## Commands

| Command | What it does |
|---|---|
| `./build.sh preview` | Builds `web/` and serves it from the demo fake server on http://localhost:5173. Prints the URLs to open. Ctrl-C stops it. |
| `./build.sh preview real` | Starts Postgres 18 and Mailpit (`deploy/compose.preview.yml`), runs the real server with `dotnet run` on http://localhost:8080, and seeds `demo@example.test` (password printed). Ctrl-C stops the server and removes the containers and the volume. |
| `./build.sh screenshots` | Runs the preview smoke checks, then captures every route and key state into `artifacts/screenshots/`. Open `artifacts/screenshots/index.html` for the contact sheet. |

If a port is taken, the command says so and names the variable to change: `PREVIEW_PORT` (both modes),
plus `PREVIEW_MAILPIT_PORT` (8025), `PREVIEW_SMTP_PORT` (1025) and `PREVIEW_PG_PORT` (55432) in real mode.
For example: `PREVIEW_PORT=5199 ./build.sh preview`.

## Fake mode

The demo lives in `tests/web-fake-server/demo/`. It runs in memory and resets when you restart it. Every
window connected to the same demo server shares its state.

### Who you are

You are **Demo User** (team owner and app admin) unless you say otherwise. Add `?as=<name>` to any page
to become another demo account. The choice is kept in a cookie for that host.

| `?as=` | Person | In Demo Team |
|---|---|---|
| `demo` | Demo User | owner (also app admin) |
| `priya` | Priya Raman | admin |
| `marcus` | Marcus Chen | member |
| `sofia` | Sofia Alvarez | member |
| `jonas` | Jonas Weber | member |
| `aisha` | Aisha Okafor | member |
| `tom`, `lena` | Tom Becker, Lena Novak | Platform Guild only |
| `signed-out` | nobody | (shows the landing page, sign-in and the guest join form) |

On the sign-in page, any of the emails above (`priya.raman@example.test`, …) signs you in with any
password. Signing up creates a new account. Mails (verification, sign-in links, resets, invites) are
printed in the terminal that runs the demo, with the link to open.

### What is in it

- **Demo Team**: six members with avatars, two pending invites, three board templates, four kudos types
  and a kudos leaderboard. **Platform Guild** is a second, quieter team.
- **Dashboard and mentions**: recent boards and sessions, and cards and comments that mention you.
- **Jira**: a fake `DEMO` project with two sprints and a backlog. `DEMO-123` has a description with a
  table island, comments, links and attachments. Team settings show the connection.
- **Team poker session** (`PKR41CHKT1`): three revealed rounds with winners and named votes, round 4
  (`DEMO-123`) in voting and round 5 (`DEMO-124`) pending as Priya's preview.
- **Quick poker session** (`/poker/QCKPKR2026`) with two guests.
- **Retro boards**, one per stage:
  - `SPR42THNK1`, thinking. The shared timer is running and new cards keep arriving.
  - `CHKVTNG042`, voting. Votes are counted and voters stay hidden.
  - `SPR41DSCSS`, discussing. One card is in focus, and the board has comments with mentions, action
    items and kudos.
  - `SPR40CL5D1`, closed.

  An archived board sits under the team's Archived filter.

### The simulated teammates

While at least one window is open on a room, the teammates the demo plays act in it:

- **Poker:** they pick cards and lock in on timers (Aisha sometimes abstains). When everyone present has
  locked in, the round reveals itself. About 7 s after a reveal, Priya previews the next ticket. About
  7 s later she ends the revealed round and starts the preview. Now and then she moves the spotlight to
  an earlier round and back.
- **Retro:** what they do follows the board's stage. In thinking they add cards and comments, mark
  themselves ready and restart the timer when it runs out. In voting they vote, then get ready. In
  discussing they comment on the focused card, add action items and give kudos.

A teammate you are playing through `?as=` is left to you. Start the demo with `--quiet-peers`
(`node tests/web-fake-server/demo/main.js --quiet-peers`) to keep everyone still.

### Two-window walkthrough

1. `./build.sh preview`.
2. Window A: http://localhost:5173/teams/0d3e0000-0001-7000-8000-000000000001/poker/PKR41CHKT1 (Demo
   User).
3. Window B, in the same browser:
   http://127.0.0.1:5173/teams/0d3e0000-0001-7000-8000-000000000001/poker/PKR41CHKT1?as=priya.
   `localhost` and `127.0.0.1` keep separate cookies, so B is Priya while A stays Demo User. A private
   window or a second browser works too.
4. Pick a card in A and lock it in. B shows Demo User's seat as locked at once. Reveal in either window
   and both flip.
5. Try the same with a retro board: add a card or vote in one window and watch the other.

### Not in the UI yet

Some panels belong to front-end units that have not landed yet: the round strip and spotlight controls
(U32), the Jira browse panel and issue drawer (U33), the retro stage bar, timer, focus and previous
action items (U35), and the kudos picker and celebration (U36). The demo already serves their data, so
they work as soon as those units merge. Until then:

- you see the board at each stage, but no stage controls;
- the poker table follows the spotlight, but you cannot take it;
- the Jira data shows up in round titles, topic badges and team settings.

## Real mode

`./build.sh preview real` prints the sign-in details and this list. It is accurate for wave 4.

**Works now**

- Sign-up, sign-in and sign-out.
- Magic links, password resets and email verification. The mails land in Mailpit at
  http://localhost:8025.
- Account settings and sessions.
- The shell pages.

**Arrives in wave 5**

- **Realtime rooms.** An `UnavailableRoomGateway` stub is still registered, so the WebSocket does not
  reach poker or retro rooms yet.
- **Teams, the dashboard and team lists, and creating sessions and boards over HTTP** (U24, U25). Until
  then the dashboard shows an error.
- **The Jira proxy** (U26).

The server log is `artifacts/preview/server.log`. The session cookie is `__Host-rs_sid` (`Secure`), which
a browser keeps over plain `http://localhost` only if it counts localhost as a secure origin. Sign-in was
checked in Chromium; if another browser loses the session right after signing in, use Chrome.
