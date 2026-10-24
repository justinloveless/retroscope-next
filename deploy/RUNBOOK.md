# Retroscope operations runbook

How Retroscope runs on the VPS under Coolify, how to change it, and how to get it back. The design rationale is in
`docs/DESIGN.md` §i (compose, Postgres tuning, memory budget) and §h.6 (cutover). Decisions made while writing this are
in `docs/decisions/U43.md`.

Facts marked **(measured locally)** come from `deploy/compose.test.yml` on a development machine. Facts marked
**(measure on the VPS)** have not been checked on the target host yet; the checklist at the end lists them.

## Contents

1. [Asset licences](#1-asset-licences)
2. [Host requirements](#2-host-requirements)
3. [Deploying with Coolify](#3-deploying-with-coolify)
4. [Environment variables](#4-environment-variables)
5. [Sign-in providers](#5-sign-in-providers)
6. [Key rotation](#6-key-rotation)
7. [Logs](#7-logs)
8. [Backups and restore](#8-backups-and-restore)
9. [Cutover from the original app](#9-cutover-from-the-original-app)
10. [Login throttling and the bcrypt trade-off](#10-login-throttling-and-the-bcrypt-trade-off)
11. [Schema changes](#11-schema-changes)
12. [Compression](#12-compression)
13. [Health and troubleshooting](#13-health-and-troubleshooting)
14. [Local rehearsal and recorded results](#14-local-rehearsal-and-recorded-results)
15. [Measure on the target VPS](#15-measure-on-the-target-vps)

## 1. Asset licences

Every shipped asset has a recorded source in `THIRD_PARTY.md`. The card art (`web/assets/cards/`, `web/assets/cards.png`)
and the app icons and logo are the project owner's own or licensed work (`THIRD_PARTY.md`; `docs/decisions/U37.md`,
decision 9). Before shipping any new third-party asset, add a section for it there.

The optional timer track (`MUSIC_FILE`) is not a shipped asset: the deployer mounts it on the VPS, and its licence is
theirs to hold (`deploy/COOLIFY_SETUP.md` §5.12, `docs/decisions/UX13.md`). Never commit it to the repository.

## 2. Host requirements

- **2 vCPU / 2 GB RAM minimum.** Coolify's control plane needs about 2 CPU / 2 GB itself and runs on the same box
  (deployment precondition, user-confirmed 2026-10-20).
- **Our slice is 1 vCPU and 1 GB.** `deploy/docker-compose.yml` caps app + db + backup at 0.6 + 0.35 + 0.05 CPUs and
  200 + 288 + 48 MiB. The importer (profile `import`, 0.6 CPU, 256 MiB) only runs while the app is stopped, in its
  place. `DeployGuardTests` fails the build if these limits grow past the slice. Coolify, Traefik and the OS use the rest.
- **A 2 GB swapfile with `vm.swappiness=10` is required** as an OOM backstop, not as working memory:

  ```sh
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf && sysctl --system
  ```

- Disk: the database, 7 daily dumps in the `pgbackups` volume, and container logs (capped at 3 × 10 MB per service).

## 3. Deploying with Coolify

**The VPS never builds images.** `.github/workflows/images.yml` builds `ghcr.io/<owner>/retroscope` and
`ghcr.io/<owner>/retroscope-import` on GitHub runners after the AOT smoke test, and prints both digests in the run
summary. On a push to `main`, `ci` calls it once the tests pass. The app image packages the server binary the smoke
test probed. The compose file pulls them by digest; it contains no `build` section (a test enforces this).

First deploy:

1. In Coolify, create a **Docker Compose** resource from this repository with compose file
   `deploy/docker-compose.yml` (or paste the file in raw mode).
2. Fill in the environment variables (section 4). Variables written `${NAME:?}` in the file are required; Coolify
   blocks the deploy until they have values.
3. Under **Domains**, attach the public domain to service `app`, port `8080`, scheme https. Coolify's Traefik
   terminates TLS and passes WebSockets through. Do not publish ports; the db service has none.
4. Deploy. The app takes the leader lease, runs migrations and creates the `retroscope_app` role and grants as
   `retroscope_owner`, then answers `/healthz`.

Each release is automatic. When `ci` passes on a push to `main`, `.github/workflows/deploy.yml` takes the digests from
that `ci` run's `image-digests` artifact (never from a tag), pins the resource to that commit, sets `APP_DIGEST` and
`IMPORTER_DIGEST` through the Coolify API, redeploys, and checks `/healthz` and the resource status
(`deploy/COOLIFY_SETUP.md` section 11). The production token is in the `production` GitHub environment, which only
`main` can use. To redeploy or roll back, run that workflow by hand from `main` with a commit SHA whose `ci` run passed;
the commit's compose file comes with it, and its artifact must still exist (90 days). Unverified branches go to the
separate preview resource through `.github/workflows/preview.yml` (adding the `preview` label to a PR, or a manual run);
it runs on its own server and Coolify team, with its own token, database and secrets, and holds one branch at a time. If
Actions or the API is down, set the resource's git commit and the two digests (the hex after `sha256:` from the `ci` run
summary of the push to `main`) in Coolify and redeploy by hand.

Coolify redeploys compose apps by **stop and recreate**; there are no rolling updates. On stop, the app sends every
socket `bye` with a spread `retryMs` and closes it with 1012; clients reconnect and resend unacknowledged commands. With
`stop_grace_period: 8s` and a 7 s shutdown budget, a redeploy is a short pause, not an outage (section 14). Because the
old and new binaries never overlap, every migration must be quick and expand/contract-compatible with the previous
release (section 11).

`deploy/compose.build.yml` overrides the images with local builds. The production file's required variables must still
have values to merge it, for example:
`GHCR_OWNER=local APP_DIGEST=0 PUBLIC_URL=… docker compose -f deploy/docker-compose.yml -f deploy/compose.build.yml build`.

## 4. Environment variables

Required unless a default is shown. Generate each 32-byte key with `openssl rand -base64 32`.

| Variable | Meaning |
|---|---|
| `GHCR_OWNER` | Lower-case GitHub owner of the images (`ghcr.io/<owner>/…`). |
| `APP_DIGEST`, `IMPORTER_DIGEST` | Image digests (hex after `sha256:`) from the images workflow. `IMPORTER_DIGEST` only matters for the `import` profile. |
| `PUBLIC_URL` | The public origin, e.g. `https://retro.example.com`: scheme and host only, no path. It sets the CSP `connect-src`, the `Origin` check for CSRF and WebSockets, links in mail, OIDC redirect URIs and HSTS (sent only when https). |
| `DB_OWNER_PASSWORD` | Password of `retroscope_owner`, the database owner. Postgres is initialised with it; the migrator, backups and the importer use it. |
| `DB_APP_PASSWORD` | Password the migrator sets on the `retroscope_app` login role, which the app uses for everything else (`statement_timeout` 5 s, `idle_in_transaction_session_timeout` 30 s, set per role). |
| `APP_KEYS` | The keyring, `"k1:<base64 32 bytes>"`, comma-separated, newest first (section 6). Seals Jira credentials, link-invite tokens and the OIDC state cookie, and keys the audit-log email HMAC. |
| `ANON_TAG_KEY` | Base64 of 32 bytes. Tags anonymous cards and comments so their authors can still edit them. **Never rotate it** unless it leaks: a new key orphans every existing anonymous card from its author. |
| `SMTP_HOST`, `SMTP_PORT` (587), `SMTP_FROM` | Mail submission. `SMTP_FROM` is `"Retroscope <noreply@example.com>"` or a bare address. |
| `SMTP_SECURITY` (by port) | `starttls`, `tls` (implicit TLS) or `none`. Empty means `tls` on port 465 and `starttls` otherwise. `none` is for local Mailpit only. |
| `SMTP_USER`, `SMTP_PASSWORD` (empty) | Set both or neither. |
| `TRUSTED_PROXY_CIDRS` | The subnet Traefik reaches the app from. Only peers inside it may set the client address (rightmost `X-Forwarded-For` hop, `ForwardLimit=1`). With it empty no proxy is trusted, every request appears to come from Traefik, and per-IP limits apply to everyone at once. Find it on the VPS: `docker inspect <app container> -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}'`, then `docker network inspect <the network shared with coolify-proxy> -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}'`. Coolify creates a network per compose resource **(check on the VPS which network the proxy joins)**. |
| `CLIENT_IP_HEADER` (empty) | Set to `CF-Connecting-IP` only when Cloudflare fronts Traefik; the header is then read instead of `X-Forwarded-For`, under the same trusted-peer rule. |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` (empty) | Google sign-in; set both or neither (section 5). |
| `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET` (empty) | Microsoft Entra sign-in; set both or neither. |
| `ENTRA_TENANT` (`organizations`) | The authority tenant segment. |
| `ENTRA_ALLOWED_TENANTS` (empty) | Comma-separated tenant ids allowed to sign in. **Empty disables Entra.** |
| `ENTRA_TENANT_DOMAINS` (empty) | `tid=acme.com\|acme.io,…`: email domains trusted for each tenant without `xms_edov`. |
| `JIRA_HOST_ALLOWLIST` (empty) | Extra exact Jira host names beyond `*.atlassian.net`. |
| `BLOB_BYTES_MAX` (2147483648) | Global ceiling on all board images together, in bytes. |
| `MUSIC_FILE` (empty) | Absolute path, inside the container, of an optional retro timer track (`.mp3`, `.ogg`, `.oga` or `.m4a`, up to 32 MiB), served at `/media/timer-music`. Checked once at startup: a missing, unreadable, oversized or wrongly typed file logs `Timer music is off: <reason>` and the timer plays its generated music. Needs the `volumes` mount in the compose file (COOLIFY_SETUP §5.12). A file overwritten in place (same format) is served from the next request; a new path or format needs a restart. |
| `LEADER_ACQUIRE_SECONDS` (60) | How long a starting app waits for the leader lease before exiting. Not in the compose file; add it only to change it. |
| Importer only | `IMPORT_SOURCE_PG`, `IMPORT_SEAL_PUBKEY`, `IMPORT_ADMIN_EMAILS` (comma-separated emails that become app admins; any other `role='admin'` in the source is ignored and reported), `IMPORT_STORAGE_URL`. Remove them after cutover. `retroscope-import verify` also needs the production `APP_KEYS` and `ANON_TAG_KEY`. See `deploy/IMPORT_RUNBOOK.md` (U46). |

The image sets the GC limits (`DOTNET_GCHeapHardLimit` 128 MiB, workstation non-concurrent GC, `GCConserveMemory=5`)
in the compose file; leave them.

## 5. Sign-in providers

- **Google:** OAuth client of type *Web application*. Authorised redirect URI `PUBLIC_URL/auth/oidc/google/callback`.
- **Microsoft Entra:** app registration with redirect URI (Web) `PUBLIC_URL/auth/oidc/entra/callback`. Under
  *Token configuration*, add the optional ID-token claims **`email`** and **`xms_edov`**. Without `xms_edov` an Entra
  email is trusted only for domains listed in `ENTRA_TENANT_DOMAINS`; otherwise the account is never linked by email.
  Put every tenant id that may sign in into `ENTRA_ALLOWED_TENANTS`.
- Providers with no credentials do not appear on the sign-in screen.

## 6. Key rotation

`APP_KEYS` lists keys newest first. The first key seals new boxes and keys the audit HMAC; every listed key still opens
what it sealed.

1. Generate a key: `openssl rand -base64 32`. Pick a new id (`k2`, `k3`, …).
2. Set `APP_KEYS="k2:<new>,k1:<old>"` and redeploy.
3. On start, the leader re-seals every stored Jira credential under `k2` (`Credentials.RotateAsync`, run once by
   `CredentialRotation` whenever `APP_KEYS` lists more than one key). The log shows
   `Re-sealed N Jira credential boxes under the active key; M could not be opened by any key`. Check:
   `docker exec <db> psql -U retroscope_owner -d retroscope -c "select key_id, count(*) from team_jira_config group by 1"`.
4. Keep `k1` listed for **30 days**: link-invite tokens are sealed with the key current when the invite was created, and
   a link invite lives at most 30 days. OIDC state cookies live minutes.
5. Remove `k1` and redeploy.

Side effects: `audit_log.email_hmac` values change with the first key, so audit rows before and after a rotation do
not correlate by email. If a key leaked, rotate at once, drop the old key without waiting (open link invites then stop
working and must be recreated), and ask team owners to rotate their Jira API tokens in Atlassian.

## 7. Logs

The app writes JSON lines to stdout (UTC timestamps, Information and up). Read them in Coolify or with `docker logs`;
the json-file driver keeps 3 × 10 MB per container.

- One line per request, category `Retroscope.Requests`: `"GET /retro/ABC123 200 in 3.1 ms"` with `Method`, `Path`,
  `Status` and `ElapsedMs` fields. No query strings, headers or bodies; `/healthz` is not logged. WebSocket sessions
  log once, when they end, with status 101 and their duration.
- The legacy invite path is logged as **`/invite/[redacted]`** (S23). ASP.NET Core's own per-request categories
  (`Microsoft.AspNetCore.*`) are held at Warning because they print raw URLs and redirect targets.
- `Logging__Console__FormatterName=simple` switches to plain text; `Logging__LogLevel__Default=Warning` quietens it.

## 8. Backups and restore

The `backup` sidecar runs `pg_dump -Fc` at start and then daily into the `pgbackups` volume as `rs-<weekday 1-7>.dump`,
so the volume holds the last 7 days. That volume is on the same disk, so **copy dumps off the box daily**, for example
from another machine:

```sh
ssh vps 'docker run --rm -v <project>_pgbackups:/b alpine cat /b/rs-$(date +%u).dump' > rs-$(date +%F).dump
```

Take a dump on demand: `docker exec <backup container> sh -c 'pg_dump -Fc -h db -U retroscope_owner retroscope > /backups/manual.dump'`.

Restore (tested by `deploy/drills/backup-restore.sh`, section 14):

1. Stop the app in Coolify (leave db running).
2. Recreate the database empty, as `retroscope_owner`:
   `docker exec <db> psql -U retroscope_owner -d postgres -c "drop database retroscope with (force)" -c "create database retroscope"`.
   For a restore into a *fresh* Postgres, start the same image with the same `POSTGRES_*` variables instead.
3. `docker exec -i <db> pg_restore --exit-on-error --no-owner --no-acl -U retroscope_owner -d retroscope < rs-N.dump`.
   `--no-owner --no-acl` is safe: the app re-creates `retroscope_app` and re-grants on every start.
4. `docker run --rm --network container:<db> -e DATABASE_URL=… -e DATABASE_OWNER_URL=… -e PUBLIC_URL=… -e APP_KEYS=… -e ANON_TAG_KEY=… ghcr.io/<owner>/retroscope@sha256:<digest> spot-load --sample 20`
   loads 20 random rooms of each kind through the real stores. It refuses to run while an app holds the lease.
5. Start the app. The migrator's checksum guard refuses to start if the dump's `schema_migrations` disagrees with the
   binary's migration files.

## 9. Cutover from the original app

From §h.6, with the steps this deployment adds:

1. Put the old app into maintenance. 2. Take a final Supabase `pg_dump`. 3. Off-box: `retroscope-import plan`,
`fetch-storage`, `run`, then `verify` (needs the production `APP_KEYS` and `ANON_TAG_KEY`) into a local Postgres
(`deploy/compose.build.yml --profile import-local`). 4. Review the report (admins, Jira configs needing rotation,
rotated board passwords, merged emails, nulled hashes). 5. Stop the app on the VPS and restore the data dump as in
section 8 steps 2–3. 6. **Run `spot-load --sample 20`** (section 8 step 4) with the app still stopped. 7. Start the app,
then `cutover`. 8. Switch the Coolify domain or DNS. 9. Tell team owners to rotate Jira tokens in Atlassian; rotate the
Supabase database password and keys; remove the `IMPORT_*` variables.

Old links keep working: `/retro/:code`, `/poker/:code`, `/teams/:teamId/poker/:code` (UUID codes accepted), and
`/invite/:token`, which answers `302 /invite#t=:token` (`Cache-Control: no-store`) so the token leaves the path.

## 10. Login throttling and the bcrypt trade-off

Every bcrypt hash and verify (cost 10) goes through one gate, one at a time, so a burst of logins can never starve the
single core. A caller waits at most 2 s and at most 50 wait at once; past either limit the endpoint answers
`503 busy` with `Retry-After: 2`. The web client never retries a POST on its own, so the user sees the error and
signs in again.

- Throughput is one hash at a time: about **10 logins/s** on the test stack's 0.6-CPU app **(measured locally, ~100 ms
  per login)**; U16 measured ~12 hashes/s in a Debug build on Apple silicon. **(measure on the VPS.)**
- The trade-off: a flood of password attempts, even with wrong passwords and from many addresses, makes legitimate
  users wait or retry for as long as it lasts. Nothing else slows down (WebSockets, rooms and pages do not hash).
- Per account: from the 5th failure an (account, /24 or /48 prefix) pair backs off 1, 2, 4, 8, then 15 minutes. Past 50
  failures an hour from all addresses together, password login for that account answers 429 from everywhere and the
  owner is mailed a sign-in link. Unknown addresses are throttled identically, so the limits reveal nothing.
- Per IP: 10 logins a minute, 5 signups an hour. These limits use the client address from section 4, so a wrong
  `TRUSTED_PROXY_CIDRS` puts every user behind one shared limit.
- These counters are in memory and reset on redeploy.

## 11. Schema changes

`db/migrations/0001_init.sql` is **frozen** (`DeployGuardTests.The_first_migration_is_frozen` pins its SHA-256, and the
migrator refuses to start against a database where an applied file changed). Every change is a new `0002_*.sql`,
`0003_*.sql`, … file. Because a redeploy stops the old binary before the new one starts, a migration must finish
quickly and leave the schema usable by the previous release (expand first, contract in a later release).

## 12. Compression

- Hashed static assets are precompressed at build time (`.br` quality 11, `.gz` level 9) and served with
  `Cache-Control: public, max-age=31536000, immutable`.
- **Dynamic responses are not compressed** (decision in `docs/decisions/U43.md`): measured HTML shells are 2.1–3.3 KB
  and typical API JSON under 300 bytes, so gzip saves about 1–2 KB per page, while compressing authenticated responses
  that mix secrets with reflected input invites BREACH-style attacks. WebSocket frames are never compressed.
- Keep Traefik compression off for this resource too. Whether Coolify adds a compress middleware by default is
  **(check on the VPS)**: `curl -sI -H 'Accept-Encoding: gzip' https://<domain>/api/me` must show no `content-encoding`.

## 13. Health and troubleshooting

- `/healthz` is 200 only while the process holds the leader lease, the database answers and shutdown has not begun.
  The compose healthcheck runs `/app/retroscope healthcheck` (the chiseled image has no shell or curl).
- A second app instance never runs: it waits up to `LEADER_ACQUIRE_SECONDS` for the lease and exits.
- `docker exec <db> psql -U retroscope_owner -d retroscope` for a shell. `PGDATA` is
  `/var/lib/postgresql/18/docker`, inside the `pgdata` volume mounted at `/var/lib/postgresql` (checked on the pinned
  `postgres:18-alpine`, 18.6, with data checksums on).
- The connection strings set `GSS Encryption Mode=Disable`: Npgsql otherwise probes GSSAPI, which the chiseled image
  lacks, and prints `Cannot load library libgssapi_krb5.so.2` at start.

## 14. Local rehearsal and recorded results

`deploy/compose.test.yml` is the production shape built locally: the same db settings and container limits (a test
keeps them equal), behind Traefik v3.7.16 with a self-signed certificate on `https://localhost:18443`, plus Mailpit and
FakeJira. It runs the e2e suite (`./build.sh e2e "smoke/*.spec.js"`) and these drills:

| Drill | Command |
|---|---|
| Idle memory | `./deploy/drills/idle-memory.sh [settle-seconds]` |
| Redeploy mid-vote | `node deploy/drills/redeploy-mid-vote.mjs` |
| Backup and restore | `./deploy/drills/backup-restore.sh` |
| WebSocket idle | `node deploy/drills/ws-idle.mjs --seconds 180` |
| Throughput and latency | `node deploy/drills/load.mjs [--rooms 20 --players 8 --rate 0.3 --seconds 60]` |

Results on 2026-10-21 (Apple silicon, Rancher Desktop VM, arm64 images; the host was shared and its load average was
20–150 during these runs, so treat latency as an upper bound):

- **Idle memory (measured locally):** app 26–37 MiB cgroup usage, 68 MB process RSS (target < 120 MB). db 35–39 MiB
  excluding page cache, 91 MiB including it (15 MiB anonymous, 18 MiB shared buffers in use, 67 MiB file cache)
  (target < 180 MB). Traefik (no Coolify configuration) 16–38 MiB.
- **Redeploy mid-vote (measured locally):** 6 players, about 730 commands (votes plus 144 round creations), app
  stopped and recreated mid-vote. Lost acked commands 0, duplicate rounds 0, wrong final votes 0, unacked at the end 0,
  nacks 0. All 6 tabs received `bye` (1012) and were back in the room within **0.6 s** (budget 15 s); `compose up
  --force-recreate --wait` returned after 6–7 s, mostly waiting for the healthcheck interval.
- **Backup and restore (measured locally):** dump by the sidecar, restore into a fresh container, row counts equal in
  all 40 tables, `spot-load` passed, the app started healthy on the copy.
- **WebSocket idle through Traefik (measured locally):** a guest socket sending nothing stayed open for 150 s, answering
  the server's PINGs every ~19 s. Traefik's defaults did not close it.
- **Throughput and latency (measured locally, not a gate):** 20 rooms × 8 players (160 sockets). At 0.3 commands/s
  per player plus 30 s bursts: 34–38 acks/s, ack latency p50 11–20 ms, p95 65–149 ms, p99 182–214 ms. At 2 commands/s
  per player: 147 acks/s, p50 4 ms, p95 83 ms, p99 124 ms. No nacks, no reconnects; peak app 67 MiB, db 64 MiB.
- **Login cost (measured locally):** ~100 ms per login, ~10 logins/s through the hash gate.

## 15. Measure on the target VPS

Nobody has run these on the production host yet. Record the numbers here (with the date) the first time, and again
after Coolify or host upgrades.

- [ ] **Coolify + Traefik memory:** `docker stats --no-stream` and `free -m` after a day of idle running. The §i.3
      budget expects Traefik at 40–60 MB, and app + db + backup within their 536 MiB of limits.
- [ ] **App and db idle RSS:** `./deploy/drills/idle-memory.sh` adapted to the Coolify container names (app < 120 MB,
      db < 180 MB).
- [ ] **WebSocket idle timeout through Coolify's Traefik:** `RS_E2E_BASE_URL=https://<domain> node deploy/drills/ws-idle.mjs --seconds 300`
      (it signs in as a guest named "Idle check"; the guest token expires on its own). It must exit 0.
- [ ] **bcrypt hashes/s:** run `HashGateTests.Bcrypt_throughput_is_recorded` on a machine of the VPS's CPU type
      (`dotnet test tests/Retroscope.Integration.Tests --filter "FullyQualifiedName~Bcrypt_throughput_is_recorded"`;
      it prints hashes/s), or time 20 sequential logins against production with a test account.
- [ ] **Throughput and p99:** run `deploy/drills/load.mjs` against a staging copy of the stack on the VPS (never
      production: it creates 160 accounts), and record acks/s and p50/p95/p99.
- [ ] **Redeploy pause:** redeploy during a test session and confirm clients are back within 15 s.
- [ ] **Trusted proxy subnet:** confirm the network Traefik shares with the app and that `TRUSTED_PROXY_CIDRS` matches
      it: after a login, `audit_log` shows your own address prefix, not Traefik's.
- [ ] **No dynamic compression:** the `curl` check in section 12.
- [ ] **HSTS on HTML:** `curl -sI https://<domain>/` shows `strict-transport-security: max-age=63072000; includeSubDomains`.
- [ ] **Swap:** `swapon --show` lists the 2 GB swapfile; `sysctl vm.swappiness` is 10.
- [ ] **Off-box backup copy** runs daily and a restore from it was rehearsed (section 8).
