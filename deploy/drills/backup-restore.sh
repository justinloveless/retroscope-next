#!/usr/bin/env bash
# Backup and restore drill against the compose.test.yml stack (RUNBOOK "Backups"): takes a dump with the backup
# sidecar exactly as its daily loop does, restores it into a fresh Postgres container, compares row counts table by
# table, runs `retroscope spot-load` against the copy, then starts the app image on it and waits for /healthz.
# Leaves nothing behind.
#   ./deploy/drills/backup-restore.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.rd/docker.sock" ]]; then
  export DOCKER_HOST="unix://$HOME/.rd/docker.sock"
fi
COMPOSE=(docker compose -f "$ROOT/deploy/compose.test.yml")
POSTGRES_IMAGE="postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873"
RESTORE="rs-restore-drill-$$"
RESTORED_APP="rs-restore-drill-app-$$"
WORK="$(mktemp -d)"
cleanup() {
  docker rm -f "$RESTORE" "$RESTORED_APP" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

# Every table in public and archive with its exact row count, one "schema.table count" per line.
COUNTS_SQL="select string_agg(format('%s.%s %s', n.nspname, c.relname,
  (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', n.nspname, c.relname), false, true, '')))[1]::text),
  E'\n' order by n.nspname, c.relname)
  from pg_class c join pg_namespace n on n.oid = c.relnamespace
  where c.relkind = 'r' and n.nspname in ('public', 'archive')"

echo "== dump with the backup sidecar"
started=$(date +%s)
"${COMPOSE[@]}" exec -T backup sh -c 'pg_dump -Fc -h db -U retroscope_owner retroscope > /backups/drill.dump && ls -l /backups/drill.dump'
"${COMPOSE[@]}" exec -T db psql -U retroscope_owner -d retroscope -Atc "$COUNTS_SQL" > "$WORK/live.txt"
"${COMPOSE[@]}" cp backup:/backups/drill.dump "$WORK/drill.dump"
"${COMPOSE[@]}" exec -T backup rm /backups/drill.dump

echo "== restore into a fresh container"
docker run -d --name "$RESTORE" -e POSTGRES_DB=retroscope -e POSTGRES_USER=retroscope_owner -e POSTGRES_PASSWORD=restore \
  -e POSTGRES_INITDB_ARGS=--data-checksums "$POSTGRES_IMAGE" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$RESTORE" pg_isready -h 127.0.0.1 -U retroscope_owner -d retroscope >/dev/null 2>&1 && break
  sleep 0.5
done
docker cp "$WORK/drill.dump" "$RESTORE:/tmp/drill.dump"
docker exec "$RESTORE" pg_restore --exit-on-error --no-owner --no-acl -U retroscope_owner -d retroscope /tmp/drill.dump
docker exec "$RESTORE" psql -U retroscope_owner -d retroscope -Atc "$COUNTS_SQL" > "$WORK/restored.txt"
if ! diff -u "$WORK/live.txt" "$WORK/restored.txt"; then
  echo "backup-restore: row counts differ" >&2
  exit 1
fi
echo "row counts match in $(wc -l < "$WORK/live.txt" | tr -d ' ') tables ($(awk '{s+=$2} END{print s}' "$WORK/live.txt") rows)"

echo "== spot-load and start the app on the restored copy"
env_of() { docker inspect retroscope-test-app-1 --format '{{range .Config.Env}}{{println .}}{{end}}' | grep "^$1=" | cut -d= -f2-; }
app_env=(
  -e "DATABASE_URL=Host=127.0.0.1;Database=retroscope;Username=retroscope_app;Password=restore-app;GSS Encryption Mode=Disable"
  -e "DATABASE_OWNER_URL=Host=127.0.0.1;Database=retroscope;Username=retroscope_owner;Password=restore;GSS Encryption Mode=Disable"
  -e "PUBLIC_URL=http://localhost:8080" -e "APP_KEYS=$(env_of APP_KEYS)" -e "ANON_TAG_KEY=$(env_of ANON_TAG_KEY)"
)
docker run --rm --network "container:$RESTORE" "${app_env[@]}" retroscope:test spot-load --sample 20
docker run -d --name "$RESTORED_APP" --network "container:$RESTORE" "${app_env[@]}" retroscope:test >/dev/null
for _ in $(seq 1 30); do
  docker exec "$RESTORED_APP" /app/retroscope healthcheck >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$RESTORED_APP" /app/retroscope healthcheck
echo "backup-restore drill passed in $(( $(date +%s) - started )) s"
