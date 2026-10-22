# UX6 decisions: avatar on the card's corner, the hand behind the bar

UX6 owns `web/src/poker/{seats,hand,table}.js`, `web/styles/neotro.css`, `tests/web-unit/poker/**` and this
file. `seats.js`, `table.js` and the stylesheet changed; `hand.js` did not need to.

1. **Which corner.** `seats.js avatarCorner()` compares a seat's centre with the arcs' vertical middle
   (`plan.curve.middle`), the line the seats on each side are centred on. A seat more than 0.5 px above it takes
   the higher of its card's two bottom corners, every other seat the lower one, so a side's middle seat goes
   to the lower corner. The stage centre would be the wrong reference: the arcs sit between the header and
   the dock, so the middle seat is rarely at exactly half the stage's height.
2. **Placement.** The avatar is centred on that corner in the seat's own (turned) frame by CSS
   (`data-corner`), so a quarter of its box lies over the card. The old `inset` size field is no longer used
   on the ring.
3. **Name label.** `seats.js seatMarks()` hangs the upright label just past the avatar on the same side
   (above an upper corner, below a lower one), overlapping only the avatar's rim by 4 px. It is also never
   closer than 3 px to the card's highest or lowest point. The label lines up with the avatar's outer edge and
   runs in toward the issue from there. A first try started the label at the avatar's inner edge. In the
   12-player crowd those labels landed on the neighbouring cards, because the gap between neighbours is
   narrowest there. `labelReach()` keeps working unchanged, because `labelAt` is still the label's outer end.
4. **Rail and bands.** `clearAvatars()` runs after `centreInColumns()` and moves a side in where an avatar
   would come closer than `STAGE_PAD_PX` to the rail. Column centring still measures the card alone. Counting
   the avatar made the single right-hand seat of a 3-player table hit the betting-line cap instead of
   centring. `table.js` now keeps 52 px below the round header (it had kept none) and 28 px above the dock
   (it had kept 64). The header's band is short, so without that room the top seat's label left the stage.
5. **Phones.** The scrolling row keeps the avatar beside the small card but lowers it onto the card's bottom
   outer corner.
6. **The hand.** The dock bar is `position: relative; z-index: 1`, and its top colour now mixes to an opaque
   result (91% + 9%; it was 80% + 9%, which left it 11% transparent). The hand has `z-index: 0` and a
   `--tuck: 14px` negative bottom margin. Every card height adds `--tuck`, so each resting strip shows as much
   as before. A card button is now a flex column, so its face sits at the top: a plain button had centred
   it, which would have sunk the raised faces into the bar. Raised heights also add
   `--clear = card width / 2 · |sin(lean)|`, so the outer cards' lower corners clear the bar.
7. **Checks run.** `./build.sh web` passed within its budgets. `node --test tests/web-unit/poker/` passed:
   the existing tests plus `avatar-corner.test.js`. A throwaway Playwright script against the demo preview
   called `elementFromPoint` at the centre of every resting card's visible strip and every raised card's face.
   Each call returned the card. A raised face's lowest point was 10 to 20 px above the bar. A click on a
   resting strip selected that card. No e2e suite or visual baseline was run or regenerated (quick-iteration
   mode).
