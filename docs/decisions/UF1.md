# UF1 decisions: wave 0–1 follow-ups

Items are numbered as in `docs/followups/UF1.md`.

## Done as specified

1. **Spotlight FKs.** Both deferred-FK tests now open the transaction, assert that the `DELETE` succeeds and
   removes one row, and assert that `CommitAsync` is the call that throws 23503. If either FK is not deferrable,
   the `DELETE` itself throws and the test fails. The holder test also asserts that the principal survives.
2. **U2 decisions.** The conventions are numbered 1–5 in order. The Check count is 385, recounted on main after
   wave 1.
3. **`Sealer.TryOpen`** takes `string? sealedValue` and returns false for null.
4. **`IAnonTagger`** takes `BoardId`/`PrincipalId`. A fixed vector (`TokenTests.Anon_tag_matches_the_pinned_vector`,
   key bytes 0..31) pins the output. The expected value was computed independently with Python's `hmac`, and the
   old `Guid` signature gave the same bytes.
5. **U3 decisions** record that `Keyring.Mac` HMACs written before and after a rotation do not match.
7. **Banned sinks.** The test reads every file under `web/src` (`readdir` recursive) and runs `findBannedSinks` on
   it. It no longer depends on the bundle graph. A sink planted in an orphan module was caught.
8. **Router.** `go()` records the navigation's token. When a newer navigation has started, its failure is dropped:
   no `onError` and no `location.assign` of the stale URL. The test runs with and without `onError`.
9. **`make-golden.mjs --orig`** without a directory (missing, or followed by another `--flag`) prints a usage
   error and exits 2. This was checked by hand. No automated test was added, because UF1 owns only the script
   itself under `tests/golden`.

## Item 6: Jira-length timing, partly blocked on U4

`MarkupJiraLengthTimingTests` runs 13 hostile 32,000-character bodies in every dialect: nested and
unclosed emphasis, unclosed links, mention runs and broken mentions. Each must parse in under 200 ms. The time
is the fastest of three runs, in a collection that does not run in parallel with others, so a GC pause or a
busy CI core does not make the test fail.

Thirty rows pass. Nine rows fail on main and are committed with
`Skip = "U4 follow-up … ScanLink rescans to the next ']' from every '['"`:

| Body (32,000 chars) | Debug test run | Release |
|---|---|---|
| `[[[[…` | 2.6–4.2 s | ~0.5 s |
| `[**a[**a…` | 0.7–1.2 s | ~0.15 s |
| `[[mention:[[mention:…` | 0.56–0.82 s | ~0.11 s |

**Cause.** `InlineParser.Link(i)` memoises its result per position, but `ScanLink` walks from `i + 1` to the next
`]` or newline. When the text has no `]`, every `[` scans to the end of the line, so the cost is O(n²). The same
holds for `ScanLabel` (mentions, images) as far as its 100-character cap. U4's card-sized bound (8,000 chars,
2 s) hides this.

**Why not fixed here.** The parser is `src/Retroscope.Core/Markup/InlineParser.cs`, owned by U4. UF1 owns only
`tests/Retroscope.Core.Tests/Markup/**` for this item.

**Suggested fix (U4).** Precompute, once per paragraph, the position of the next `]` or `\n` for every index (one
right-to-left pass), skipping escapes the way `ScanLink` does. `ScanLink` then reads the label end in O(1).
After that, delete the `Skip`s. U5's `web/src/markup/parse.js` has the same shape: on Node 20, 32,000 `[` take
about 1 s. It needs the same change to keep parity.
