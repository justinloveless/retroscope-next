# UX4: seats and betting arcs wrap around the issue

This is the owner's feedback on UX3. UX4 owns `web/src/poker/{geometry,seats,table}.js`, `web/styles/neotro.css`, `tests/web-unit/poker/**`, and `docs/decisions/UX4.md` (you may append to `docs/decisions/UX3.md`).

1. **Curve direction flips.** Each side's seat path and its betting arc now wrap *around* the issue, like brackets: `( ISSUE )`.
   - The middle of each side is farthest from the stage centre, and the ends curve in toward it.
   - Keep the curve gentle, with the existing small sagitta limit.
   - Seats still rotate so their card tops point at the stage centre.
   - The arc still follows the seat curve with a constant offset.
2. **Seats centred in the side column.** Each side's seats sit in the middle of the felt between the panel or rail edge and the frosted issue panel, rather than hugging the outer edge.
   - Each side's group is centred vertically within the space between the top and bottom bands.
3. **Arc further from the seats.** The gap between each side's seat outlines and its betting arc is clearly larger: about double today's, so the arc sits nearer the issue panel, with room left before the panel's edge.
   - The issue's side padding still reserves room so the arc never runs under the frosted panel.
4. **Unit tests.**
   - Update the golden geometry tests for the flipped curve. The middle seat's centre should be farther from the stage centre than the end seats'.
   - Add a test for centring within the side column.
   - Add a test that the arc keeps at least the new gap from every seat outline.
   - The existing bounds, overlap, band and angle-to-centre tests stay.
