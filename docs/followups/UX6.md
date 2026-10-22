# UX6: avatar on the card's corner; the hand tucks behind the button bar

This is the owner's feedback on UX5. UX6 owns `web/src/poker/{seats,hand,table}.js`, `web/styles/neotro.css`, `tests/web-unit/poker/**`, and `docs/decisions/UX6.md`.

1. **The avatar moves to the card's bottom-outer corner.** It should cover the card much less than it does today.
   - **Which corner.** A seat's card points its top at the stage centre, so its bottom edge faces the player. The avatar sits on the bottom corner nearer the stage's top or bottom edge: upper-half seats use the upper corner, lower-half seats the lower corner. A seat exactly at the vertical middle uses the lower corner.
   - **Placement.** The avatar is centred on that corner, so at most about a quarter of it overlaps the card face.
   - **Name label.** It stays upright and next to the avatar, and must not cover the card.
   - **Layout.** Recompute the seat ring depth and the label reach (`labelReach()`, the issue padding) so nothing clips against the rail or the frosted panel.
   - **Phones.** The phone seat row keeps the avatar below or beside the small card in the same spirit.
2. **The hand sits behind the button bar.** In the owner's screenshot, the bottoms of the resting hand cards draw over the top edge of the dark button bar.
   - **Stacking.** The fanned cards now sit below the bar: the bar is opaque and covers the cards' lower part, so the hand looks tucked into the rail.
   - **Raised hand.** When the hand rises (hover, focus, drag, touch, or a selected card), the raised cards clear the bar fully and stay fully clickable.
   - **Resting cards.** Their visible strip must still take clicks.
   - **Unchanged.** Drag-to-play, the between zones and keyboard use behave exactly as before.

Quick-iteration rules apply. Update the unit tests that pin seat geometry or label reach, and add a unit test for the corner choice.
