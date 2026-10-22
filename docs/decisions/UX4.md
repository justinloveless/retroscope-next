# UX4 decisions: seats and betting arcs wrap around the issue

UX4 owns `web/src/poker/{geometry,seats,table}.js`, `web/styles/neotro.css`, `tests/web-unit/poker/**` and
this file.

1. **Bracket curve.** `geometry.js pointOn()` now measures each seat's inset from the arc's middle, which is
   the point farthest from the stage centre; the ends curve in by at most the existing sagitta limit. The
   arcs' circle centre therefore lies in toward the issue, and `printedArc()` draws the betting line on the
   smaller concentric circle (radius minus the offset, sweep flipped).
2. **Vertical centring.** Each side's group is centred on the middle of the space between the top and bottom
   bands, not on the stage centre. Seats still turn to face the stage centre, so a group sitting above the
   centre tilts its top seats a little more than its bottom ones. To keep six players on full-size cards at
   1440×900 with that tilt, `MAX_DEPTH_SHARE` went from 0.15 to 0.16.
3. **Betting line gap measured radially.** `outlineReach()` finds how far each rotated seat outline reaches
   toward the circle's centre, so the line keeps a true minimum gap from every outline. The gap
   (`seats.js ARC_GAP_PX`) doubled from 14 to 28 px. `bettingArc()` also reports how far in the line reaches
   (at its ends, which curve in), and that drives the issue pane's side padding.
4. **Centring in the felt column.** `seats.js centreInColumns()` moves each side's seats in from the rail so
   they sit midway between the rail and the issue pane (`geometry.js columnShift()` solves this both when the
   pane follows the ring and when the pane is capped at 46rem on a wide stage), and re-aims them at the
   centre. The side whose line reaches furthest in sets the pane edge, and the other side is centred inside
   that. Centring never takes the issue below `ISSUE_KEEP` (half) of the stage width, so on narrow stages,
   such as 1440×900 with both side panels open, the seats move in only partly or stay near the rail.
5. **No stylesheet change.** `seats.js` mirrors the pane edge formula from `neotro.css`
   (`max(ring, 50% - 23rem) + 0.75rem`) as `PANE_HALF_PX` and `PANE_INSET_PX`.
