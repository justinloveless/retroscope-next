# UF3 decisions: domain and contract fixes from wave 2 verification

The per-domain changes are appended to `docs/decisions/{U2,U7,U8,U9}.md`. This file records the calls UF3 made
where `docs/followups/UF3.md` left room.

1. **Deleted poker session: client commands keep `not_found`.** Item 2 asks for a NoOp "for every input, internal
   ones included". A NoOp acks a client command as if it had happened, so a vote sent after the delete would look
   accepted. Internal inputs become NoOps; client commands keep the `not_found` nack U7 decision 15 gives them, which
   the client can act on.
2. **`submitPoints` permission is asked as if Jira were configured.** The policy denies Jira capabilities while Jira
   is not configured (U9 decision 6), so checking `Policy.Can` first with the real facts would turn every
   `jira_not_configured` into `forbidden`. The decider asks with `JiraConfigured = true`, then checks the real
   configuration, then asks again with the real facts through the usual round check.
3. **`RetroTimer` follows the facilitator rule in the policy too.** The decider already required a member on team
   boards (U8 decision 3) while the policy only required a signed-in user. Item 6 asks that policy and decider
   cannot drift, so the policy now delegates `RetroTimer` to `RetroRoles.IsFacilitator` as well.
4. **The policy denies `RetroReady`/`RetroFocus` to non-members on team boards** (item 9), through the same
   `RetroRoles.MayPace` the decider uses, so an HTTP or subscribe-time check cannot disagree with the decider.
5. **Action-item comments ignore the stage gate and the archive.** They are posted from the team page about items
   from earlier retros, whose boards are usually closed and often archived; refusing them there would make the
   team-page thread unusable. Membership is still checked at decide time (R12), and quick boards refuse them.
6. **`retro.members` is a new kind** (`RetroMembers` in `Protocol/Events.cs`, registered in `WireJsonContext`) rather
   than widening `team.members` to board rooms: a payload type has exactly one kind, and a board-scoped name keeps
   the board reducer's events in one namespace. Adding it needed one line in each of `Events.cs` and
   `WireJsonContext.cs` beyond `Catalog.cs`, plus the regenerated `protocol.gen.js` and `docs/protocol.md`.
7. **Team events carry `At`.** `IRoomDomain.Apply` has no clock, so the decider stamps every accepted event with
   `context.Now` (an init-only property on `TeamEvent`, so constructors and the store are unchanged) and `Apply`
   prunes against it. The ring re-projects events without looking at `At`.

## Verification

- `dotnet test tests/Retroscope.Core.Tests`: 1,634 tests pass.
- Each item's new test was run against the code with that fix reverted and failed (poker items 1–3, retro items
  7, 9 and 10, team item 11, the policy-agreement test for 6 and 9); items 5 and 8 add new members that the old code
  does not have. Item 4 is a test-scope change.
- `./build.sh test`: see the unit report.
