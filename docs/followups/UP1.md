# UP1: local preview for a human walkthrough (after wave 4)

The user wants to explore the UI, try realtime with two windows, and take screenshots, before wave 5 starts. UP1 owns: `tests/web-fake-server/demo/**` (new), the minimal hook in `tests/web-fake-server` needed to start a demo scenario, the `preview` and `screenshots` verbs in `build.sh`, `tests/e2e/screenshots/**`, `deploy/compose.preview.yml`, and `docs/PREVIEW.md`.

## `./build.sh preview` (default: fake mode)

Builds `web/` and serves it from `tests/web-fake-server` in a **demo mode** on `http://localhost:5173`. It prints the URLs to open. Ctrl-C stops everything.

The demo scenario must make every screen explorable with believable data:
- **Identity.** The visitor is signed in as "Demo User". `?as=<name>` signs in as another demo member, so two windows can be two people.
- **Team and dashboard.** "Demo Team" has about six members with avatars, roles, invites, templates, endorsement types and a kudos leaderboard. The dashboard has recent activity.
- **Jira.** Fake Jira fixtures: browse buckets across sprints and backlog, issue details with a description that contains editable blocks and islands, comments, links and attachments.
- **Team poker session.** A team session with several rounds:
  - one revealed, with a winner and named votes;
  - one in voting, where **simulated peers lock in votes on timers**, react to reveal and to next round, and occasionally move the spotlight;
  - one pending.
- **Quick poker session.** A quick (guest) session with its own link.
- **Retro boards.** One board per stage (thinking, voting, discussing, closed), with cards, anonymous cards, votes, emoji, comments with mentions, a running shared timer, readiness, focus, action items, previous action items and endorsements. Simulated peers add cards and votes while you watch.
- **Realtime.** The user's own commands are acked and folded with the real protocol. State is shared across tabs and windows connected to the same demo server, so actions in one window appear live in the other.

## `./build.sh preview real`

`deploy/compose.preview.yml` runs Postgres 18 and Mailpit, then starts the real server with `dotnet run` (JIT is fine; this is a preview, not the AOT image) on `http://localhost:8080`. The web build is served by the server itself. It seeds one account (`demo@example.test` / printed password) through the real signup route, then verifies it through Mailpit.

The script prints:
- the Mailpit UI URL (`http://localhost:8025`) for magic links, resets and verify mails;
- an honest list of what works at this stage and what lands in wave 5. Teams, session and board creation over HTTP (U24, U25) and the Jira proxy (U26) arrive in wave 5.

Ctrl-C stops the server and removes the containers and volumes.

## `./build.sh screenshots`

Playwright against fake-mode preview captures every route and the key states in light and dark, at desktop (1440×900) and mobile (iPhone 13). Output goes to `artifacts/screenshots/<route>-<state>-<theme>-<viewport>.png`, plus an `index.html` contact sheet. The key states are:
- poker voting, revealed and drag-in-progress;
- the Jira browse panel and issue drawer;
- each retro stage, the timer running, and the kudos celebration.

## `docs/PREVIEW.md`

Covers prerequisites (Docker via Rancher Desktop, Node, the .NET SDK, `npx playwright install chromium`), the commands, the demo accounts, the two-window realtime walkthrough, and the wave 5 caveat list.

## Check

Run each of these:
- `./build.sh preview` in the background; `curl` gets 200 on `/`, `/teams/<demo>`, a poker route and a retro route; a Playwright smoke opens two contexts and sees a vote in one appear as "locked" in the other.
- `./build.sh preview real` brings up the stack, signup and login work, and the verify mail lands in Mailpit.
- `./build.sh screenshots` writes at least one PNG per route and theme.
- `./build.sh test` stays green.

**Status after UX18:** the demo no longer has a dashboard or a quick poker session (`docs/followups/UX18.md`).
