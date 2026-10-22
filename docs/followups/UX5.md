# UX5: symmetrical betting arcs

This is the owner's feedback on UX4. UX5 owns `web/src/poker/{geometry,seats,table}.js`, `web/styles/neotro.css` (only if needed), `tests/web-unit/poker/**`, and `docs/decisions/UX5.md`.

The two betting arcs are always mirror images of each other across the stage's vertical centre line.

1. **Same size.**
   - Both arcs take the vertical span (top and bottom ends) and the curve of whichever side's arc is bigger.
   - Because the two sides can hold different numbers of seats, "bigger" means the longer span; on a tie, the deeper curve.
2. **Same distance from the centre.**
   - Both arcs sit the same horizontal distance from the stage centre.
   - Use the position that keeps at least the UX4 gap (28 px) from every seat outline on both sides. In practice that is the more inboard of the two.
   - Both arcs still stop short of the frosted issue panel, and the issue's side padding is recomputed from the mirrored arcs.
3. **Always both.** Both arcs are drawn whenever at least one other player is seated, even if one side has no seats, so the table always looks symmetrical. With no seated players, neither arc is drawn.
4. **Unit tests** cover each of these:
   - the arcs mirror exactly (within 0.5 px) for uneven seat counts (1–12) on three stage sizes
   - the 28 px minimum gap holds on both sides
   - no arc enters the issue panel
   - one seat gives two arcs
   - zero seats gives none
