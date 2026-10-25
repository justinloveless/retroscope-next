# Open follow-ups after wave 5

```
U24 done verified= True open= []
   FU: U23/UF9: MembershipCache.MembersSql should join teams with deleted_at is null (or the rooms should learn TeamDeleted), so a reload after a restart or Forget does not give a deleted team's members access to its poker sessions and boards again.
   FU: Realtime timing tests (LivenessTests, PresenceAndTimerTests, RingAndResumeTests, PokerRuntimeTests, OutboundBufferTests) fail when the machine is heavily loaded (load average about 50 to 65) but pass in isolation and on a quieter full run. UF9 may want to look at how tightly their timing is set.
   MINOR: Ownership transfer in TeamsEndpoints.TransferAsync is two separate actor asks: demote the owner, then promote the target. Between them the team briefly has no owner. If the process crashes after the demote, the team is left without an owner. The roll
   MINOR: A soft-deleted team stays locked out only because the membership cache holds an empty roster. MembershipCache.LoadAsync does not filter on teams.deleted_at, so after a restart or Forget(team) a reload would let the deleted team's members back into it
U25 done verified= True open= []
   FU: Integrator: when merging unit/UF9, RetroRoom.cs will have changes from both branches. UF9 adds IsCreate and switches to AddRoom. U25 rewrites DurabilityOf as a switch that includes BoardDeleted and adds IsDeleted. The hunks are separate, but keep both sides.
   FU: tests/Retroscope.Core.Tests/Retro/StageMatrixTests.cs gained one test (Deleting_marks_the_board_deleted_once). This path is outside U25's owned paths.
   MINOR: The deletion-kick fix edits files U25 does not own: src/Retroscope.Core/Retro/{RetroApply,RetroDecider,RetroEvents,RetroInput,RetroProjection,RetroState}.cs, tests/Retroscope.Core.Tests/Retro/StageMatrixTests.cs, and src/Retroscope.Server/Realtime/Ro
   MINOR: RetroInput.Delete has no authorization check in the decider. This is safe today: a WebSocket client only produces RetroInput.Client, and the only caller is DELETE /api/retro/{id}, which checks Capability.RetroDeleteBoard first. The safety depends on 
U26 done verified= True open= []
   FU: U33: build the drawer and browse panel against the shapes and query values in docs/decisions/U26.md. `editable` gates description editing, `descriptionMarkup` is ready-made markup with island refs, `updated` is the `descriptionBase`, and browse takes `session` and `hidePointedOrPointing` and returns `roundState` per row.
   FU: U36: 'Create in Jira' should POST `{summary, description: <card text>, fromRetro: true}` to `/api/teams/{id}/jira/issues`.
   FU: U48: `tests/web-fake-server/jira.js` should accept status not_done|all, points any|<number>, session, hidePointedOrPointing and fromRetro, and return editable/descriptionMarkup, so screens see what the server sends.
   FU: U19/FakeJira: the fake ignores JQL (sprint, backlog and search pages return every issue), so JQL filters are tested on request bodies only.
   FU: Integrator: when UF9 changes ActorRegistry, check that `ActorRegistry.Post<PokerState, PokerInput>` (used for JiraSettingsChanged) and `PokerJiraLoop.Invalidate` keep their contracts.
   MINOR: DELETE /api/teams/{id}/jira/links/{linkId} accepts any numeric link id on the team's Jira site. Unlike attachments, it does not check that the link belongs to an issue the caller is viewing, and it then drops every cached drawer for the team. It is s
   MINOR: My full ./build.sh test run failed on two Core timing tests: AdfCapsTests.Hostile_shapes_convert_quickly and MarkupJiraLengthTimingTests 200 ms. The machine's load average was about 80 at the time. U26 does not touch either test. Run alone, Retroscop
   MINOR: FakeJira ignores JQL, so the browse status, points and scope filters are only checked by asserting on the JQL text sent in request bodies. Nothing tests that they actually filter the results. The builder recorded this as a follow-up.
U27 done verified= True open= []
   FU: Integrator (UF9): SpotLoadCommand builds `new RetroStore(...)` when no RetroStore is registered, and uses PokerBinding, RetroBinding and TeamBinding directly. If UF9 changes those constructors or registers RetroStore, adjust it in a fix(integration) commit. AdminEndpoints relies on ActorRegistry.QueryAsync, ActorRegistry.AskAsync and ActorRuntime.R
   FU: U42: the import-verify test can call SpotLoadCommand.RunAsync(services, sample, output, ct) in process, which returns a SpotLoadReport. Memberless or ownerless teams can be repaired with POST /api/admin/teams/{id}/members using role owner.
   FU: U43 (RUNBOOK): run `retroscope spot-load --sample 20` with the app stopped, after pg_restore and before cutover. It exits 1 with 'Stop the app before running spot-load' if the app holds the lease.
   MINOR: In AdminEndpoints.SetMemberAsync, an owner transfer takes two separate actor Asks: it demotes the old owner to admin, then promotes or adds the new owner. If the second Ask fails (Unavailable or a unique-index failure), the team is left with no owner
   MINOR: The retention and team-purge deletes check ILeaderEpoch.IsLeader once, at the start of a pass, and do not run under the epoch fence (rs_fence). An instance that loses the lease partway through an hourly pass keeps deleting rooms that a new leader may
   MINOR: RetentionJob lists idle quick rooms ordered by last_activity_at, limit 500, and a skipped live room keeps its old last_activity_at. If 500 or more of the oldest idle rooms all have subscribers, every pass picks the same rooms and nothing else is purg
   MINOR: The verifier's first full-suite run failed one test, Retroscope.Core.Tests.Adf.AdfCapsTests.Hostile_shapes_convert_quickly. It has a 2-second wall-clock assertion and failed with load average around 58 from other agents. U27 does not touch this test.
U32 done verified= True open= []
   FU: The integrator should review the fix(integration) commit 8295fcb, which changes U31-owned index.js, neotro.css and visual baselines.
   FU: U31's visual spec should pin timezoneId before CI generates its -linux baselines, because the History panel shows a local 'Revealed by X · HH:MM' time.
   FU: U33 or the integrator can replace icon('tag') on strip chips with the issue-type glyph from ui/glyphs.js once that file exists on main.
   FU: AdfCapsTests.Hostile_shapes_convert_quickly uses a 2 s wall-clock limit and can fail when the machine is heavily loaded. Consider loosening the limit or measuring a different way.
   FU: The entry chunk was already over its brotli budget on main (about 12.6 KB against 12 KB) before U32. It is a warning only.
   MINOR: Full `./build.sh test` exited 2 in my run: 2891/2892 passed. The one failure was AdfCapsTests.Hostile_shapes_convert_quickly, a 2 s wall-clock test that took 2.53 s with the load average around 75. U32 changes no C#. Run alone, the test passed (1.9 s
   MINOR: The unit edits files it does not own: web/src/poker/index.js (U31), web/styles/neotro.css (U31) and U31's visual baselines. They are in one fix(integration) commit (8295fcb) and recorded in docs/decisions/U32.md. The Check needs them to see any of th
   MINOR: Strip chips show icon('tag') where §g.8 asks for the issue-type glyph, because ui/glyphs.js (U33) is not on main yet.
U33 done verified= True open= []
   FU: U26: serve the HTTP shapes in docs/decisions/U33.md, contract 1. That includes optional 'editable', links with type, direction and optional label, and link POST {typeId, direction, targetKey}. Alternatively, change the screens and specs to new shapes in the same integration commit.
   FU: Integrator: merge the poker/index.js FEATURES line with U32's entry (one-line conflict).
   FU: U31: a 'center' (or 'table') slot in ctx.slots would replace the DOM lookup submit.js uses to place the chips.
   FU: U10/UF11: add a jira.css to ROUTE_STYLES for poker routes so the CSSOM layout rules can move into a stylesheet. Style .panel-*, .island and Jira tables inside .jira-prose. (Done on 2026-10-22 by UX8: web/styles/jira.css is in ROUTE_STYLES for poker routes and styles panels, islands, tables and every other Jira kind inside .jira-prose. The drawer's own layout rules still use the CSSOM.)
   FU: UF11: once lazy chunks are measured net of their importer, issue-drawer.js can lazy-import markup/editor.js.
   FU: U32 and whoever builds the right panel's Issue/History tabs: createIssueDrawer can be hosted inline (§0.1). Round-chip hover prefetch needs the drawer's prefetch exposed through the feature context.
   FU: Panel resizing (240–480 px, §g.8) of the left Jira panel is not done; wrap the slot with ui/resizable.js.
   MINOR: The left Jira panel can't be resized (240–480 px, §g.8). The builder documented this as a follow-up. The §k Check doesn't list it.
   MINOR: Ownership deviation: web/src/poker/index.js (owned by U31) gets one import and one FEATURES entry in a fix(integration) commit. U31 decision 4 allows this as a change request or an integrator commit. It will conflict on one line with U32's entry.
   MINOR: submit.js ships in the poker chunk, while §g.1/§g.7 put it in jira-drawer. jira-drawer is at 22,311 of 22,528 B, which leaves almost no headroom for future drawer changes.
   MINOR: The HTTP shapes the screens depend on (docs/decisions/U33.md contract 1: editable, links {type, direction, label}, link POST {typeId, direction, targetKey}) are checked only against U48's fake and the spec overrides. U26 must serve them, or the real 
U35 done verified= True open= []
   FU: U28: expose socket.resync() (drop clock samples, ping) so timer.js need not dispatch a synthetic 'online' event.
   FU: U25: add a per-viewer canManage (board overlay or by-code answer) so quick boards show share controls only to their creator.
   FU: UP1 demo: retro.config.update with maxVotes 0 should store unlimited (null), and expected maxVotes 0 should match an unlimited config. Today the demo stores 0 and treats an unlimited board as stale.
   FU: Integrator: U36 also adds FEATURES entries in web/src/retro/index.js. Merge both sets. U35's edits to web/styles/retro.css (U34-owned) also need review.
   FU: UF11/U29: retro-board/cards.spec.js 'the boot snapshot paints the board before the socket answers' already fails on main, because the entry's shared socket sends an empty-subs hello first. Re-run it once {socket} is passed to the retro mount.
   FU: Budget: the retro chunk is 27,827 of 32,768 B brotli. If U36 needs room, move the settings and share dialogs into lazy chunks (this needs new file paths).
   MINOR: web/src/retro/config.js: suppose someone else changes a field after the facilitator has edited that same field. Then refresh() keeps the facilitator's value but never compares it with the server, and save() sends expected = the old seen value. The se
   MINOR: The U35 styles are appended to web/styles/retro.css, which U34 owns. The palette selectors there were also extended to .retro-focus. Both are recorded in docs/decisions/U35.md, but U34 decision 4 only sanctions the index.js FEATURES edit, not the CSS
   MINOR: timer.js triggers clock resampling by dispatching a synthetic window 'online' event. This relies on how socket.js handles wake, and any future 'online' listener would also fire on every clock step.
   MINOR: focus.js puts columnId and cardId straight into querySelector attribute selectors without CSS.escape. An id containing a quote or backslash would throw. Ids come from the server, so this is not a security sink.
U36 done verified= True open= []
   FU: UF11: pass {socket: getSocket()} to the retro mount() so the board and team rooms share one connection (U36 decision contract 3).
   FU: Integrator: web/src/retro/index.js FEATURES line is edited by both U35 and U36; merge both entries.
   FU: U26: POST /api/teams/{id}/jira/issues must accept {summary, description} and answer {key}.
   MINOR: The Check is flaky when the machine is heavily loaded. My first run of `./build.sh fake-e2e "retro-actions/*.spec.js"` (load average about 65) failed 4 webkit tests in actions.spec.js. Each failed in openBoard (_actions.js:73), where `.retro-column-l
   MINOR: The branch edits web/src/retro/index.js, which U34 owns. U34 decision contract 4 explicitly asks U35/U36 to make this edit (the imports plus the FEATURES entries), so it is sanctioned. U35 changes the same line, though, so the two branches will confl
