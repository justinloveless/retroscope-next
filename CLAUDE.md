# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Retroscope Next is a ground-up rewrite of Retroscope, a planning-poker and retrospective app. It has a C# (.NET 10, Native AOT) server, Postgres 18 and a vanilla-JS front end, deployed with Docker Compose on Coolify. Production runs at https://nextroscope.lovelesslabs.net. The original Supabase data was imported and cut over on 2026-10-22.

`docs/DESIGN.md` is the authoritative spec, and section (k) is the implementation plan. `docs/decisions/<unit>.md` records why each unit deviated from the spec or filled a gap in it, and `docs/followups/` holds open follow-ups. Read the relevant decision files before changing a subsystem. `docs/DESIGN.html` and `deploy/COOLIFY_SETUP.html` are rendered copies of their `.md` files. They go stale when the Markdown changes, so regenerate them or flag them as stale.

## Tenets

These came from the owner at the start of the project and shape every decision. Changes that break them need the owner's explicit agreement.

1. **Core features come first.** The core is:
   - planning poker, including the Jira refinement flow: the backlog panel, round strip, issue details and submitting points to Jira;
   - retro boards, with endorsements (kudos) and team action items.

   The original's chat, notifications, Slack, AI/TTS, billing, organizations, feature flags, impersonation, music uploads and local advisor were dropped on purpose (DESIGN §0.1). The importer archived their data. Do not bring them back unasked.
2. **Fast on small hardware, for users anywhere in the world.** App and database share a 1 vCPU / 1 GB slice, enforced by compose limits and `DeployGuardTests`; the memory targets are in DESIGN §i.
   - **AOT rules.** The Server and Core are published Native AOT, so in them the following are all off-limits:
     - reflection, `dynamic` and `Activator`;
     - JSON serialisation outside the source-generated contexts in `Server/Json` and `Core/Protocol`;
     - EF Core. Data access is Npgsql's slim builder only.
   - **Warnings are errors.** `Directory.Build.props` sets `TreatWarningsAsErrors`, and `aot-smoke` fails on any AOT or trim warning.
   - **Dependencies are pinned by tests.** `DependencyRuleTests` pins each project's NuGet references (Core: BCrypt.Net-Next; Server: Npgsql; Importer: AngleSharp and Npgsql), so a new package fails it. That needs the owner's agreement. `tests/web-unit/core/deps.test.js` pins the web packages the same way.
   - **Front end.** It is plain ES modules. esbuild is the only build dependency, and the ProseMirror packages (UX11) are the only runtime ones, bundled into the lazy `rich-editor` chunk. `web/build.mjs` rewrites `prosemirror-view` so no HTML parser ships. Chunk sizes are gated by `web/budgets.json`: a build warns over budget and fails at more than 10 % over.
   - **Delivery.** Static assets are precompressed, and pages ship a boot snapshot (`Hosting/Boot.cs`) so a room paints after one round trip.
3. **Realtime must be rock solid.** Every shared room state has exactly one writer: a single-threaded `RoomActor` per poker session, board or team.
   - **Write path.** Clients send intents, never rows.
     - **Preconditions.** Overwrite intents carry a precondition (`baseVersion` or an expected value). Commands that set a value directly, such as votes, lock and ready, need none.
     - **Idempotent creates.** Creates use client-generated ids and are idempotent.
     - **Ephemeral kinds** are dropped rather than resent after a reconnect (DESIGN §c.4–§c.6).
   - **Commit first.** The actor commits to Postgres, fenced by the leader epoch, before it broadcasts or acks anything.
   - **Recovery.** Events are sequenced per room, and clients resume or resync after a reconnect.
   - **Presence** is derived on the server.
   - **The simulator enforces it.** `tests/Retroscope.Sim` must stay green under `--strict`. Never weaken a sim or soak gate to make a run pass; find the product bug.
