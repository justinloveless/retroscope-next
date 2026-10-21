# UF4 decisions: server infrastructure fixes from wave 2 verification

The per-item decisions are appended to the owning units' records: U11 (rate limiter), U19 (throttle, cache, Basic
header), U38 (preflight, `--html-textonly`, csproj), U12 (Leadership tests) and U48 (HSTS on the fake).

## Edits outside the listed paths

The follow-up asks for these behaviours, and each one needs a file the ownership list does not name. The edits are
kept as small as possible:

1. **`src/Retroscope.Importer/Program.cs`** parses `--html-textonly <path>`, documents it in the usage text and calls
   `HtmlTagPolicy.AddTextOnly` before any verb runs. A CLI flag cannot exist without the CLI's option loop. The loop
   became an index loop so that the option can take the next argument.
2. **`src/Retroscope.Importer/Map/expected-columns.json`** gains one `exclude` entry, `auth.instances`. Once an
   unlisted `auth` table fails preflight, the fake Supabase source (which has GoTrue's `auth.instances`, as production
   does) fails `plan` unless the table is listed. Removing the table from the fake would only hide the failure.
3. **`tests/Retroscope.Integration.Tests/Schema/PostgresFixtureTests.cs`** (new) holds the 120-database test next to
   the fixture it tests (`Category=Schema`, `SharedPostgres` collection). It drops its databases at the end, so the
   shared container does not keep them.

## Verification

- With the production changes reverted and the new tests kept, all six new tests fail: two in the rate limiter, two in
  the throttle, one in the cache and the 120-database test, which hit `53300` and then made 13 other Schema/Hosting
  tests fail too. With the changes, they pass.
- `--html-textonly` is tested through the real entry point (`Assembly.EntryPoint`). The tests check that a valid file
  extends the set the histogram uses, that a missing path is a usage error (2), and that an unreadable file refuses
  the run (1).