U42 done verified= True open= []
   FU: Integrator/U27: once SpotLoadCommand lands, SpotLoadTests can also call SpotLoadCommand.RunAsync against the same server-migrated output database.
   FU: Integrator: U42 edited two U38 files (src/Retroscope.Importer/Map/Common.cs: VerifyContext.Keys/Run; src/Retroscope.Importer/Source/ImportRunner.cs: passes them) and the importer test csproj (adds the aliased server reference). These need review at merge.
   FU: U46/RUNBOOK: retroscope-import verify now needs APP_KEYS and ANON_TAG_KEY. Run verify between run and cutover; after cutover, password changes make password-hashes fail by design.
   FU: Flaky wall-clock tests under heavy machine load (load average about 140): Integration LoginTests.Unknown_email_costs_as_much_as_a_wrong_password and Core AdfCapsTests.Hostile_shapes_convert_quickly each failed once. Neither is related to U42, and both passed on rerun.
   MINOR: The unit edits files it does not own under §k. It changes U38's src/Retroscope.Importer/Map/Common.cs (adds VerifyContext.Keys and VerifyContext.Run) and src/Retroscope.Importer/Source/ImportRunner.cs (sets them). It also adds an aliased Retroscope.S
   MINOR: The §k Check says `retroscope spot-load` runs in-proc. SpotLoadTests instead calls PokerStore, RetroStore and TeamStore directly, because U27's SpotLoadCommand is still a stub on main. The test does load and project every sampled room for an anonymou
   MINOR: The full `./build.sh test` stopped after one failure: the wall-clock test Integration LoginTests.Unknown_email_costs_as_much_as_a_wrong_password (321.9 ms vs 563.7 ms) failed while machine load was about 60. U42 does not touch server auth. The test p