4. **Keep as little sensitive data as possible.** Never persist Jira titles or descriptions: they are fetched live and cached only in memory.
   - **Vote secrecy.** Poker votes never reach other clients before reveal. The projections (`PokerProjection`, `RetroProjection`) enforce this, and tests check it on the wire.
   - **No HTML.** No HTML is stored or rendered. User text is a restricted markup (`Core/Markup`, `web/src/markup`), and `web/build.mjs` fails the build on `innerHTML`, `outerHTML`, `insertAdjacentHTML` and `document.write`/`writeln`.
   - **Jira credentials** are sealed with AES-GCM under `APP_KEYS`, are write-only, and are never sent to a browser.
   - **Jira hosts** are limited to `*.atlassian.net` plus `JIRA_HOST_ALLOWLIST`.
   - **Anonymous cards** store only a keyed tag (`AnonTagger`), never an author.
   - **Authorization** lives in one place, `Core/Authz`, and defaults to deny.
5. **Jira must be rock solid.** Every Jira call goes through the server proxy in `Server/Jira`, which handles throttling, `Retry-After`, a circuit breaker, single-flight caching and error mapping.
   - Description edits never lose content. ADF blocks that markup cannot round-trip become islands, which are spliced back byte for byte (`Core/Adf`).
6. **The repository will become public** (`docs/PUBLISHING.md`). Never commit any of the following:
   - absolute local paths;
   - real people's data, including anything from the Supabase dump or the import report;
   - details of the original app's security weaknesses (it is still live, behind a redirect).

## Working conventions

- **Commits** are small and split by concern: tests and fixtures separate from production code, refactors separate from features (DESIGN §k.1).
- **The host is the owner's workstation.** `build.sh` runs at low priority (`RS_NICE`, default 10) and caps test parallelism with `RS_JOBS`: a third of the cores, at least 2. CI sets `RS_JOBS=0`.
  - Never generate artificial CPU or memory load: no `yes`, busy loops or stress tools.
  - Don't run the full suite repeatedly.
  - Remove any containers you start.
- **Only verified work merges to `main`.** `main` deploys to production by itself once CI passes on it, so a merge is a release.
- **Test while you work with `./build.sh quick`, and nothing bigger.** It maps the changed files (since the merge base with upstream, including uncommitted and untracked files) to the narrowest tests that cover them through the table in `tools/quick.mjs`, and runs only those. Pass paths to narrow it further, or `--plan` to see the commands without running them.
  - Do not run `./build.sh test`, `./build.sh`, the 2000-seed sim or `aot-smoke` during development. The full suite runs once, before a push.
  - When a path has no focused test, `quick` says so. Add a row to the `RULES` table rather than reaching for the full suite.
  - `quick` reports files that tests regenerated (`protocol.gen.js`, golden transcripts). Review and commit them.
  - It builds the web bundle into a temporary directory, so `web/dist` is never touched.
- **Verify before every push with `./build.sh verify`.** It is the full pass CI runs: `test`, `web`, generated files committed, then `aot-smoke`. It needs a clean working tree and records the tree it passed. It takes far longer than a tool call's timeout, so commit first, run it as its own background step, and push only after it passes.
  - The pre-push hook (installed into `.git/hooks` by any `build.sh` verb, and shared by every worktree) refuses to push a commit whose tree has not passed `verify`. It never runs the pass itself. A rebase or amend that leaves the content unchanged keeps its pass.
  - Never bypass it. `.claude/hooks/guard-push.sh` is a tripwire, not a guarantee: it blocks `--no-verify` (and its abbreviations), `hooksPath` overrides and hand-made stamps. If `verify` fails, fix the cause.
  - This replaces any general "run the full suite after every change" rule for this repository: the full suite runs at push time.
- **Parallel agents** each work in their own `git worktree`. Two `dotnet build` runs in one checkout fight over `bin/` and `obj/`.
- **UI quick mode.** For UI iterations the owner reviews by eye, the owner may ask for quick mode: run `./build.sh quick` and self-review screenshots, with no e2e. Quick-mode work is unverified, so it stays on a branch (pushing the branch still needs `verify`, which has no e2e). The owner reviews it locally (`./build.sh preview`) or on the preview slot, through the `preview` label on its PR or a manual run of the preview workflow. The gaps it leaves (visual baselines, e2e specs) must be closed by a full verified pass before it merges to `main`.

## Commands

`build.sh` is the entry point. On Rancher Desktop it sets `DOCKER_HOST` and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` itself. The Integration and Importer tests need Docker (Testcontainers).

```sh
./build.sh quick [paths] [--plan]    # development loop: only the tests covering the changed files
./build.sh verify                    # full pass before pushing: test, web, generated files committed, aot-smoke
./build.sh                           # test, then web
./build.sh test                      # dotnet build + all .NET tests + simulation (2000 seeds, --strict)
                                     #   + web-unit and web-fake-server node tests + golden-vector check
