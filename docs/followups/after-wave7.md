# Open follow-ups after wave 7

```
U44 blocked verified= False open= ["blocker: U44's Check (`dotnet run --project tests/Retroscope.Soak -- --profile nightly --minutes 30` meets §j) has not passed. The builder's own records show the one all", 'major: The RSS gate and the database-oracle gates (lost_acked, duplicate, stale_overwrite, wrong_state) have never been judged at full nightly scale (281 sockets, 50x2']
   FU: Schedule a follow-up for the U13/U22 owner to fix ActorTimers. Record a key in _fired only when TimerRules.Expire actually expired the timer, or re-arm when Expire answers Unchanged and now < endsAt. Add a FakeTimeProvider regression test where the timer fires early, and a test that a timer started after a restart still expires.
   FU: After that fix lands on main: merge main into unit/U44, run the nightly Check once (dotnet run --project tests/Retroscope.Soak -- --profile nightly --minutes 30) at nice -n 10 against deploy/compose.test.yml, record the full report in docs/decisions/U44.md, then run ./build.sh test.
   FU: U13: classify SQLSTATE classes 08 and 57 from SqlRoomStore.CommitAsync as outcome unknown (bye + 1012) rather than a final internal nack (finding 2).
   FU: U8/U34: make card order consistent between the server projection and toWire for cards created in the same millisecond (finding 3).
   FU: Integrator/CI owner: add a nightly or pre-release job that runs the soak Check command, as §j asks.
U45 done verified= True open= []
   FU: U10/U29/U30/U31/U34/U36 owners: the axe scan found serious-impact issues: colour contrast on .btn-green, .btn-emerald, dark .btn-primary, inactive tabs and light .nt-chip-key; label-content-name-mismatch on .app-team-switch and .retro-kudos-button; aria-prohibited-attr on .retro-presence. Each is listed in KNOWN in tests/e2e/real/a11y.spec.js; remo
   FU: U22/U23: a display-name change (PATCH /api/me) does not reach a board room that is already loaded. After a reload, the mention picker still shows the old name. Which cache holds it is not known yet.
   FU: CI / build.sh owner: run ./build.sh e2e inside the pinned Playwright image and make Linux baselines for tests/e2e/real/visual.spec.js once. cmd_e2e could also export RS_E2E_BASE_URL.
   FU: U32: add a fake-server spec that the holder's preview keeps the spotlight, to pin the spotlight.js fix next to the strip specs.
   FU: Integrator: U45 changed files other units own: src/Retroscope.Server/Realtime/Actors/ActorTimers.cs (U13), web/src/poker/spotlight.js (U32), tests/Retroscope.Integration.Tests/Actors/PresenceAndTimerTests.cs, and the root package.json/package-lock.json (U0).
   MINOR: U45 changed files that other units own: src/Retroscope.Server/Realtime/Actors/ActorTimers.cs (U13), web/src/poker/spotlight.js (U32), tests/Retroscope.Integration.Tests/Actors/PresenceAndTimerTests.cs, the root package.json and package-lock.json (U0)
   MINOR: The real-stack visual baselines exist only as *-chromium-darwin.png. UF14 set the precedent of committing -linux baselines from the pinned Playwright image as well. Any Linux run of real/visual.spec.js will fail because those baselines are missing. C
   MINOR: The test 'a holder whose page is hidden for six minutes keeps the spotlight' is weak. page.clock only fast-forwards the client, and the server's release grace is 3 real minutes. If the hidden tab's socket watchdog wrongly dropped and reopened the soc
   MINOR: The axe scan passes with a KNOWN allowlist of serious violations: colour contrast, label-content-name-mismatch and aria-prohibited-attr. They are in other units' code. §j asks only for an 'axe scan of core screens', so this is within the spec, but th
   MINOR: unit/U45 is 8 commits behind main (the U46 importer work, which touches only importer paths and docs). git merge-tree shows the merge is clean.
UF15 done verified= True open= []
   FU: U48's decision record still says the Playwright default base URL is http://localhost:8080. It is now https://localhost:18443, and U48.md was not on UF15's append list.
   FU: tests/e2e/fake/poker-table/visual.spec.js still has its own test.use({timezoneId:'UTC', locale:'en-US'}). It is now redundant and the spec's owner can remove it.
   FU: After UF15's config change, the full four-browser fake e2e suite was run on Chromium only (400 passed). The Check covers only the harness specs on all four browsers; the next full matrix run will cover Firefox and WebKit with the time zone pin.
   MINOR: Environment note, not a code defect: my first full `./build.sh test` run exited 2. 19 of 3177 .NET tests failed, all with infrastructure errors (13 Npgsql 'Exception while reading from stream', 4 Npgsql timeouts, 1 statement timeout, 1 LeadershipLost
   MINOR: The test An_owner_change_that_fails_to_promote_leaves_the_old_owner_in_place only checks that the status is not 204 (Assert.NotEqual NoContent). It does not check the actual error mapping, which the decision record says is 'internal' (a 500).
   MINOR: Some docs still describe the old state and are outside UF15's owned paths: docs/DESIGN.md (around line 1379) still lists ui/select.js in the file tree, U48's decision record still says the Playwright base URL default is http://localhost:8080, and tes
=== MERGE ===
Wave 7 is integrated. U45 and UF15 are merged into main, U44 is not, and the full suite and aot-smoke both pass on the merged main.

**Merged** (`git merge --no-ff`, in unit-number order, no conflicts, nothing pushed):
- **U45**: merge commit e17c6ed. I accepted its edits to files other units own: `src/Retroscope.Server/Realtime/Actors/ActorTimers.cs` (U13), `web/src/poker/spotlight.js` (U32), `tests/Retroscope.Integration.Tests/Actors/PresenceAndTimerTests.cs`, the root `package.json` and `package-lock.json` (U0), `tests/Fakes.Host/*` and `deploy/compose.test.yml`. They are recorded in `docs/decisions/U45.md`.
- **UF15**: merge commit 4e3c77f, now the tip of main.

I removed both worktrees and deleted `unit/U45` and `unit/UF15`.

**Not merged:**
- **U44** is blocked and unverified. Its nightly soak Check has never passed: the one allowed run gave no verdict, and a later short run failed the `timer_missing` gate. The RSS and database-oracle gates have never been judged at nightly scale. The `unit/U44` branch and its worktree at `<repo>-wt/U44` are still in place.

**Suite on main at 4e3c77f** (no `fix(integration)` commit was needed):
- `./build.sh test` exited 0:
  - .NET: 3186 run, 3186 passed, 0 failed, 0 skipped.
  - Simulation: 2000 of 2000 seeds passed.
  - Web unit tests: 349 of 349 passed.
  - Golden vectors verified against their recorded hashes only, because the original repo path wasn't given.
- `./build.sh aot-smoke` exited 0, with no warnings in the log. I ran it because C# under `src/` changed (`ActorTimers.cs`).
- The host's load average was about 18 when the run started.
- I started no containers of my own.

**Follow-ups:**
1. **U44 should now be unblocked.** U45's fix to `ActorTimers.cs` (commits b967051 and 0afda17) is now on main. It rounds the timer delay up and re-arms a timer that fires early. That looks like the same bug that failed U44's `timer_missing` gate, so a separate fix for the U13/U22 owner may not be needed. The steps for U44:
   - Check whether 0afda17 covers the early-fire case and a timer started after a restart. Add a regression test with FakeTimeProvider if it doesn't.
   - Merge main into `unit/U44`.
   - Run the nightly Check once at `nice -n 10` against `deploy/compose.test.yml`, and record the full report in `docs/decisions/U44.md`.
   - Run `./build.sh test`.
2. **Tell the U13 and U32 owners** about U45's fixes in `ActorTimers.cs` and `web/src/poker/spotlight.js`, so their next changes keep them.
3. **U45 Linux baselines:** the real-stack visual baselines exist only as `-chromium-darwin.png`. Generate the `-linux` ones in the pinned Playwright image, as UF14 did.
4. **U45 spotlight test is weak:** "a holder whose page is hidden for six minutes keeps the spotlight" should also check that no new WebSocket opened for the holder, or that the role stays `bound`.
5. **U45 accessibility scan:** it passes with a known list of serious violations in other units' code (U10, U29, U30, U31, U34–U36). Remove each entry as those units fix them.
6. **UF15 owner-change test:** `An_owner_change_that_fails_to_promote_leaves_the_old_owner_in_place` should assert the exact status and problem code (500 `internal`), not just "not 204".
7. **Stale docs for their owners:**
   - `docs/DESIGN.md` (around line 1379) still lists `ui/select.js`, which UF15 deleted.
   - U48's decision record still gives the old Playwright base URL default, `http://localhost:8080`.
   - `tests/e2e/fake/poker-table/visual.spec.js` still sets its own time zone and locale, which is now redundant.
8. **Flaky Postgres tests:** expect Postgres-backed integration tests to flake while other agents' compose stacks are saturating Docker. This is what UF15 saw; this run was clean.

The full logs are in `<scratch>/w7-test.log` and `<scratch>/w7-aot.log`.
```
