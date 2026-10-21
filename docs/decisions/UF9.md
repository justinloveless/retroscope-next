# UF9: realtime end to end and correctness

Spec: `docs/followups/UF9.md`. Amendments to U13, U14, U15, U21, U22, U23 and U28 point back here.

## Contracts later units rely on

- **`RoomGateway` lives in `Realtime/Actors`** (namespace `Retroscope.Server.Realtime.Actors`, with `IResumeFill`) and
  `ActorsModule.AddServices` registers it as the `IRoomGateway` for every registered `RoomType`. A room kind only adds
  its `RoomType` (`services.AddRoom`); `UnavailableRoomGateway`, `NoTeamAccess` and the board's `NoAccess` are gone, so
  a missing `IAccessView` now fails at startup instead of denying everyone.
- **`IRoomPeer.Kick(room, code)` and `IRoomPeer.SendSync(frame)`** are the typed hooks the actor uses for a kick and
  for the `snap`/`replay` that starts a subscription. Both default to `Send`; the gateway's peer uses them to forget a
  kicked room and to send resume fills, so nothing sniffs frame bytes any more.
- **Order per (connection, room).** The gateway chains every `sub` and `cmd` of one connection to one room, so they
  reach the registry in arrival order even while the actor is starting. A step whose predecessors are done runs at once
  on the receive loop.