UF9 done verified= True open= []
   FU: Integrator: RoomGateway and IResumeFill now live in Retroscope.Server.Realtime.Actors, and ActorsModule registers the IRoomGateway. U25/U27 branches still have the main-era copies (UnavailableRoomGateway in TransportModule, RoomGateway and IResumeFill in PokerRoom.cs); reconcile them in fix(integration) commits.
   FU: Integrator: ActorRegistry.TryPurgeAsync (used by U27 retention) can now return false even when the queues looked empty at the moment of the check; treat false as 'try again later'.
   FU: Integrator: rerun the end-to-end check after merging U25/U27. PokerRoom/RealServerTests.Two_signed_in_clients_see_each_others_votes_and_the_reveal runs two real WebSocket clients against the real server.
   FU: Optional: closing the remaining Twins case would need the server to remember a hash per q below the cursor; only worth doing if section c.6's acceptance of that case changes.
   FU: Core.Tests owner (optional): a Core test that JiraMetaLoaded on a deleted session is a no-op would pin down the guard that already exists in PokerDecider.Decide.
   MINOR: Connection.cs lets a SubFrame skip the rate limiter whenever its room is rate-refused. A client can repeat cmd (refused), then sub (always admitted, clears the refusal and costs the actor a snapshot or replay), then cmd again. That loop lets it reque
   MINOR: RoomGateway: when a client's sub is queued in a Peer's chain and the actor kicks that same Peer, Forget and Connection.Kick run first, and then the queued sub subscribes the Peer again. The actor then keeps a subscriber the connection no longer track
   MINOR: Twins is still an opt-in fault class (2 of seeds 1-2000 fail with Default,Twins). The spec says to make the 4409 classes default 'if feasible', and docs/decisions/UF9.md justifies the exception by citing §c.6. Seed 13 with Twins passes as the Check r
   MINOR: unit/UF9 was branched from main at 2f3f03a, and main has since gained 3f5a62c and 1e6c866 (build.sh RS_JOBS/nice changes, tmpfs Postgres). A test merge applies cleanly, but the build.sh sim-step edit has not been run against the newer build.sh.
