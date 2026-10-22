# UX3: a real poker table

The owner wants the poker session to feel like a real game of poker. They gave two reference images. The first, a stock photo of a green felt table with white printed seat boxes, sets the general idea only: do not copy it or use any stock image. The second is the owner's own mockup of this screen, and it is the layout to match:
- the felt fills the whole stage, behind the issue too;
- the issue panel is frosted glass, so the table shows through it, blurred;
- thick white printed outlines mark each seat, rotated toward the centre: corner seats sit diagonally and the middle seats lie sideways;
- each side has one printed arc line, bowing toward the centre and following that side's seat curve, inboard of the seats.

UX3 owns `web/src/poker/**`, `web/styles/neotro.css`, `web/styles/tokens.css` (the felt tokens only), `tests/web-unit/poker/**`, `tests/e2e/fake/poker-*/**`, the poker states in `tests/e2e/screenshots/**`, the poker visual baselines, `web/budgets.json` (only if the felt needs a few hundred bytes), and `docs/decisions/UX3.md`.

1. **Green felt stage.** The stage (the region between the Jira and history panels) becomes a green felt poker table in both themes. The felt spans the whole stage, including the area behind the issue panel.
   - **Generated, not downloaded.** The felt is generated: CSS gradients plus a tiny inline SVG `feTurbulence` noise as a data URI, with a soft darker vignette toward the edges. There are no image downloads and no third-party assets, and it adds well under 2 KB brotli to the CSS.
   - **Tokens.** Felt colours come from tokens (`--felt`, `--felt-shade`, `--felt-print`). The dark theme uses a deeper green, and both themes read clearly as a card-table green.
   - **Rail.** A subtle padded rail (a darker, slightly raised border, like a table's leather rail) frames the stage edge. It must stay understated and never compete with the issue.
   - **Performance.** The felt must not repaint on scroll or animation: put it on a non-scrolling layer behind the issue.
2. **Printed seat outlines.** Each other player's seat gets a white printed outline drawn on the felt, generated dynamically from the seat layout.
   - **Shape and look.** A rectangle with slightly rounded corners, the size and shape of the seat's card spot, rotated with the seat. The stroke is bold, about 4–6 px scaled with the seat size, coloured `--felt-print` (near-white), with a faint softness like ink printed on felt, as in the owner's mockup.
   - **Follows the seats.** Outlines appear and disappear with players, and move and turn smoothly when the layout changes, honouring reduced motion.
   - **Replaces the dashed placeholder.** A locked card slides into its outline. On reveal it flips inside the outline. Observers get a dimmed outline with no card.
   - **Printed betting arcs.** Each side gets one printed arc line in the same ink, like a casino table's betting line.
     - It runs between that side's seats and the issue, following the same curve as the seat path with a constant gap.
     - It ends just past the first and last seat on that side.
     - Generate it from the seat geometry (an SVG path recomputed with the layout), and hide it when that side has no seats.
     - Nothing else is drawn: no full oval, no top or bottom lines.
   - **Overflow chip.** The "+N" chip sits on the felt in the same printed style.
3. **Everything readable on felt.**
   - **Names and pills.** Seat name labels, presence dots and vote pills stay readable on green: white text with a subtle shadow, meeting WCAG AA contrast against the felt in both themes. Add the check to the a11y specs.
   - **Issue panel.** It is frosted glass over the felt: a translucent surface in the theme's card colour with `backdrop-filter: blur()` and saturation, so the felt and seat print show through softly blurred, plus a soft drop shadow.
     - Pick the opacity so body text, tables, chips and links meet WCAG AA contrast against the panel in both themes. Assert this in a spec by sampling the rendered background behind the text.
     - Without `backdrop-filter` support, fall back to a more opaque panel.
     - The blur layer must not repaint while the issue scrolls: blur a fixed layer and scroll the content inside it.
   - **Overlay.** The results overlay keeps its translucent blur over the issue, and its piles read as cards laid on the felt.
   - **Hand.** The hand's dock bar takes the rail's material, so the hand reads as held over the table edge.
4. **Phones.** The seat row stays a row, but it sits on a strip of the same felt with printed outlines.

## Check

- `./build.sh test`
- `./build.sh fake-e2e "poker-*/*.spec.js"` on all four projects, with an added contrast assertion for seat labels on felt
- `./build.sh e2e "real/poker*.spec.js"`
- `PREVIEW_PORT=5200 ./build.sh screenshots`

Visual baselines are regenerated on macOS and Linux. Before declaring done, look at screenshots in light and dark at 1440×900, 1920×1080 and 1280×800, and on a phone, with 3, 6 and 12 players, both voting and revealed. Judge them as the owner would: does it feel like a real poker table, and is the issue still the clear focus?
