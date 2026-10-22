# UX3: a real poker table

The owner wants the poker session to feel like a real game of poker. Their reference image (a green felt table with white printed seat boxes) set the general idea only: do not copy it or use any stock image. UX3 owns `web/src/poker/**`, `web/styles/neotro.css`, `web/styles/tokens.css` (the felt tokens only), `tests/web-unit/poker/**`, `tests/e2e/fake/poker-*/**`, the poker states in `tests/e2e/screenshots/**`, the poker visual baselines, `web/budgets.json` (only if the felt needs a few hundred bytes), and `docs/decisions/UX3.md`.

1. **Green felt stage.** The stage (the region between the Jira and history panels) becomes a green felt poker table in both themes.
   - **Generated, not downloaded.** The felt is generated: CSS gradients plus a tiny inline SVG `feTurbulence` noise as a data URI, with a soft darker vignette toward the edges. There are no image downloads and no third-party assets, and it adds well under 2 KB brotli to the CSS.
   - **Tokens.** Felt colours come from tokens (`--felt`, `--felt-shade`, `--felt-print`). The dark theme uses a deeper green, and both themes read clearly as a card-table green.
   - **Rail.** A subtle padded rail (a darker, slightly raised border, like a table's leather rail) frames the stage edge. It must stay understated and never compete with the issue.
   - **Performance.** The felt must not repaint on scroll or animation: put it on a non-scrolling layer behind the issue.
2. **Printed seat outlines.** Each other player's seat gets a white printed outline drawn on the felt, generated dynamically from the seat layout.
   - **Shape and look.** A rounded rectangle the size and shape of the seat's card spot, rotated with the seat. The stroke is about 2 px, coloured `--felt-print` (off-white, about 80 % opacity), slightly soft like ink printed on felt.
   - **Follows the seats.** Outlines appear and disappear with players, and move and turn smoothly when the layout changes, honouring reduced motion.
   - **Replaces the dashed placeholder.** A locked card slides into its outline. On reveal it flips inside the outline. Observers get a dimmed outline with no card.
   - **No table outline.** No other table lines are drawn: the owner earlier asked not to see the table's outline. The seat outlines alone imply the table.
   - **Overflow chip.** The "+N" chip sits on the felt in the same printed style.
3. **Everything readable on felt.**
   - **Names and pills.** Seat name labels, presence dots and vote pills stay readable on green: white text with a subtle shadow, meeting WCAG AA contrast against the felt in both themes. Add the check to the a11y specs.
   - **Issue panel.** It sits on the felt like a card on the table: an opaque surface in the theme's card colour with a soft drop shadow. The text never sits directly on felt.
   - **Overlay.** The results overlay keeps its translucent blur over the issue, and its piles read as cards laid on the felt.
   - **Hand.** The hand's dock bar takes the rail's material, so the hand reads as held over the table edge.
4. **Phones.** The seat row stays a row, but it sits on a strip of the same felt with printed outlines.

## Check

- `./build.sh test`
- `./build.sh fake-e2e "poker-*/*.spec.js"` on all four projects, with an added contrast assertion for seat labels on felt
- `./build.sh e2e "real/poker*.spec.js"`
- `PREVIEW_PORT=5200 ./build.sh screenshots`

Visual baselines are regenerated on macOS and Linux. Before declaring done, look at screenshots in light and dark at 1440×900, 1920×1080 and 1280×800, and on a phone, with 3, 6 and 12 players, both voting and revealed. Judge them as the owner would: does it feel like a real poker table, and is the issue still the clear focus?
