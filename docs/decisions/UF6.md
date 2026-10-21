# UF6 decisions: client network correctness

The two protocol rules are recorded in `docs/decisions/U28.md` (12 and 13). This file records the calls UF6 made
where `docs/followups/UF6.md` left room.

1. **A repeated create acks as a plain ack, not `dup`.** `ack{dup:true}` is written by the actor's cursor check
   (`Server/Realtime/Actors`, owned by UF7). Core can only return a `Decision`, and `NoOp` already acks. The
   client handles both acks the same way (DESIGN §g.4: "`dup` acks are handled the same way"), so no new decision
   variant was added. `NoOp` does not advance the cursor for the new cid; later commands of that cid still carry
   higher `q`s, so nothing depends on it.
2. **"Same payload" means what state still holds.** State does not keep command payloads, so each create compares
   the fields that survive in state, and only for the caller's own post:
   - `poker.round.create`, `poker.round.next`: the round with that id has the same normalized ticket key. A `next`
     whose `fromRoundId` equals `newRoundId` is never a repeat.
   - `poker.round.createMany`: every listed round exists with its ticket, except that the first may have filled
     the placeholder (it keeps the placeholder's id), so its ticket only has to be on some round.
   - `poker.rounds.clearAll`: the round with that id is number 1, the only trace a reset leaves.
   - `retro.card.add`: same column, same anonymity, owned by the caller, and the same body unless the card was
     edited since (version > 1). Action items are created with their card, so this covers them.
   - `retro.comment.add` and the HTTP `ActionItemCommentAdd`: same card, anonymity, owner and body.
   - `retro.column.add`: same title and colour unless the column was updated since.
   - `retro.endorse.give`: same giver, receiver and type, checked before the type and receiver checks, so a gift
     whose type was archived since still acks.
   Any other reuse of an existing id stays `invalid`. The repeat check comes after the permission check, so a
   caller who lost access is still refused, but before stage and archive gates, which may have closed since.
3. **Known gap: a single `poker.round.create` that filled the placeholder.** That create keeps the placeholder's
   id, so a resend finds no round with its own id and the placeholder now has a ticket: it creates a second round
   with the same ticket. Detecting this would need state that records which create filled the placeholder.
   That state would be a new persisted event or column, which is outside UF6. It requires a cid_reuse renumbering
   that lands exactly between that create's commit and its ack.
4. **The supersede table lives in `web/src/net/supersede.js`**, next to the generated `protocol.gen.js`, not in
   the generated file. The C# catalog does not describe targets, and the generator belongs to the protocol unit.
   The completeness test checks that the table has exactly the catalog's kinds, that every kind named like an
   overwrite (`.set`, `.edit`, `.update`, `.reorder`, …) has a key unless it is a listed exception, that creates
   have none, and that key members are required members of the command.

## Verification

- `node --test` over `tests/web-unit/net/*.test.js` and `tests/web-unit/markup.test.js` (Node 20.19): see the unit
  report. With the `room.js`/`outbox.js` changes reverted, the vote 3 → 5, edit → edit, backoff and reload tests
  fail (6 failures).
- `dotnet test tests/Retroscope.Core.Tests --filter "Category=Poker|Category=Retro"`: with the decider changes
  reverted, all 12 new idempotent-create tests and the updated endorsement test fail.
- The 39 JS timing rows (13 bodies × 3 dialects) each parse in under 200 ms.
