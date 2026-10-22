# UX5 decisions: symmetrical betting arcs

UX5 owns `web/src/poker/{geometry,seats,table}.js`, `web/styles/neotro.css`, `tests/web-unit/poker/**` and
this file. Only `geometry.js` and `seats.js` changed; the stylesheet and `table.js` did not need to.

1. **One line shape for both sides.** `geometry.js mirroredArcs()` first works out each side's own line as
   UX4 did (concentric with its seats, `ARC_GAP_PX` in from its outlines). The longer one gives both lines
   their top and bottom ends and their radius. When the spans are within 0.5 px of each other, the smaller
   radius (the deeper curve) wins. A straight side keeps a straight line.
2. **One distance from the centre.** For each side, a bisection finds the smallest circle-centre position at
   which a circle of that radius keeps the gap from every outline on that side. The lines use the most
   inboard of those positions, mirrored. The distance is measured to the whole circle, which can only
   overstate the true gap, so the 28 px minimum holds on both sides.
3. **Always both lines.** `seats.js bettingLines()` returns both paths whenever any slot (a seat or the "+N"
   chip) is placed, and neither when nobody is seated.
4. **Issue padding and column centring.** The issue pane's side padding comes from the mirrored lines'
   reach. `centreInColumns()` still centres each side's seats in its felt column, but it now measures each
   side by the reach the mirrored lines would have if that side alone placed them (`needs`). The side that
   pushes the lines furthest in leads, and the other side may move in only until it would push the lines
   further. Using the shared reach for both sides instead would stop the other side from centring.
5. **Checks.** `tests/web-unit/poker/mirror.test.js` covers the spec's Check list for 1–12 players on three
   stage sizes. The existing per-side `printedArc()` tests still describe the single-side building block.
