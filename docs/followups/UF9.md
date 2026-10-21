# UF9: realtime end to end and correctness (from wave 4)

UF9 owns these paths:
- `src/Retroscope.Server/Realtime/**` (Actors, Transport, Rooms, Stores)
- `src/Retroscope.Core/Poker/**`
- `web/src/net/**`
- `tests/Retroscope.Sim/**`
- `build.sh` (the sim step only)
- the matching tests under `tests/Retroscope.Integration.Tests/{Actors,Transport,PokerRoom,RetroRoom,TeamRoom}/**` and `tests/web-unit/net/**`
- `docs/decisions/{U13,U14,U15,U21,U22,U23,U28}.md` (append-only)

1. **Rooms reachable over real WebSockets.** Register a real `IRoomGateway` backed by `ActorRegistry` for every room kind (poker, retro, team), and move `RoomGateway` under `Realtime/Actors`. Remove `UnavailableRoomGateway`. Switch `RetroRoom.AddServices` to `services.AddRoom` and remove the `NoAccess` fallback now that U23 registers `IAccessView`. Add an integration test where a signed-in client over a real socket subscribes to a poker session, votes, and a second client sees the locked status. Do the same for a retro board and a team room.
2. **lost-retryable-nack (sim seed 819).** A command refused `server_busy`/`rate_limited` can lose its nack when the socket dies, while a later pipelined command of the same cid commits. The resend is then acked `dup` and the intent is silently lost. Fix it at the root: the server must never ack `dup` for a q it refused. Persist nothing extra if a structural rule suffices, for example: after refusing q on a connection, refuse every later q of that cid on that connection with the same retryable code, so the cursor never passes a refused q. Then turn on `--strict` in `build.sh`'s sim step and delete the KnownIssues entry.
3. **4409 re-mint double-apply** (`--seed 51 --faults Default,StaleSocket`, `--seed 13 --faults Default,Twins`). Entries that committed under the old cid must not be decided again under the new one. Combined with UF6's idempotent creates and preconditions, make the remaining overwrite kinds safe too, so both seeds pass. Add them as default fault classes if feasible.
4. **Stale storage** (`--seed 697 --faults Default,StaleStorage`). Entries restored from an older sessionStorage copy are treated as possibly sent and never blindly renumbered.
5. **Keyed overwrite preconditions (UF6 follow-up).** `baseVersion`/expected values come from server-confirmed state, never from optimistic local state. If that's impossible, drop the supersede keys from rename and setTicket. Map a NoOp create to `ack{dup:true}`.
6. **Rooms.**
   - Poker: stop duplicate Jira refill posts, and add a Deleted guard to `DecideJiraMeta`/Track.
   - Retro: remove the post-commit DB round trip for `ActionItemCommentsChanged`.
   - Team: `TeamDeleted` posts `AccessChanged` to the team's live sessions and boards.
   - Spotlight: `SpotlightSettled` writes `spotlight_round_id` and `spotlight_client_id` from the event.
   - Membership: loading a team room refreshes the membership cache and re-posts `RosterChanged`/`AccessChanged`.
7. **Sim.**
   - Replace reflection on `RoomActor` private fields with an internal quiescence hook (`InternalsVisibleTo`).
   - Add a simulated retro room covering anonymous tags, readiness, and a timer that expires once.
   - Fix the leftover U13/U14 minor races: the purge internal queue, and the hello timer.

**Check:** the integration filters above, then `dotnet run --project tests/Retroscope.Sim -- --seeds 2000 --strict` and the three seeds above with their fault sets, `./build.sh aot-smoke`, and `./build.sh test`, all green.
