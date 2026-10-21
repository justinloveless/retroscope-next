# UF8 decisions: importer robustness

The per-item decisions are appended to the owning units' records: U39 (identity data, merge rules, palette), U40
(codes, null averages, deep selections, guest ids) and U41 (blob creators, markdown links, guests, colours).

## Edits outside the listed paths

UF8 owns `Importer/Map/**`, but items 6 and 7 name code that lives in `Importer/Html/**` (U41's folder). The edits
are kept as small as possible:

1. **`src/Retroscope.Importer/Html/BoardImages.cs`** keeps an `Anonymous` flag next to each blob and passes it to the
   upsert, which now clears `created_by` on conflict only when that flag is set (item 6).
2. **`src/Retroscope.Importer/Html/DocBuilder.cs`**: the `TextToken` regex accepts `mailto:` hrefs, and a matched
   markdown link goes through `IsLinkable` before it becomes a link (item 7).

## Beyond the listed items

`PokerMap.Selection.Parse` used `JsonDocument.Parse` without a catch, so the same jsonb depth limit that broke
`identity_data` could abort a run on one round. It now counts such a value as a malformed selection. This follows the
unit's rule that one bad source row is skipped, counted and reported.

## Verification

- With the production commits reverted and the new tests kept, the tests for items 1, 3, 4, 5 (guest id stored in
  upper case), 6 and 7 (`mailto:`) fail. With the changes, all 252 importer tests pass.
- The new length and control-character cases for markdown links also pass on the old code. The markup normaliser
  already dropped those links. They stay as regression tests, since the converter no longer depends on that.
