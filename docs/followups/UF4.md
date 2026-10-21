# UF4: server infrastructure fixes from wave 2 verification

UF4 owns these paths for these changes only: `src/Retroscope.Server/Hosting/RateLimiter.cs`, `src/Retroscope.Server/Jira/{JiraThrottle,JiraCache}.cs`, `src/Retroscope.Importer/Source/**` (preflight only), `src/Retroscope.Importer/Retroscope.Importer.csproj`, `tests/Retroscope.Integration.Tests/{Hosting,Jira*}/**` and `tests/Retroscope.Importer.Tests/Infrastructure/**` (tests for these items), `tests/Retroscope.Integration.Tests/Infrastructure/PostgresFixture.cs` (or wherever U1's fixture lives), `docs/decisions/{U11,U12,U19,U38,U48}.md` (append-only).

1. **RateLimiter (U11):** `TryAcquire` must not create or touch buckets for any rule once one rule refuses. A caller over the per-IP budget cannot grow the bucket table with fresh recipient keys. Add a test.
2. **JiraThrottle (U19):** a caller that cancels while waiting gets its rate token back. Add a test.
3. **JiraCache (U19):** key in-flight work by (key, typeof(T)), so two callers with different T cannot remove each other's entries. Add a test.
4. **Importer preflight (U38):** an unlisted `auth.*` table or column fails preflight (S25), not just warns. Allow an `--html-textonly <path>` override so operators can extend `html-textonly.json` without a rebuild. Accept the EmbeddedResource line in the importer csproj.
5. **PostgresFixture (U1):** clear the owner-connection pool after migrating each test database (`NpgsqlConnection.ClearPool`), so ~70 databases no longer exhaust Postgres's 100 connections. Add a test creating 120 databases.
6. **Decision records (append):** U12 owns `tests/Retroscope.Integration.Tests/Leadership/**`; U19 notes the Basic credential header is necessarily a string; U48 notes HSTS is deliberately omitted on the plain-HTTP fake.

**Check:** `./build.sh test` green with the new tests present.
