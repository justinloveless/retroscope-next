#!/usr/bin/env bash
# Idle memory of the compose.test.yml stack (DESIGN §i.3 targets: app < 120 MB, db < 180 MB). Waits for the stack to
# settle, then prints docker's view (cgroup usage minus inactive page cache), the app process's RSS, and the db
# cgroup's total with its anonymous, page-cache and shared-memory parts.
#   ./deploy/drills/idle-memory.sh [settle-seconds]
set -euo pipefail
settle="${1:-60}"
if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.rd/docker.sock" ]]; then
  export DOCKER_HOST="unix://$HOME/.rd/docker.sock"
fi

sleep "$settle"
echo "== docker stats (usage excludes inactive page cache)"
docker stats --no-stream --format '{{.Name}} {{.MemUsage}} {{.CPUPerc}}' retroscope-test-app-1 retroscope-test-db-1 retroscope-test-backup-1 retroscope-test-proxy-1
echo "== app process RSS"
docker top retroscope-test-app-1 -o rss,args | tail -n +2
echo "== db cgroup (bytes)"
docker exec retroscope-test-db-1 sh -c 'echo "total $(cat /sys/fs/cgroup/memory.current)"; grep -E "^(anon|file|shmem) " /sys/fs/cgroup/memory.stat'
