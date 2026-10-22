# UX2: bigger seat cards, gentler side curves, wider between zones

This is the owner's feedback on UX1, after seeing it in the preview. UX2 owns `web/src/poker/**`, `web/styles/neotro.css`, `tests/web-unit/poker/**`, `tests/e2e/fake/poker-*/**`, the poker states in `tests/e2e/screenshots/**`, the poker visual baselines, and `docs/decisions/UX2.md`. You may append to `docs/decisions/UX1.md`.

1. **Bigger seat cards.** Seat cards are too small: today `SEAT_SIZES.full.card` is 0.55, about 39×52 px. Make them read as real cards at a glance.
   - Seat cards use scales the pixel art renders crisply at: 1.5× (107×143) when there is room, then 1×, then 0.75×, then the avatar-only minimum. Avatars grow in proportion.
   - At 1440×900 with 6 players, every seat shows at least 1× cards.
   - Re-tune `SEAT_SIZES`, `MAX_DEPTH_PX` and `MAX_DEPTH_SHARE` as needed. The issue column must stay at least 560 px wide at 1440×900, and fewer seats get bigger cards.
2. **Gentler, separate curves.** The single ellipse pulls the top and bottom seats too far in toward the centre.
   - Replace it with **two independent curves**, one per side. Each is a shallow circular arc whose chord runs vertically along that side, between the top and bottom bands.
   - Each arc bows toward the stage centre by a small sagitta: at most `min(56 px, 5 % of stage width)`. A seat at the top or bottom of a side stays almost as close to the side edge as the middle seat.
   - Seats are spaced evenly by arc length and fill the two sides alternately, as before.
   - Rotation still turns each card's top toward the stage centre with `atan2`.
   - The top and bottom edges stay clear apart from a small corner spill.
   - Update the golden geometry tests:
     - Each side's horizontal spread of seat centres is no more than the sagitta plus 2 px.
     - No seat lies outside the stage.
     - Nothing lies in the bands.
     - No two seats overlap.
     - Every angle points within 1° of the centre.
     - Cover 1–16 seats on three stage sizes.
3. **No visible table.** Remove the drawn oval (the `.nt-board` radial felt) completely. The seats alone imply the table. Keep the issue panel's own background.
4. **Wider "between" zones.**
   - **Twice as wide.** While dragging a card, each between zone beside it is twice its current width. Today it is `gapRect` with `ZONE_MARGIN`.
   - **Bigger gap.** The neighbouring cards of the dragged card slide outward so the gap is visibly larger.
   - **No conflicting targets.** The zones must never overlap each other or the drop-to-play area, and the hold time stays 2 s.
   - **Tests.** Add or adjust unit tests for the zone rectangles, and keep the drag and between-hold e2e specs green on all four browser projects, mobile WebKit touch included.

## Check

- `./build.sh test`
- `./build.sh fake-e2e "poker-*/*.spec.js"` (all four projects)
- `./build.sh e2e "real/poker*.spec.js"`
- `PREVIEW_PORT=5200 ./build.sh screenshots`

Visual baselines are regenerated on macOS and Linux. Before declaring done, look at screenshots at 1440×900, 1920×1080 and 1280×800 with 3, 6 and 12 players, with the votes both locked and revealed, and fix anything cramped, clipped or unbalanced.
