# UF3: domain and contract fixes from wave 2 verification

UF3 owns these paths for these changes only: `src/Retroscope.Core/{Poker,Retro,Team,Authz}/**`, `src/Retroscope.Core/Protocol/Catalog.cs` (only if a capability or kind must be added), `tests/Retroscope.Core.Tests/{Poker,Retro,Team,Authz,Protocol}/**`, `tests/authz-matrix.csv`, `docs/decisions/{U2,U7,U8,U9}.md` (append-only).

1. **Poker (U7):** `ClientDecider.Create` must not run `FillPlaceholder` for `pending: true` commands; a holder's preview must never write a key onto the shared committed round. Add a test.
2. **Poker:** once the session is deleted, `PokerDecider.Decide` returns NoOp for every internal input; client commands keep the `not_found` nack so a stale vote is never acked as accepted (AutoRevealDue, JiraPointsResult, JiraMetaLoaded, RosterChanged, …). Add a test.
3. **Poker:** `SubmitJiraPoints` checks the capability before `jira_not_configured`, so callers without permission get `forbidden`.
4. **Protocol (U2):** narrow `ProtocolCatalogTests.Every_concrete_payload_type_has_exactly_one_kind` to types in `Retroscope.Core.Protocol`, and correct U2 decision 3 to describe the input-wrapper pattern U7/U8/U9 use (`PokerInput`, `RetroInput`, `TeamInput`).
5. **Authz (U9):** add an owner-only capability `TeamManageAdmins` covering role changes, removing an admin and ownership transfer (§d). `TeamManageMembers` stays admin-or-owner for everything else. Extend the matrix with allow/deny rows.
6. **Authz:** Retro capabilities `RetroFacilitate`, `RetroEndorse`, `RetroViewRoster` delegate to `RetroRoles.IsFacilitator`/`IsMember`, so the two cannot drift.
7. **Retro (U8):** `RevokeEndorsement` requires the giver to still be a team member.
8. **Retro:** add a `RetroInput` variant for HTTP-originated action-item comments (routed via Ask per §c.4), so U22/U25 don't impersonate a client command.
9. **Retro:** non-members on link/password **team** boards may not `SetReady`/`SetFocus` (§c.4: "add, edit their own, vote and comment, nothing more"). Guests on quick boards keep both.
10. **Retro:** `MembersReplaced` projects a board wire event (`retro.members`) so an incrementally folded member view equals a fresh snapshot. If it needs a new kind in `Catalog.cs`, add it and regenerate `protocol.gen.js`.
11. **Team (U9):** prune action items closed more than 30 days ago from `TeamState` on apply, not just in the projection.

**Check:** `dotnet test tests/Retroscope.Core.Tests` and `./build.sh test` green; every item above has a test that fails on the old code.
