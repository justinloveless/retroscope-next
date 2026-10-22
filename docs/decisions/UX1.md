# UX1 decisions: the issue at the centre, the players around it

Spec: `docs/followups/UX1.md`. UX1 owns `web/src/poker/**`, `web/styles/neotro.css`, the demo, the poker specs and
baselines it lists, and this file.

## Contracts later units rely on

1. **Seat geometry** (`poker/geometry.js`, pure). `seatLayout(stage, count, seat)` takes `{width, height, top,
   bottom, maxDepth}` and a seat size `{width, height, spacing}`. It returns `{seats: [{x, y, angle, side}], chip,
   hidden, depth}`. Player i sits on the left when i is even. Each side runs top to bottom and is spread evenly by
   arc length, at 1.6 × the minimum spacing when there is room. The arc stops at ±60°, at the top and bottom bands
   (seat centres never enter them), or at `maxDepth`, whichever comes first. `angle` is
   `atan2` toward the stage centre (the card's top edge points at it). `planSeats(stage, count)` picks the first of
   `SEAT_SIZES` (full, compact, avatar-only) that seats everyone within a ring depth of
   min(260 px, a quarter of the stage width). Failing that, it uses avatar-only seats with a "+N" chip.
   `seatsOverlap` and `extents` are exported for the tests.
2. **Table slots.** `ctx.slots` gains `issue` (a feature fills it with the issue's details; the table's fallback
   shows while it is empty), `issueScroller` (the stage's scroll container) and `results` (added to a revealed
   round's results in the dock). `ctx.actions.editTicket()` focuses the ticket key field. `createTable(actions)`
   no longer takes the hand. `table.attachHand(hand, observerNote)` docks it, and `hand.actions` holds the Lock In
   and Abstain buttons for the dock's bar.
3. **Issue views** (`issue-drawer.js`): `createIssueDrawer(ctx)` (the side sheet, unchanged behaviour) and
   `createIssuePanel(ctx)` share one view. The panel takes `scroller`, `onLink` and `openLinked` and is shown
   with `show(key)`.
4. **Seat DOM.** `.nt-seat[data-pid][data-status]` keep their meaning (`empty`, `selected`, `locked`, and now
   `revealed`). There is no seat for the viewer (`[data-me]` is gone). Observers are seated with `data-observer`.
   A seat's accessible name is `"<name>[ (guest)], <waiting|thinking|locked in|voted 5|voted 3 to 5|abstained|did not
   vote|observer>[, away]"`. `.nt-stack-pill` keeps its "N x 5 pts" text in the results bar. The `.nt-stack` items
   carry the voters in their accessible name and title.

## Decisions where the spec was silent or did not fit

1. **Where the chip sits.** In a full crowd the players alternate left and right, so the left arc fills first and
   the last slot of the right arc stays free. The "+N" chip takes that slot, and both arcs hold as many items as
   fit. Putting the chip after the fuller (left) arc would need a slot that arc does not have.
2. **The ring depth is capped**, and the seats shrink before the arcs reach far inward. A 120° arc on a
   stage-sized ellipse curves in by a quarter of the width, which left the issue less than 250 px at 1440×900.
   The labels hang from the avatar's outer edge, so they need no extra inset. The lowest seats stay a label's
   height (64 px) above the dock.
3. **Narrow stages** (below 560 px wide, which is every phone) use the scrolling avatar row. That is a property of
   the stage, not the device, so a tablet with both panels open gets the row too.
4. **Results without stacks.** The cards flip on the seats, so the revealed table no longer draws overlapping
   stacks. The dock's results show the winner, the group pills (the voters are in each pill's name and title, and
   each seat's label carries its value as a chip), Replay and End/Continue/Next Round, and the Jira submit chips
   above them. The viewer's own vote counts in the groups.
5. **Peek and raise.** At rest a hand card's button is only the strip that peeks out (35 % of the card), with the
   face clipped inside it. Raising grows the button upward from a fixed bottom edge, and the hover and selection
   lifts grow it further. Two simpler versions failed. Clipping a full-height card left its centre under the bar,
   so a press there hit the bar. Translating the raised card moved it out from under the pointer, which with
   reduced motion happens before the press lands (the real-stack visual spec caught this). Hovering anywhere on
   the dock raises the fan.
6. **"Between" gaps.** While a card is dragged, the gaps between it and its two neighbours in the raised fan are
   the 2 s hold zones (`.nt-zone-left`/`-right`, labelled "Between 3 & 5"). Other gaps are not zones: a between
   vote is always the dragged card and a neighbour. A drop counts only above the raised fan's top edge.
7. **The browse drawer stays.** Issues opened from the Jira panel still open in the side sheet, and the holder's
   preview still works as before (U33 decision 8; followers see the preview round's issue at the centre). A linked
   issue clicked in the centre view opens in the drawer, so the centre keeps the round's own issue. The "Issue
   details" toolbar button is gone.
8. **Announcements.** Lock and presence changes are batched into one polite announcement per second
   (`core/a11y.js announce`). They are spoken only for changes on the viewed round, never on a round switch.
9. **Reduced motion.** Seats jump to new places, a locked card fades in instead of sliding, and every seat card
   crossfades on the reveal (the existing between-card fade).
10. **Demo.** The demo had 3-player (quick) and 6-player (team) tables. A crowded quick session `CRWDPKR012`
    (Demo User and 11 fictional guests) covers 12 players.

## Ownership deviations

- `tests/web-unit/poker/geometry.test.js` is new. No unit owns `tests/web-unit/poker/`, and `build.sh test` runs every
  `*.test.js` there.
- `tests/e2e/real/_poker.js` (shared helper): `table()` expects `players − 1` seats.
- `tests/e2e/real/reconnect.spec.js` and `tests/e2e/smoke/poker.spec.js`: seat counts and the player's own lock are
  read from the hand, because there is no own seat. These are minimal edits.
- `tests/e2e/screenshots/_preview.js` gains `CODES.crowdPoker`, and `smoke.spec.js` no longer looks for an own seat.

## The Check as written

`./build.sh e2e "real/poker*/*.spec.js"` matches no file (the real poker specs are `real/poker-*.spec.js`, not
directories), so Playwright exits with "No tests found". The specs were run as `./build.sh e2e "real/poker*.spec.js"`.
`cmd_e2e` does not rebuild `retroscope:test`, so `docker compose -f deploy/compose.test.yml build` ran first.

## Verification

- `node --test tests/web-unit/poker/geometry.test.js`: 11 pass. Planting an angle bug (+2°), a too-tight spacing
  or arcs that ignore the bands makes 5 to 7 of them fail.
- `./build.sh fake-e2e "poker-*/*.spec.js"`: 543 passed, 41 skipped (chromium, firefox, webkit, mobile-webkit). The
  shell, team and harness suites: 532 passed, 8 skipped.
- `./build.sh e2e "real/poker*.spec.js"`: 52 passed on the four projects. `real/a11y`, `real/reconnect` and
  `real/visual` (all projects) and `smoke/poker` also pass.
- Visual baselines were regenerated on macOS and in the pinned Linux image: the fake table (`--repeat-each 5` on
  both) and the real-stack table, hand and reveal (Linux through `run-server`, `--repeat-each 3`).
- `PREVIEW_PORT=5200 ./build.sh screenshots`: smoke 16/16, capture 140/140. New states: `poker-hand-raised`,
  `poker-between-hold`, `poker-crowd-12-voting`, `poker-crowd-12-revealed`.
- Looked at 1440×900, 1920×1080 and 1280×800 with 3, 6 and 12 players, light and dark, voting and revealed, and the
  iPhone 13 layouts.