UF10 done verified= True open= []
   FU: RecheckAccessAsync in Realtime/Actors (UF9's paths) still needs checking so that its catch-all does not swallow OperationCanceledException.
   MINOR: PUT /api/me/avatar inserts the blob (BlobsRepo.InsertAvatarAsync) and then calls UsersRepo.SetAvatarAsync as a separate statement. If the request is aborted or the server fails between the two, an avatar blob is left that nothing references. BlobsRep
   MINOR: In MagicConsumeAsync, the session-holder branch decides whether to verify from caller.EmailVerified, which comes from the cached principal. If that cache is stale and says verified while the row is not, the verify write and its auth.verify audit row 
UF11 done verified= True open= []
   FU: U30/U31: accept app.socket in mount() (team/index.js, poker/index.js) and add those modules to SHARED_SOCKET in entry.js. Until then, in-app navigation between a team or poker page and a board keeps two sockets in one tab, which fight over the cid with 4409. This was already true before UF11.
   FU: U34: the board can put its title, code and guest line into the header with setHeaderCenter() instead of relying on the document.title copy, and could drop its duplicate on-page title.
   FU: U35/U36: add their FEATURES entries to web/src/retro/index.js. The shared socket is already passed to the retro mount().
   FU: U48: the plain fake server (tests/web-fake-server/server.js and shell.js) does not serve the root files or render the shell's icon, manifest and theme-color links. The demo server serves the root files.
   FU: U31: the poker visual baselines exist only as chromium-darwin files, so a Linux CI run has no baseline. This predates UF11.
   FU: Watch for a rare Firefox failure seen once under full parallel load: a click on the logo link did not navigate from a board (header.spec.js, centre-slot test). It did not reproduce in repeated runs.
   MINOR: Spec item 3 says to fill FEATURES per docs/decisions/U34.md, but UF11 left FEATURES in web/src/retro/index.js untouched. Its reason is that U34 owns that file and U35/U36, running in this wave, own their entries. The decision is recorded in docs/deci
   MINOR: Spec item 2 asks for the root files through the fake and demo servers. Only the demo server (tests/web-fake-server/demo/routes.js) serves favicon.ico, manifest.webmanifest and robots.txt. The plain fake server (U48's server.js and shell.js) does not,
   MINOR: Ownership deviations: the U31 poker-table visual baselines (8 chromium-darwin PNGs) were rebaselined. A new tests/e2e/fake/team/uploads.spec.js was added in U30's spec directory. Both are documented, both are a direct result of owned changes (docked 
   MINOR: Only retro/index.js gets the shared socket. Team and poker still create their own sockets with createSocket, so in-app navigation between a team or poker page and a board can still hold two sockets with the same cid (4409). This predates UF11 and is 
=== MERGE ===
All 12 verified units from wave 5 are merged into main, and the final `./build.sh test` passes. Making the merge work needed four integration fixes, a merge-conflict resolution and two test-only changes. The demo preview on port 5199 is still running, no test containers are left over, and nothing was pushed.

**Merged** (`git merge --no-ff`, in unit order): U24, U25, U26, U27, U32, U33, U35, U36, U42, UF9, UF10, UF11.

**Not merged:** none. All 12 were verified.

**Merge conflicts resolved:**
- `web/src/poker/index.js` (U32 and U33): I kept both sides. FEATURES is now `[createStrip, createSpotlight, createObserverToggle, createSettings, jiraFeature, createHistory, createMobile]`.
- `web/src/retro/index.js` (U35 and U36): I kept U35's five entries followed by U36's three.
- The 8 poker-table visual baseline images (U32 and UF11 had both regenerated them): I took main's copies during the merge, then regenerated them from the merged code in 0180cb8.
- `RetroRoom.cs` (U25 and UF9) merged with no conflict. Both sides are present: UF9's AddRoom/IsCreate and U25's `BoardDeleted => MemoryOnly` and IsDeleted.

**Integration fixes:**
- **9a65bae**: U24 and U25 had both built `GET /api/teams/{id}/action-items` and both declared a `ListCursor` type, so the server did not compile. I kept U24's route in TeamsEndpoints and removed U25's copy. I switched U24's route to U25's microsecond cursor, because U24's millisecond cursor could skip rows created in the same millisecond. This is recorded in `docs/decisions/U25.md` (decision 11).
- **1a819eb** (test only): U25's list test failed with a 404 because its test host does not map TeamsEndpoints. I moved that coverage into `TeamHttp/TeamListsTests` and added a test for paging within a single millisecond.
- **78edcec**: every U36 retro-actions test failed (22 on Chromium). UF11 made `getSocket()` asynchronous, but U36's `actions.js` called it as if it returned the socket directly. The board now passes its own socket to features (this touches U34's `board.js`), and the team link uses it. That also stops a second socket from sharing the same client id.
- **dd52175**: one drag test failed on Firefox every time. With U32's strip and UF11's header together, the test released the mouse 1.5 px below the viewport, and Firefox never delivers that release. The test now keeps the release point inside the viewport.
- **055ca19** (test only): the UF9 actor test `Retiring_actor_loses_no_message_posted_while_it_closes` failed about 3 times in 15 when run alone, because a fast run could finish before the actor ever shut down. It now keeps writing until that has happened. It then passed 25 out of 25 runs.

**Final results on main:**
- **`./build.sh test`**: exit 0.
  - .NET: 3103 of 3103 passed.
  - Simulation: 2000 of 2000 seeds passed, in strict mode.
  - Web unit tests: 347 of 347 passed.
  - Golden vectors: verified.
- **`./build.sh aot-smoke`**: passed, including the WebSocket smoke test and the slimmed-down Docker image. It ran before the last two fixes, which changed only JavaScript and e2e test files, so no C# changed after it.
- **Two-voter check against the real server:** passed. I ran the compiled server binary against a throwaway Postgres, with two signed-in users on separate WebSocket connections. Ann created the session and a round. Each user saw the other's locked vote but not its value. Both saw the reveal (Ann 5, Bob 8, winner 8). The server logged no errors.
- **All browser tests against the fake server (`./build.sh fake-e2e "**/*.spec.js"`):** 1492 passed and 3 failed before the drag fix. The drag test was fixed and now passes on all 4 browsers. The other two failures were on WebKit (`retro-stages/timer.spec.js:72` and `team/actions.spec.js:159`), and each passed 5 out of 5 reruns, so they look load-related. I did not rerun the whole browser suite after the last two fixes, but the retro tests (105 of 105 on Chromium) and the drag test were rerun.

All 12 worktrees are removed and all 12 `unit/*` branches are deleted. The two-voter script is at `<scratch>/e2e-ws/two-voters.cjs`.

**Follow-ups:**
1. **Ownership transfer (U24 and U27)** takes two separate steps, so a crash between them leaves the team with no owner. A single team action that does both at once would fix both units.
2. **MembershipCache** does not filter out soft-deleted teams, so a reload lets a deleted team's members back in (U23/UF9 area).
3. **U27 cleanup jobs:**
   - The retention and team-purge deletes check leadership only at the start of a pass and are not fenced, so a server that loses leadership mid-pass keeps deleting.
   - If the 500 oldest idle quick rooms all have live subscribers, every pass picks the same rooms and nothing else is purged.
   - The team-purge query on recent_activity matches a concatenated key, so it can't use the index.
4. **U26:** deleting a Jira link does not check that the link belongs to the issue being viewed. FakeJira also ignores JQL, so the browse filters are never really tested.
5. **UF9:**
   - A client can repeat "subscribe" to request snapshots beyond its rate limit.
   - A subscribe queued while the same connection is being kicked can leave a subscription the connection no longer tracks.
   - The "Twins" fault class still fails 2 of 2000 seeds, so it stays off by default.
6. **UF10:**
   - Avatar upload writes the blob and the user's avatar link in two separate statements, which can leave an orphaned avatar blob that nothing cleans up.
   - The magic-link verify check reads possibly stale cached data.
7. **UF11:**
   - Team and poker pages still open their own sockets, so moving between them and a board can produce two sockets with the same client id.
   - The plain fake server does not serve the favicon, manifest or robots files.
   - Visual baselines exist for macOS Chromium only. The poker-table visual test should pin a timezone before Linux baselines are generated, because the "Revealed by X · HH:MM" text depends on the local timezone.
8. **U32/U33:**
   - Strip chips still use a tag icon instead of the issue-type glyph.
   - The left Jira panel cannot be resized.
   - The jira-drawer bundle is at 22,311 of its 22,528-byte budget.
9. **U35:** the settings dialog sends a save it already knows will be rejected and then waits 8 s. The timer forces a clock resync by firing a fake `online` event. `focus.js` puts ids into selectors without `CSS.escape`.
10. **U42:** SpotLoadTests should now call U27's `SpotLoadCommand`. The runbook should say that `retroscope-import verify` needs APP_KEYS and ANON_TAG_KEY. The integrator still needs to accept U42's edits to `Common.cs`, `ImportRunner.cs` and the Importer test project file. I did not review them in detail.
11. **Tests that fail under heavy machine load:**
    - `AdfCapsTests.Hostile_shapes_convert_quickly`
    - `MarkupJiraLengthTimingTests`
    - `LoginTests.Unknown_email_costs_as_much_as_a_wrong_password`
    - U36's `openBoard` first-paint timeout
    - The webkit `team/actions.spec.js:159` failure was a CSP inline-style violation that did not reproduce in 5 reruns. It is worth checking that nothing sets inline styles.
12. **Unowned-file edits:** U32, U33, U35, U36, UF11 and my fixes edit files other units own (`poker/index.js`, `retro/index.js`, `retro.css`, the U31 baselines, and `board.js` in 78edcec). The owning units should look these over.
```

## Status after UX18

`docs/followups/UX18.md` closes four items above: the U27 MINOR and the second bullet of the closing follow-up 3, on
retention picking the same idle quick rooms (the quick-room purge is gone); the U35 item on a per-viewer `canManage`
for quick boards' share controls (Share is "Copy link"); and the UF11 item on putting the board's title into the header
(UX18 decision 12).
