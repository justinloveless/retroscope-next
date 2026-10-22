# UX3 decisions: a real poker table

UX3 owns `web/src/poker/**`, `web/styles/neotro.css`, the felt tokens in `web/styles/tokens.css`,
`tests/web-unit/poker/**`, `tests/e2e/fake/poker-*/**` and this file.

1. **Felt tokens.** `--felt`, `--felt-shade`, `--felt-print` and `--felt-glass` (how much of the card colour the
   frosted issue pane keeps) live in `tokens.css`, with deeper values for dark. The rail colour is derived in
   `neotro.css` (`--nt-rail`, felt shade mixed toward dark leather) rather than given its own token.
2. **One fixed felt layer.** `.nt-table::before` paints the cloth: an inline SVG `feTurbulence` nap, a lighter
   pool in the middle, a vignette and the rail as inset shadows. It has `contain: strict` and never moves.
3. **Frosted pane, not a frosted card.** The blur is `.nt-field::before`, a fixed pane the width of the issue;
   the issue scrolls over it inside `.nt-issue`, which fades its content out at the top and above the hand with
   a mask. The issue panel (`.jira-issue`) lost its own card background. Without `backdrop-filter` the pane is
   96% opaque. Inside the field, light mode's `--muted-foreground` is a step darker (and dark mode's a step
   lighter) so muted text keeps AA contrast on the glass.
4. **Seat outlines are CSS.** `.nt-seat-slot::before` draws the outline around the card spot, so it turns,
   moves and fades with the seat. `seats.js printMetrics()` mirrors its pad and stroke for the geometry.
5. **Betting lines are SVG.** `geometry.js printedArc()` (pure, unit-tested) returns one side's path:
   concentric with the seat arc (`seatLayout` now also returns `curve: {radius, middle}`), offset in by the
   widest outline extent plus a gap, ending a little past the outer seats. `seats.js` draws them in
   `seats.print` (an `<svg>` the table puts behind everything else) and widens the reported ring depth so the
   pane keeps clear of the lines. The path transitions through CSS `d` where supported.
6. **Labels on felt** are white on a felt-shade pill, dark enough to stay AA over a revealed card's white face.
7. **The dock bar** takes the rail's material and re-scopes the colour tokens to a dark set inside it.