- **`IRoomBinding.IsCreate(command)`**: a `NoOp` for a create under a client-minted id (UF6's repeated create) is acked
  `{dup: true}`. Poker: `round.create`, `round.createMany`, `round.next`, `rounds.clearAll`; retro: `card.add`,
  `comment.add`, `column.add`, `endorse.give`.
- **`ActorRegistry.Retire` returns whether the close found no leftovers** (internal; only `RoomActor` calls it).

## Decisions

1. **The cursor never passes a refused `q` (lost-retryable-nack).** Nothing extra is persisted; the rule is structural
   and per connection:
   - `server_busy` (the gateway, per connection and room): once a command is refused, every later command of that room
     on that connection is refused `server_busy` too, until the client subscribes to the room again.
   - `rate_limited` (the transport, per room): the same, until the next `sub` of that room; that `sub` is never dropped
     by the limiter, or the client could not lift the refusal.
   - The client (U28 `room.js`, and the simulated tab) keeps the entry's `q` on a retryable nack and, after the backoff,
     resubscribes and resends after the `snap`/`replay`. A refused `q` is therefore always decided before anything
     behind it can commit, and its resend is never answered `dup` for an intent the server did not decide.
2. **4409 keeps the cid.** A 4409 means another socket holds the cid. The client first asks BroadcastChannel: a live
   twin that answers owns the cid and the outbox, and this tab yields exactly as a duplicated tab does at boot (fresh
   cid, empty outbox). Otherwise the holder is this tab's own dead socket, which keep-alive reaps: the cid and every `q`
   stay, so the cursor still answers committed entries `dup` instead of deciding them again under a new cid.
3. **`cid_reuse` renumbers only the nacked entry.** Another intent of this cid holds that `q` and this one was not
   applied. Entries in flight behind it keep their `q`s, since they may have committed.
4. **Restored entries keep their `q` (stale storage).** An entry restored from sessionStorage may have been sent and
   committed since that copy was written, so it is never renumbered at sync. Only entries this page created before
   their room first synced, whose `q` may come from a `cseq` storage let fall behind the cursor, are renumbered.
5. **Supersede keys and preconditions.** The screens take `baseVersion`/`expected` from the view, which folds pending
   entries in, so the "server-confirmed values" option was not available without changing screens UF9 does not own.
   The keys are dropped instead from `poker.round.setTicket` and `retro.board.rename`, as the spec allowed, and also
   from `retro.card.edit`: the card's optimistic edit bumps its version, so a newer edit names a version only the older
   one produces, and dropping the older one on a retryable nack made the newer one stale.
6. **Rooms.**
   - Poker: `PokerJiraLoop.Track` posts a parent refill once per cached issue for the state it last saw (it ran after
     every message and re-posted the same refill until the actor applied it), and skips deleted sessions.
     `DecideJiraMeta` needs no guard of its own: `PokerDecider.Decide` already answers every internal input on a deleted
     session with a no-op. `SpotlightSettled` already writes `spotlight_round_id` and `spotlight_client_id` from the
     event (`PokerStore`), so no change was needed.
   - Retro: the store loads which action item each card of the board feeds and keeps that map from the board's own
     events (`CardAdded` with an item, `CardDeleted`, `ColumnDeleted`), so `ActionItemCommentsChanged` needs no query in
     the commit path. Items imported later attach to cards loaded with the board, so the load covers them.
   - Team: a committed `TeamDeleted` publishes an empty role set and posts `AccessChanged` to every live session and
     board of the team. Loading the team room posts the roster it read (`RosterChanged`, `MembersChanged`,
     `AccessChanged`), so a member change whose commit outcome was unknown still reaches live rooms.
7. **Purge race (U13).** UF7 refused a purge while anything was queued, but a message accepted in the instant between
   that check and the close was handed to the incarnation after the purge, which served it from the purged room. The
   close now decides, under the registry lock: leftovers go to a next incarnation started at once and the purge is
   refused. `RegistryTests.An_ask_the_actor_accepted_is_never_served_by_the_purged_room` races the two 300 times; the
   window is too narrow to fail reliably against the old code, so the test guards the invariant rather than proving
   the bug.
8. **Hello timer (U14).** UF7 already made `Hello` and the hello timer race for one flag with
   `Interlocked.CompareExchange` (`HelloRaceTests`). Reading the code again found no remaining window, so nothing
   changed.
9. **Simulation.**
   - `IQuiescent.IsParked` (internal, `InternalsVisibleTo("Retroscope.Sim")`) replaces the reflection on `RoomActor`'s
     private channels.
   - Every simulated socket is an `IClientConnection` driven through the real `RoomGateway`, so the refusal rule of
     decision 1 is what the simulation exercises.
   - A team-visible board joins the world on the real `RetroBinding` and `RetroDecider`, folded by the linked reference
     client (`RetroViewerModel`). The oracle checks anonymity on every frame (an anonymous post never carries its author,
     and only its author's tabs are told it is theirs), that readiness and stage changes commit only for the stage the
     board is in, that each timer end expires once (committed, and as seen by each page) and none is left running past
     its end, and that each folded board equals a fresh snapshot and the live board equals a reload.
   - `StaleStorage` and `StaleSocket` are default fault classes. `Twins` stays opt-in: two live tabs that missed the
     boot check hold one cid with diverging outboxes, and each can send a different intent under the same `q`. Below the
     cursor the server cannot tell them apart without remembering a hash per `q`, so one is acked `dup` undecided.
     That is 2 of seeds 1–2000 with `--faults Default,Twins` (seed 292 first); seed 13 passes. §c.6 accepts this
     residual case.
   - `build.sh`'s sim step runs with `--strict`, and the `lost-retryable-nack` known issue is gone.

## Integrator notes

- U25/U27 (and anything else that names them) must import `RoomGateway` and `IResumeFill` from
  `Retroscope.Server.Realtime.Actors`, register no `IRoomGateway` of their own, and expect a `TryPurgeAsync` that can
  return false even when the queues looked empty at the time of the check.

## Verification

See the end of the unit's report; the numbers are recorded in the U15 amendment.

## Amendment (UF13)

1. **Decision 1, `rate_limited`:** the `sub` that lifts the refusal now borrows its rate token instead of passing for
   free (UF13 decision 1).
2. **Decision 9, `Twins`:** fixed and on by default. The actor remembers the hash of every decided `q`; the oracle
   judges each twin's answer on its own send and counts the restart residual that §c.6 accepts instead of failing it.
   Seeds 1–12000 with `Twins` on: all pass, 3 intents counted as the residual (UF13 decision 3).
