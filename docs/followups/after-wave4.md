# Open follow-ups after wave 4

Raw integrator and verifier output, to be split into follow-up units when wave 5 starts.

```
U15 done verified= True open= []
   FU: U13/U14 with U28 (known issue lost-retryable-nack): a command refused with server_busy (or rate_limited) can lose its nack when the socket dies, while a later pipelined command of the same cid commits. The resend is acked dup and the intent is silently lost (first seen at seed 819). Possible fixes: refuse every later command of that cid on the conn
   FU: U28/U14 (4409 keeps q values): a tab that reconnects while the server still holds its old socket gets 4409. It then re-mints its cid and resends its outbox under the new cid, so entries that committed under the old cid are decided again (double application, or a final nack for a committed intent). Reproduce with --seed 51 --faults Default,StaleSock
   FU: U28: entries restored from an older copy of sessionStorage are renumbered at sync even though they may have been sent and committed since, so they are applied again (--seed 697 --faults Default,StaleStorage). Mark entries loaded from storage as possibly sent.
   FU: U21: SpotlightSettled must write spotlight_round_id and spotlight_client_id from the event; PokerApply only clears Dirty.
   FU: U23: loading the team room must refresh the membership cache and post RosterChanged and AccessChanged to the live poker and board rooms. Otherwise a member change whose commit outcome was unknown is never fanned out.
   FU: U15 follow-up: add a retro room (anonymous tags, readiness, timer expired once) to the simulation.
   FU: Integrator: the simulation reads RoomActor's private fields _internal, _external, _internalReady and _externalReady by reflection. If UF7 renames them, the harness fails at once with a clear message and tests/Retroscope.Sim/SimProcess.cs needs updating.
   MINOR: The Check reports green while a real §j invariant violation ('no unacked intent lost to a transient failure') is downgraded to a 'known open issue' (lost-retryable-nack, 9 of 2000 seeds; I reproduced it with --seed 819). The filter is narrow: it need
   MINOR: tests/Retroscope.Sim/SimProcess.cs uses reflection on RoomActor's private fields (_internal, _external, _internalReady, _externalReady) to detect quiescence. It is test-only and not AOT-published, so the AOT rule does not apply, but it is brittle aga
   MINOR: DESIGN §j names FakeTimeProvider for the simulation, but the harness uses its own SimClock and drops the TimeProvider.Testing reference. The deviation is reasoned and recorded in docs/decisions/U15.md (decision 1), and determinism was verified (300-s
   MINOR: Only the poker and team rooms are simulated; retro-room invariants (anonymous tags, readiness, timer expired once) are not covered. The §k Check items do not require them, and this is recorded as a follow-up.
U17 done verified= True open= []
   FU: U24: send invite mail by calling EmailBudget.TryReserve(email, ip, sender: inviter), then putting Outbox.Insert(Templates.TeamInvite(teamName, link).To(email), now) in the invite's own transaction, then calling Outbox.Kick() after commit.
   FU: Web unit owning screens/auth.js: /auth/magic reads #t, POSTs /api/auth/magic/preview to show 'Sign in as a•••@x.com?', and POSTs /api/auth/magic/consume only on click. /auth/reset POSTs {token,password} to reset/consume. /auth/verify POSTs verify/consume (401 means show 'Sign in to finish verifying'; 403 other_account means signed in as someone els
   FU: Owner of deploy/compose.test.yml (U43/U48): mailpit needs SMTP_SECURITY=none plus SMTP_HOST/SMTP_PORT/SMTP_FROM; the RUNBOOK should document SMTP_SECURITY.
   FU: U27: show EmailBudget.Dropped and outbox depth in /api/admin/stats. RetentionJob deletes email_outbox rows with null body older than 7 days; TokenSweepJob deletes expired auth_tokens.
   FU: Integrator: U17 added 4 [JsonSerializable] lines and a using to src/Retroscope.Server/Json/AuthJsonContext.cs (U16-owned); watch for merge conflicts there.
   FU: Observation: aot-smoke reported 2.28 GB peak RSS for the server AOT publish on osx-arm64, up from the 1.2-1.3 GB U0 recorded. Images are built in CI, but U43 may want to track it.
   MINOR: Magic consume always calls AccountClaims.ClaimAsync, even when the request already carries a session of the same (unverified) user. §e applies the takeover rule only to proof 'by someone other than the current session holder'. As written, an honest u
   MINOR: An edit outside owned paths: 4 [JsonSerializable] lines and a using were added to the U16-owned src/Retroscope.Server/Json/AuthJsonContext.cs. It is needed because U0's scaffold test requires every context to be chained in the frozen Program.cs, and 
   MINOR: Reset consume does not check whether the account is disabled, so a reset link already issued can still set a password on an account disabled since. The account still cannot sign in, so there is no security impact.
   MINOR: Verification was partial: I did not re-run ./build.sh aot-smoke and did not reproduce the builder's scratch AOT console for STARTTLS. AOT safety rests on code review: the diff has no reflection, every JSON type goes through source-generated contexts,
U18 done verified= True open= []
   FU: U16/U17 or the integrator: move the external_identities delete and its auth.unlink audit into UsersRepo.Claim's transaction inside AccountClaims.ClaimAsync. That makes it atomic with verification and also covers the magic-link claim (U17), which currently does not unlink anything. OidcAccounts.UnlinkBeforeClaimAsync can then be removed.
   FU: Remaining race until that move: a still-signed-in squatter who completes an explicit link in the instant between the unlink transaction and the ClaimAsync transaction keeps that link.
   MINOR: Flaky full-suite run, not caused by U18. On the first ./build.sh test run, the U0-owned test Schema/PostgresFixtureTests.More_databases_than_postgres_has_connection_slots_can_be_created failed with 'Expected 0, Actual 1' (a lingering pg_stat_activity
   MINOR: IdTokenValidator applies the 5-minute skew to exp as well as iat: a token is accepted until exp + 5 min. Section e only names the ±5 min tolerance for iat. This is defensible under OIDC Core 3.1.3.7 item 9, which allows a small leeway, but it is not 
   MINOR: OidcAccounts.UnlinkBeforeClaimAsync runs in a different transaction from AccountClaims.ClaimAsync, so there is a small race window. It also removes every external identity on the unverified account, which can include identities the real owner linked 
U21 done verified= True open= []
   FU: U31/integrator: titles arrive only as x jira.meta frames sent right after a snap or replay. Treat those as the snapshot's meta, or add jiraMeta to PokerSnapshot in a Core follow-up.
   FU: Integrator (UF7): give IRoomPeer typed hooks (OnKick, OnSnapshotSent) or a frame-kind tag so RoomGateway stops detecting kick, snap and replay frames by their leading bytes. Move RoomGateway under Realtime/Actors.
   FU: U23: register IAccessView with AddSingleton or Replace so it overrides the deny-all NoTeamAccess, which is added with TryAdd.
   MINOR: PokerJiraLoop.Track runs after every message the actor handles. While a refill JiraMetaLoaded is still queued, any other message re-detects the same parent mismatch and posts the same refill again. The extra copies are harmless, because DecideJiraMet
   MINOR: DecideJiraMeta does not check state.Deleted. A meta fetch or refill that arrives after SessionDeleted can still produce RoundUpdated events, and the store plans UPDATE statements for them. Those statements match no rows.
   MINOR: The tests at tests/Retroscope.Integration.Tests/PokerRoom/** and the generic RoomGateway inside PokerRoom.cs are not listed among U21's owned paths in §k. Both are justified in docs/decisions/U21.md (the U13/UF4 precedent and the carried follow-up), 
U22 done verified= True open= []
   FU: U13/UF7: nothing implements IRoomGateway yet (UnavailableRoomGateway is still registered), so a real WebSocket cannot reach board rooms. The RetroRoom tests drive actors through ActorRegistry instead.
   FU: U25: write visibility, password_hash and password_set_at (and unlock grants) in its own transaction, then AskAsync(RetroInput.AccessSet), then registry.AccessChanged(room). Use RetroRoom.AddActionItemCommentAsync for the action-item comment POST.
   FU: U8/U25: board deletion has no RetroInput and the binding's IsDeleted cannot see it. Either add a deletion input or use TryPurgeAsync after the soft delete.
   FU: U23: register IAccessView, IMembershipLookup and ITeamNotices, and post MembersChanged/TeamSettingsChanged to live boards. After that, the integrator can switch RetroRoom.AddServices to services.AddRoom.
   FU: Integrator: merge UF6's idempotent-create rule in Core/Retro; RetroStore does not depend on it. `./build.sh aot-smoke` was not run for this unit.
   MINOR: The room is registered with services.AddSingleton(RoomType.Of(...)) and not with services.AddRoom(sp => binding, sp => store), which the carried follow-up asked for. The reason given holds up: there is no IAccessView implementation anywhere in src, a
   MINOR: The NoAccess fallback denies every role and grant, so it is default-deny. But if U23 never registers IAccessView, production would quietly run with team members unable to reach team-only boards, and nothing would be logged or fail.
   MINOR: The carried follow-up said rooms should implement IRoomGateway fan-out. U22 deferred this to U13/UF7. IRoomGateway's own doc says the actor runtime implements it, and U22 does not own Actors/Transport, so the deferral is reasonable. Still, no real We
   MINOR: RetroStore's post-commit ActionItemCommentsChanged path makes an extra DB round trip (CancellationToken.None) inside the actor's commit path whenever a comment is added or deleted on a team board. This adds latency to the single-writer loop.
U23 done verified= True open= []
   FU: UF7/integrator: still no IRoomGateway implementation (UnavailableRoomGateway is registered). It should adapt IClientConnection to IRoomPeer for all room kinds.
   FU: U21/U22: call IMembershipLookup.LoadAsync(team) in your store's LoadAsync. The team actor posts RosterChanged (poker), RetroInput.MembersChanged (boards), PointDescriptionsChanged, RetroInput.TeamSettingsChanged and AccessChanged to live rooms after membership or settings commits.
   FU: U24: send MemberAdded, MemberRoleSet and MemberRemoved with AskAsync<TeamState, TeamInput>, not Post, because Post drops changes for a team actor that is not live. In an ownership transfer, demote the old owner before promoting the new one (team_members_one_owner_uq is not deferrable). Ask SettingsChanged after writing the settings. Membership audi
   FU: Users hard-deleted outside the team actor (ON DELETE CASCADE on team_members) leave a stale cache entry until the team actor reloads. Account-deletion code should post MemberRemoved or call MembershipCache.Forget.
   MINOR: In TeamRoom.cs, TeamFanOut.CommitAsync returns early unless the commit carries a roster or SettingsChanged, so a committed TeamDeleted fans out nothing. Only the team room's own subscribers are kicked, through IsDeleted. §c.2 says team deletion posts
   MINOR: MembershipCache.RoleIn and HasCurrentGrant do blocking synchronous Npgsql reads (ExecuteReader) when the cache misses. Actors call them at decide time on their threads. RoleIn's fallback runs once per team, but HasCurrentGrant reads the row again on 
   MINOR: The carried follow-up says rooms implement IRoomGateway fan-out, but U23 did not implement it, and UnavailableRoomGateway is still registered. The interface lives in Realtime/Transport, which UF7 owns this wave, so U23 cannot add it within its owned 
U29 done verified= True open= []
   FU: U10: WebKit (desktop and mobile) reports a style-src-attr CSP violation whenever a native <select> (`ui/select.js`) is attached, so any screen that uses `createSelect` fails the WebKit fake-e2e projects.
   FU: U10: a `shell.css` mapped in `ROUTE_STYLES` for `screens/*.js` would let the shell's inline CSSOM layout move into a stylesheet.
   FU: U48: Playwright WebKit screenshots inject an inline <style> that the CSP reports as style-src-elem. `tests/e2e/fake/shell/theme.spec.js` filters those reports; the harness could do this for visual-baseline specs (U31, U34).
   FU: U17: map `POST /api/auth/magic/preview {token} -> {email masked}`. `magic/consume` should return the /api/me body or 204, and `reset/consume` / `verify/consume` should use invalid_token (or 404/410) for dead links, as screens/auth.js expects.
   FU: U18: `POST /api/me/identities/{provider}/link` should answer `{url}`; OIDC failures should redirect to `/login?error=<code>` (account_exists, tenant_not_allowed, provider_disabled).
   FU: U24/U25/U27: align response shapes with the provisional ones in docs/decisions/U29.md (dashboard, teams, invites accept `{teamId}` and its error codes, me/mentions, me/endorsements, admin users/teams/stats), or ask U29's owner to adjust.
   FU: U30/U31/U34: import `getSocket`, `session` and `navigate` from `web/src/screens/app.js`. The Reconnecting and 4401 notices are already drawn there.
   MINOR: web/src/screens/auth.js calls POST /api/auth/magic/preview, a route that is not in the §d route table. §e only says a 'preview POST' exists. Until U17 maps that exact route, the magic-link page fails against the real server.
   MINOR: Several response shapes are provisional guesses for routes owned by later server units: dashboard, teams, invites/accept {teamId}, me/mentions and /seen, me/endorsements, admin users/teams/stats, and the identities link {url}. The fake-server tests o
   MINOR: getSocket() reads pid when the socket is first created. If a guest socket already exists and the visitor then signs in on the same tab, the socket keeps the guest pid. Only signOut() resets the socket.
U30 done verified= True open= []
   FU: U48: filter WebKit's native <select> style-src-attr CSP report in _harness/fixtures.js so every screen benefits; then drop the copy in tests/e2e/fake/team/_team.js
   FU: U10/U0: the editor budget line counts parse/render/doc in its closure (12.2 KB vs 8 KB); U34/U33 importing markup/editor.js directly will fail the build — re-measure or rebaseline
   FU: Integrator: replace teamSocket() in web/src/team/index.js with U29's screens/app.js getSocket().start(), and the hidden-link navigate with app.js navigate
   FU: U24/U25/U26: implement the HTTP shapes recorded in docs/decisions/U30.md or change the screens and specs together
   MINOR: Upload error mapping in web/src/team/comments.js keys only on the problem+json code. If Kestrel rejects a body over MaxRequestBodySize (2 MiB, §g) before the endpoint runs, the response may not carry the 'too_large' problem body, so the code becomes 
   MINOR: The carried follow-up asks U30 to handle the 409 board_quota, 413, 415 and 507 upload errors, but no fake e2e spec covers that mapping. The §k Check does not list it.
   MINOR: Most HTTP response shapes, and POST /jira/meta, are defined by U30 itself in docs/decisions/U30.md rather than by §d, so the screens depend on U24/U25/U26 adopting them. The decision record states this, but it is an integration risk.
U31 done verified= True open= []
   FU: U48/integrator: change tests/e2e/fake/_harness/shell.spec.js so it no longer expects '#app' to read 'poker'. Use a marker such as #app having children, or .neotro / .nt-join being present. Until then that spec fails on all four projects after the merge.
   FU: U32: read view.heldByThisTab for bound-tab rebinding (see docs/decisions/U31.md contract 3), and treat a not_holder nack on move as unbound.
   FU: U29: expose one shared socket accessor and switch socketFor() in web/src/poker/index.js to it.
   MINOR: Merging U31 will break U48's tests/e2e/fake/_harness/shell.spec.js. That spec expects #app to read 'poker' on /poker/FAKE123456, which was the old stub's text; the real screen now shows the guest join form there. U31 cannot edit that file because it 
   MINOR: tests/web-unit/reducers.test.js has no owner in §k, and U31 created it. The choice is justified and recorded as decision 1 in docs/decisions/U31.md. U34 (same wave) will also want retro transcripts in this file, so the merge may conflict.
   MINOR: heldByThisTab can be wrong in one edge case. If another tab of mine force-takes the spotlight or rebinds it on the same round while this tab is bound, the event looks like a repair, so this tab keeps heldByThisTab true until the next snapshot. It is 
U34 done verified= True open= []
   FU: U29: pass {socket} as the third argument of mount() in entry.js, and load socket/outbox/room in the entry (this should bring the retro chunk down from 26.7 KB)
   FU: U25/U11: write boot.room as {r, access?, e, s, d, o} for /retro/:code shell and /api/boot
   FU: U10: measure lazy chunks net of what their importer already loaded; then card.js and composer can import markup/editor.js directly and the 'editor' budget line applies again (U33 hits the same issue with the jira drawer)
   FU: U10: ui/select.js triggers a WebKit style-src-attr CSP violation (any <select>); U35/U36 should avoid <select>
   FU: U35/U36: add their feature import and one FEATURES entry to web/src/retro/index.js (a one-line change in a U34-owned file; request it from U34 or the integrator)
   FU: One unexplained firefox failure in about 1,100 repeated runs (votes.spec.js 'voting follows the stage…'); not reproduced in 20 targeted repeats
   MINOR: The editor ships inside a lazy 'composer' chunk instead of its own 'editor' chunk, so the 8 KB editor line in web/budgets.json is no longer checked for the retro board. This differs from the carried follow-up, which asks for the editor in its own chu
   MINOR: web/src/retro/index.js passes an empty FEATURES array, and entry.js does not yet pass {socket}. Until U29, U35 and U36 land, the retro module opens its own socket and has no stage, timer or action-item features. The design intends this split, but the
   MINOR: The builder reports one unexplained firefox failure in about 1,100 runs, in votes.spec.js 'voting follows the stage…'. It did not happen in this verification run, which passed 177 and skipped 27.
UF6 done verified= True open= []
   FU: Known gap: a single poker.round.create that filled the placeholder round keeps the placeholder's id, so if it is resent after a cid_reuse renumbering it creates a second round with the same ticket. Fixing this needs a persisted record of which create filled the placeholder (a new event or column), which UF6 does not own.
   FU: Integrator: the Core deciders now return NoOp for same-payload repeated creates. This affects anything that expected 'invalid' for those cases, such as U21/U22/U23 room tests or Sim scenarios. Optionally, UF7/U13 could ack repeated creates with dup:true.
   MINOR: In my reruns, ./build.sh test on unit/UF6 exited 2 three times. Each time the only failure (1 of 2664) was Retroscope.Integration.Tests.Schema.PostgresFixtureTests.More_databases_than_postgres_has_connection_slots_can_be_created (pg_stat_activity cou
   MINOR: supersede.js gives keys to retro.board.rename (precondition `expected`), poker.round.setTicket and retro.card.edit (precondition `baseVersion`, which states the value being replaced). The table's own rationale says such overwrites should have no key.
   MINOR: The spec says a repeated create 'acks as dup', but Core returns NoOp, so the client gets a plain ack. This is recorded in docs/decisions/UF6.md, citing DESIGN §g.4 (dup acks are handled the same way), and producing dup belongs to UF7's actor code. Al
UF7 done verified= True open= []
   FU: Integrator: UF7 changed ActorRegistry internals that U21-U23 use (Retired -> Retire(actor, close), GetOrStartAsync can throw IncarnationTimeoutException while a key is being purged, AskAsync can return Unavailable with Code=server_busy). Rooms calling AskAsync/QueryAsync should map Unavailable/server_busy and RoomUnavailableException to a retryable
   FU: U25: RoomView.SnapFrameAsync on a not-found view still throws InvalidOperationException; check view.Found first (or CanSubscribe, which is false for not-found views).
   FU: U17: signup now writes an audit row in the signup statement itself; IAccountMail.SignedUpAsync/AlreadyRegisteredAsync are still called after it, so their outbox writes come after an equal-cost commit on both paths.
   FU: U27/admin: audit_log gains the actions 'auth.signup' and 'auth.signup.taken' (meta: email_hmac, ip).
   FU: Pre-existing LivenessTests.A_silent_client_keeps_working_across_many_ping_intervals (200 ms ping interval) failed once while the machine load average was about 20-29 from parallel agents; it passed in 5 repeat runs of Category=Transport and in both full Check reruns. Consider widening its timings if it flakes in CI.
   MINOR: Item 7 asks for HSTS on HTML responses when PUBLIC_URL is https. SecurityHeaders sends it on every response, so it reaches beyond HTML. This is valid under RFC 6797 and is recorded in docs/decisions/UF7.md, but it differs from the follow-up's wording
   MINOR: The signup SQL (principals + users insert, now with an audit CTE) is a second copy of UsersRepo.CreateAsync, placed in AuthEndpoints.cs because UsersRepo.cs is not a UF7 path. If either insert changes later (new user columns, defaults), signup can dr
   MINOR: In RoomActor.RecheckAccessAsync, the cursor-load catch is catch (Exception), so it also catches OperationCanceledException during shutdown. That logs a 'Poisoned' line and restarts the peer, which is noisy but harmless.
   MINOR: SignupParityTests.A_taken_address_costs_as_much_time_as_a_new_one takes the median of 5 samples per path with a 0.6x tolerance. The builder says it passed against the old code, so as a regression guard it is weak. The command-parity test is the one t
UF8 done verified= True open= []
   FU: A leftover entry 'WIP on unit/UF8: 83dcfb7' (d3bbb515) is still in the stash list shared by all worktrees. Its content is committed on unit/UF8, so the user or integrator can safely drop it, and no one else should pop it.
   FU: Workflow hazard: git stash is shared across worktrees, so a unit that stashes while another does can pop the wrong entry. Future units should check the old code in a separate worktree instead of using git stash.
   FU: U42 / verify: two new report keys exist: the skip reason malformed_identity_data on auth.identities, and the counter poker.nullAveragePoints with the finding pokerNullAveragePoints.
   FU: Core AdfCapsTests.Hostile_shapes_convert_quickly (a 2 s wall-clock limit) failed once while other units were running and passed on the rerun. It may need the best-of-three timing approach UF1 used for the markup timing tests.
   MINOR: UF8 edited src/Retroscope.Importer/Html/BoardImages.cs and Html/DocBuilder.cs. Those files are outside its listed owned paths (Map/**, the test folders, decisions U39-U41). The UF8 spec requires both edits (items 6 and 7). The edits are small and rec
   MINOR: A leftover entry, 'stash@{0}: WIP on unit/UF8: 83dcfb7', is still in the stash list that every worktree shares. Its content is already committed on unit/UF8. If another unit runs a blind 'git stash pop', the UF8 changes would be applied to that unit'
   MINOR: The unified ColumnPalette.FromTailwind changed template behaviour in edge cases. The old template rule picked the first bg- class (including 'dark:bg-'). The new rule takes the first bg-, border- or text- class and ignores 'dark:'-prefixed ones. Ever
=== MERGE ===
**Wave 4 integration on main (`<repo>`, HEAD 5ec2ad2)**

**Merged units:** all 13 verified units, each with `git merge --no-ff` in this order: U15, U17, U18, U21, U22, U23, U29, U30, U31, U34, UF6, UF7, UF8. There were no merge conflicts. U31 and U34 both touched `tests/web-unit/reducers.test.js`, and it merged cleanly.

**Unmerged units:** none. All units were reported done and verified.

**Integration fixes (separate commits):**
1. `2af45c8 fix(integration): stop pooling owner connections to per-test databases`. After the merge, the integration tests failed every time: 19 failures with "53300 too many clients", in TeamRoomTests and PasswordChangeTests. Each test database left one pooled owner connection idle, and with this wave's extra tests that used up `max_connections`. I watched the connection counts during a run and owner backends reached 85 or more. The fix sets `Pooling = false` on owner connection strings in `tests/Retroscope.Integration.Tests/Schema/PostgresFixture.cs` and keeps pooling on for the app role. It also makes `PostgresFixtureTests.More_databases_than_postgres_has_connection_slots_can_be_created` poll for up to 5 s for backends to exit. That test is the flaky one U18 and UF6 reported.
2. `afc878d fix(integration): raise the mentions chunk baseline to 12 KB`. `web/budgets.json` set mentions to 10240 bytes, and merged main measured 12083 bytes brotli, which is 18 % over and fails 13 web unit tests. The mentions screen code itself did not change. After the merge, esbuild's shared markup chunk (parse/render/doc) holds everything any importer uses from it, and `build.mjs` counts that whole chunk against mentions (9453 bytes on unit/U29). DESIGN §g.7 allows changing the baseline in an explicit commit, and that commit message records the cause.
3. `5ec2ad2 fix(integration): check the shell spec against the real poker screen`. This is the U31 follow-up: `tests/e2e/fake/_harness/shell.spec.js` now checks for `.neotro` or `.nt-join` instead of the old stub text. `./build.sh fake-e2e "_harness/shell.spec.js"` passed 15 of 15.

**Final suite status:**
- `./build.sh test` on afc878d exited 0:
  - .NET: 2892 total, 2892 passed, 0 failed, 0 skipped. The Integration project alone is 953 of 953.
  - Simulation: 2000 of 2000 seeds passed, with real seed counts. The known open issue lost-retryable-nack still occurs in 9 seeds, first at seed 819.
  - Web unit tests: 315 of 315 passed.
  - Golden vectors: verified against their recorded hashes only, because no original repository path was given.
- 5ec2ad2 changes only the shell e2e spec, which `./build.sh test` does not run. That spec was run on its own (fix 3).
- `./build.sh aot-smoke` exited 0 (C# under `src/` changed). The AOT publish and every probe passed: the npgsql features, bcrypt, SmtpClient construction, signup and login over `/ws` with the native binary, and `/healthz` from the chiseled image. It did not test sending mail over STARTTLS.
- No test containers are left running.

**Cleanup:**
- All 13 worktrees under `<repo>-wt` are removed and all 13 `unit/*` branches are deleted. Only main is left.
- I dropped the leftover `stash@{0}` (WIP on unit/UF8) after checking that all 7 of its files match main.
- Nothing was pushed.

**Follow-ups:**
- **U13/U14/U28:** fix lost-retryable-nack. After that, turn on `--strict` in `build.sh` or delete the KnownIssues entry.
- **U10:**
  - Measure each lazy chunk net of shared or importer-loaded code, then put mentions back to 10240.
  - Let `card.js` and `composer.js` import `markup/editor.js` directly again (U34).
  - The entry chunk is 12586 bytes against 12288, a warning only (+2.4 %).
- **UF7/U15:** expose a quiescence hook on RoomActor instead of the reflection in `tests/Retroscope.Sim/SimProcess.cs`. Add a simulated retro room now that U22 has landed.
- **IRoomGateway:** register a real implementation backed by ActorRegistry for all room kinds. UnavailableRoomGateway is still registered, so no real WebSocket reaches a room yet (from U22 and U23). Then switch `RetroRoom.AddServices` to `services.AddRoom` and remove the NoAccess fallback, now that U23 registers IAccessView.
- **U23:** the TeamDeleted commit fans out nothing. Post AccessChanged to the team's live sessions and boards, or correct decision 3.
- **U17:**
  - MagicConsumeAsync should skip the account claim when the caller is the same principal.
  - Reset consume should refuse disabled accounts.
  - The edit to the U16-owned `AuthJsonContext.cs` is accepted.
- **U18/U16:**
  - Move the external_identities unlink into the UsersRepo.Claim transaction.
  - Record the leeway on exp in `docs/decisions/U18.md`.
- **UF7/U16:** move SignupSql into UsersRepo so signup and CreateAsync share one column list. Keep OperationCanceledException out of the RecheckAccessAsync catch.
- **U21:**
  - Stop the duplicate Jira refill posts.
  - Add a Deleted guard to DecideJiraMeta or Track.
  - Move RoomGateway under Realtime/Actors.
- **U22:** remove the post-commit database round trip for ActionItemCommentsChanged.
- **UF6/U28:** a keyed overwrite's baseVersion or expected value must come from server-confirmed state, or remove the keys from rename and setTicket. Optionally map a NoOp create to `ack{dup:true}`.
- **Route and response shapes:** U17 should map `POST /api/auth/magic/preview`. U24, U25, U26, U27 and U18 should match the response shapes in `docs/decisions/U29.md` and `docs/decisions/U30.md`.
- **U30:** map upload errors from the HTTP status too, and add a fake e2e case for 413 and 409.
- **U29, U35, U36:** pass `{socket}` to retro `mount()` and fill FEATURES (`docs/decisions/U34.md`).
- **U8 file ownership:** accept that UF8 edited `Html/BoardImages.cs` and `Html/DocBuilder.cs`, as its spec required.
- **Firefox flake:** watch for the U34 failure in `votes.spec.js` in CI.
```

## Observed in the preview walkthrough (orchestrator)

- `/favicon.ico`, `/manifest.webmanifest` and `/robots.txt` return 404 in the preview. U37 owns the assets and U11 owns static serving. The real server must serve them too.
- The team page and retro board have no persistent app header (logo, team switcher, user menu, theme toggle). Only the poker page has a "Back" link. The original had a sticky `AppHeader`, with the retro title in its centre slot. Decide in U29's scope before wave 5's front-end units land.

## Status after UX18

`docs/followups/UX18.md` closes three items above: U25's write of visibility, passwords and grants (the routes and
columns are gone), the `HasCurrentGrant` half of the `MembershipCache` blocking-read MINOR (the `RoleIn` half stands),
and the `getSocket()` guest-pid MINOR (already fixed in `screens/app.js`, and only users open sockets now).
