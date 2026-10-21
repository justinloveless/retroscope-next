# UF7: server runtime hardening (from wave 3 verification)

UF7 owns: `src/Retroscope.Server/Realtime/{Actors,Transport}/**`, `src/Retroscope.Server/Hosting/SecurityHeaders.cs`, `src/Retroscope.Server/Endpoints/{BlobEndpoints,AuthEndpoints}.cs`, `src/Retroscope.Server/Data/Repos/BlobsRepo.cs`, `build.sh`, their tests under `tests/Retroscope.Integration.Tests/{Actors,Transport,Hosting,Blobs,AuthCore}/**`, and `docs/decisions/{U13,U14,U16,U20}.md` (append-only).

1. **Actors (U13):** `RoomActor.View()` on a missing room returns a clean not-found result, never a NullReferenceException.
2. **Actors:** `RecheckAccessAsync` handles a cursor-load failure by restarting that one peer, not poisoning the room.
3. **Actors:** `TryPurge` refuses while the internal queue holds items (resume subs, asks), so a purge never races a pending load.
4. **Actors:** replace the fixed 100-retry loop on closed actors with a wait on the next incarnation's start (bounded by a timeout that surfaces a retryable `server_busy`).
5. **Actors tests:** remove `.Result` blocking in the `PokerRuntimeTests` constructor (use `IAsyncLifetime`).
6. **Transport (U14):** synchronize `_abortTimer` assignment and disposal; read `_viewer` with `Volatile`; make the hello timer and `Hello` mutually exclusive (a hello that wins sends welcome; a timer that wins closes 4400 and no welcome follows).
7. **Hosting:** `SecurityHeaders` sends `Strict-Transport-Security: max-age=63072000; includeSubDomains` on HTML responses when `PUBLIC_URL` is https (§e). Test both cases.
8. **build.sh:** `aot-smoke` signs in (create a user through the real signup route) and proves `/ws` hello/welcome and protocol PING/PONG on the native AOT binary.
9. **Blobs (U20):** `StageAllowsAdding` calls `Core.Retro.StageRules.AllowsContent` instead of copying it; the GC EXPLAIN test runs the repo's real SQL; add a test for the GET read rate limit; parenthesize the `GetAsync` condition; note the create-in-place rule in the decision record.
10. **Auth (U16):** signup on a taken email does the same database work and timing shape as a new email (no extra `FindIdAsync` round trip on the taken path). Add a timing-parity test with a tolerance like the login one.

**Check:** `dotnet test tests/Retroscope.Integration.Tests --filter "Category=Actors|Category=Transport|Category=Hosting|Category=Blobs|Category=AuthCore"`, `./build.sh aot-smoke`, and `./build.sh test` green.