./build.sh web                       # esbuild bundle into web/dist (budgets, CSP hashes, banned-sink check)
./build.sh aot-smoke                 # AOT publish with zero warnings, native binary + chiseled image probes
./build.sh fake-e2e "poker-*/*.spec.js"   # Playwright against tests/web-fake-server (glob under tests/e2e/fake/)
./build.sh e2e "real/poker*.spec.js"      # Playwright against deploy/compose.test.yml (rebuilds images)
./build.sh preview [fake|real]       # fake: demo with simulated teammates (PREVIEW_PORT, default 5173; real: 8080)
./build.sh screenshots               # every route and state into artifacts/screenshots/index.html
dotnet run --project tests/Retroscope.Soak -- --profile short    # soak/chaos run (nightly = 30 min); manual only
```

**CI** (`.github/workflows/ci.yml`) runs `./build.sh test`, `./build.sh web`, `git diff --exit-code` (generated files must be committed) and `./build.sh aot-smoke`. It does not run e2e or soak.

**Running a subset:**

- **.NET tests by category or name.** For example: `dotnet test tests/Retroscope.Core.Tests --filter "Category=Poker"`, or `dotnet test tests/Retroscope.Integration.Tests --filter "FullyQualifiedName~LoginTests"`.
  - Tests run on Microsoft.Testing.Platform (`global.json`). A filter that matches nothing exits with code 8.
  - Export the two Docker variables `build.sh` sets before running `dotnet test` directly.
- **Web unit tests.** Use `node --test tests/web-unit/poker/*.test.js`. Pass explicit files, not a directory: directory arguments fail on Node 22, which CI uses, even though they work on Node 20.
- **Simulation.** `quick` runs `dotnet run --project tests/Retroscope.Sim -- --seeds 50 --strict --no-teeth` (a small seed count needs `--no-teeth`); `verify` runs the 2000-seed pass with teeth. To reproduce a failure, run the `replay:` line the sim prints, which carries the seed, steps and fault set.
- **Regenerating pinned outputs.** These are regenerated with environment variables, and the diff must then be reviewed:
  - `RS_UPDATE_SCHEMA_SNAPSHOT=1` (`tests/schema.snapshot.sql`)
  - `RS_UPDATE_AUTHZ_MATRIX=1` (`tests/authz-matrix.csv`)
  - `RS_UPDATE_GOLDEN=1` (the importer identity goldens)
- **Generated protocol files.** Running `dotnet test tests/Retroscope.Core.Tests --filter Category=Protocol` regenerates `web/src/net/protocol.gen.js` and `docs/protocol.md` from `Core/Protocol/Catalog.cs`.

## Architecture

**Projects** (`Retroscope.slnx`):

- **`src/Retroscope.Core`**: pure, with no IO, clock, ASP.NET or Npgsql. It holds:
  - typed ids;
  - the wire protocol (`Protocol/Catalog.cs` lists every command and event kind);
  - the room domains `Poker`, `Retro` and `Team`, each implementing `Room/IRoomDomain`:
    - `Decide` is pure and runs before commit;
    - `Apply` runs after commit;
    - `OnPresence`, `OnWarm` (once after load) and `Timers` handle presence, warm-up and timers;
    - per-viewer projections are separate classes beside each domain;
  - `Authz`, `Markup`, `Adf` and `Security`, which covers the keyring, sealing, token hashing and the Jira host policy.
- **`src/Retroscope.Server`**: the ASP.NET Core minimal-API host.
  - **`Program.cs` is frozen.** Each module exposes static `AddServices`/`Map` hooks on its own files, and `Program.cs` calls them in a fixed order. `CompositionRootTests` pins the module list and the JSON-context chain. Add behaviour through existing hooks. A new module or JSON context means editing `Program.cs` and that test together.
  - **The realtime path:**
    1. `Realtime/Transport` (WebSocket, origin and rate checks);
    2. `Realtime/Actors/RoomGateway` and `ActorRegistry`;
    3. one `RoomActor` per room;
    4. `Realtime/Stores` (SQL per event) through `Data/Leader` (leader lease plus epoch fence on every write).
  - `Realtime/Rooms` bind each Core domain to the runtime.
- **`src/Retroscope.Importer`**: a JIT console (`retroscope-import`) that maps the Supabase schema into this one and archives dropped data.
  - **Production is already cut over.** `cutover_at` is set in its database, so `run` refuses unless given `--force-reimport --i-have-a-dump=<path>` and the database name at a prompt.
  - The procedure is in `deploy/IMPORT_RUNBOOK.md`.
- **`db/migrations`**: applied by the migrator after the server takes the leader lease.
  - **Deployed migrations are never edited.** The migrator refuses to start if an applied file changed, and versions must be contiguous, so add the next `000N_*.sql`.
  - **Expand/contract only.** A redeploy stops the old binary before starting the new one, so a migration must be quick and compatible with the previous release (`deploy/RUNBOOK.md` §11).
- **`web/`**: vanilla JS with no framework.
  - **Routing.** `web/src/routes.js` is the frozen route table, and each screen module exports `mount()`.
  - **Network.** `web/src/net` holds the single app socket, the outbox with resend and supersede keys, and the room client.
  - **Screens.** The screens are in `poker/`, `retro/` and `team/`; `markup/` holds the parser, renderer and editor.
  - **Styles** live in `web/styles`, and importing CSS from JS fails the build. A new route stylesheet needs a `ROUTE_STYLES` entry in `web/build.mjs`.
- **Tests:**
  - `Retroscope.Core.Tests`: units and FsCheck properties.
  - `Retroscope.Integration.Tests`: Testcontainers Postgres, with a fresh migrated database per test through `PostgresFixture.CreateDatabaseAsync`.
  - `Retroscope.Importer.Tests`: a fake Supabase source.
  - `Retroscope.Sim`: deterministic simulation of real actors under injected faults.
  - `Retroscope.Soak`: the soak and chaos run.
  - `Retroscope.AotSmoke`: the AOT smoke probe.
  - `Fakes`: Jira, OIDC and SMTP fakes.
  - `web-unit` and `web-fake-server`: the front-end tests, the fake server and the demo preview.
  - `e2e`: Playwright, against the fake or the real stack.
  - `golden`: vectors taken from the original app.

## Deployment

`.github/workflows/images.yml` builds and pushes `ghcr.io/justinloveless/retroscope` and `retroscope-import`, tagged with the commit SHA, on every push to `main` and on every `v*` tag. `deploy/docker-compose.yml` pulls them by digest and never builds on the VPS.

Coolify's own auto deploy is off; GitHub Actions drives every deploy through the Coolify API, using `deploy/coolify-deploy.sh`. The script pins the resource's branch and commit (`git_commit_sha`, so the compose file matches the images), sets `APP_DIGEST`/`IMPORTER_DIGEST` (the hex after `sha256:`), deploys, and waits for `/healthz` and `running:healthy`.

- **Production** (`.github/workflows/deploy.yml`) deploys automatically after `ci` succeeds on a push to `main`. It follows that push's `images.yml` run and takes the digests from its `image-digests` artifact, never from a GHCR tag. It skips a commit only when a newer commit on `main` has a `ci` run that is running or passed. A manual run from `main` redeploys or rolls back to any commit whose `ci` run on `main` passed and whose artifact still exists. Its token lives in the `production` environment, which only `main` can use.
- **Preview** (`.github/workflows/preview.yml`) is a single slot on its own Coolify resource, server and team, with its own token (`preview` environment), database and secrets. A same-repository PR labelled `preview` deploys on label, push and reopen, and a manual run deploys any branch. Preview images are tagged `preview-<sha>`. The last deploy wins, and the PR gets one sticky comment with the URL and commit.
- The repository variables, the token's permissions and the by-hand fallback are in `deploy/COOLIFY_SETUP.md` section 11.

The guides are:
- `deploy/COOLIFY_SETUP.md`: setup and every environment variable;
- `deploy/RUNBOOK.md`: operations, backups and key rotation;
- `deploy/IMPORT_RUNBOOK.md`: the data import.

**Never change `ANON_TAG_KEY`.** It would orphan every anonymous card from its author. Rotate `APP_KEYS` only as the RUNBOOK describes: add the new key first and keep old keys until stored secrets are re-sealed.
