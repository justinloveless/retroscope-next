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
   error and exits 2. `tests/web-unit/golden-cli.test.js` (revision 2, item 11) runs `--orig` and
   `--orig --verify` and asserts exit code 2 and the usage text.

## Item 6 and item 10: Jira-length timing

`MarkupJiraLengthTimingTests` runs 13 hostile 32,000-character bodies in every dialect: nested and
unclosed emphasis, unclosed links, mention runs and broken mentions. Each must parse in under 200 ms. The time
is the fastest of three runs, in a collection that does not run in parallel with others, so a GC pause or a
busy CI core does not make the test fail.

In revision 1, nine rows (`[[[[…`, `[**a[**a…`, `[[mention:[[mention:…`, each in three dialects) took
0.1–4 s, because `ScanLink` walked from every `[` to the next `]` or newline, and were committed with `Skip`.

**Fix (revision 2).** `InlineParser` (C#) and `parse.js` build, on first use per paragraph, two arrays in one
right-to-left pass: for every index the next `]` or newline, once counting every character (`ScanLabel`, used by
mentions and images) and once stepping over backslash escapes exactly as `ScanLink` did. Escapes make the
answer depend on where the scan starts, so the escaped array is defined by the same recurrence as the old loop
(`\` + ASCII punctuation jumps two) rather than by a single "is this `]` escaped" flag. `ScanLink` and
`ScanLabel` are now O(1). All 39 rows run, none skipped. In Node 20, the 32,000-character `[` bodies now parse in
1–10 ms (they took about 1 s).

**Parity.** The shared fixture corpus and both fuzz suites pass unchanged. In addition, 200,000 random strings
from a bracket-heavy alphabet (`[ ] \ \n ( ) ! * _` backticks, link, mention and image fragments, 100-character
runs) were parsed in all three dialects by the old and the new `parse.js`; every Doc was identical. The C#
change is the same recurrence.

## Integration with wave 2

Merging main brought in U8's retro domain, which called `IAnonTagger` with `Guid`s. A separate
`fix(integration)` commit passes the typed ids in `RetroRoles`, `RetroDecider`, the test `FakeTagger` and
`CardTests`. It changes no behaviour (`.Value` was unwrapped at the call); without it the branch does not build.
