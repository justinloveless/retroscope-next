#!/usr/bin/env bash
# Retroscope build entry point (DESIGN §k.1). Verbs:
#   ./build.sh                     restore, build, test, web
#   ./build.sh test                every suite except e2e and soak
#   ./build.sh web                 esbuild bundle into web/dist (emits manifest.json)
#   ./build.sh aot-smoke           AOT publish with zero warnings, probe against a throwaway Postgres,
#                                  chiseled image serves /healthz (needs Docker)
#   ./build.sh fake-e2e <glob>     Playwright specs under tests/e2e/fake/ against tests/web-fake-server
#   ./build.sh e2e <glob>          Playwright specs under tests/e2e/ against deploy/compose.test.yml
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$ROOT/artifacts"
POSTGRES_IMAGE="postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873"
cd "$ROOT"

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'build.sh: %s\n' "$*" >&2; exit 1; }

node_deps() {
  if [[ ! -d node_modules ]] || [[ package-lock.json -nt node_modules ]]; then
    log "npm ci"
    npm ci --no-audit --no-fund
  fi
}

need_docker() {
  docker info >/dev/null 2>&1 || die "Docker is not reachable (set DOCKER_HOST if your engine uses a non-default socket)"
}

free_port() {
  node -e 'const s=require("net").createServer().listen(0,()=>{console.log(s.address().port);s.close()})'
}

wait_http() {
  local url="$1"
  for _ in $(seq 1 60); do
    curl -fsS "$url" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  return 1
}

host_rid() {
  local os arch
  case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) die "unsupported OS $(uname -s)" ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *) die "unsupported architecture $(uname -m)" ;;
  esac
  echo "$os-$arch"
}

# Turns a spec glob into the regular expression Playwright uses to filter files.
glob_to_regex() {
  printf '%s' "$1" | sed -e 's/[.+^$(){}|]/\\&/g' -e 's/\*\*\//@@ANYDIR@@/g' -e 's/\*\*/@@ANY@@/g' -e 's/\*/[^\/]*/g' -e 's/@@ANY@@/.*/g' -e 's/@@ANYDIR@@/(.*\/)?/g'
}

cmd_web() {
  node_deps
  log "web build"
  node web/build.mjs
  [[ -f web/dist/manifest.json ]] || die "web build did not emit manifest.json"
}

cmd_test() {
  node_deps
  log "dotnet build"
  dotnet build Retroscope.slnx

  log "dotnet test (unit, integration, importer)"
  dotnet test --solution Retroscope.slnx --no-build

  log "simulation (PR seeds)"
  dotnet run --no-build --project tests/Retroscope.Sim -- --seeds 2000

  local web_tests=() dir file
  for dir in tests/web-unit tests/web-fake-server; do
    [[ -d "$dir" ]] || continue
    while IFS= read -r file; do
      web_tests+=("$file")
    done < <(find "$dir" -name '*.test.js' -not -path '*/node_modules/*' | sort)
  done
  if (( ${#web_tests[@]} > 0 )); then
    log "web unit tests"
    node --test "${web_tests[@]}"
  fi

  if [[ -f tests/golden/make-golden.mjs ]]; then
    log "golden vectors"
    node tests/golden/make-golden.mjs --verify
  fi
}

# Peak RSS of the publish is recorded for CI runner sizing (DESIGN §12); the VPS never compiles.
aot_publish() {
  local project="$1" out="$2" log_file="$3" timer=()
  case "$(uname -s)" in
    Darwin) timer=(/usr/bin/time -l) ;;
    Linux) [[ -x /usr/bin/time ]] && timer=(/usr/bin/time -v) ;;
  esac
  "${timer[@]}" dotnet publish "$project" -c Release -r "$(host_rid)" -o "$out" --disable-build-servers >"$log_file" 2>&1 \
    || { cat "$log_file"; die "AOT publish of $project failed"; }
  grep -i -E 'maximum resident set size' "$log_file" | sed 's/^ */peak RSS of publish: /' || true
  if grep -E ': warning [A-Z]+[0-9]+' "$log_file"; then
    die "AOT publish of $project produced warnings"
  fi
}

smoke_cleanup() {
  [[ -n "${SMOKE_PID:-}" ]] && kill "$SMOKE_PID" 2>/dev/null
  docker rm -f "$SMOKE_PG" "$SMOKE_APP" >/dev/null 2>&1 || true
}

cmd_aot_smoke() {
  need_docker
  rm -rf "$ARTIFACTS/aot"
  mkdir -p "$ARTIFACTS/aot"

  log "AOT publish: server"
  aot_publish src/Retroscope.Server "$ARTIFACTS/aot/server" "$ARTIFACTS/aot/server.log"
  log "AOT publish: smoke probe"
  aot_publish tests/Retroscope.AotSmoke "$ARTIFACTS/aot/smoke" "$ARTIFACTS/aot/smoke.log"

  local pg_port app_port image_port
  pg_port="$(free_port)"
  SMOKE_PG="rs-aot-smoke-pg-$$"
  SMOKE_APP="rs-aot-smoke-app-$$"
  SMOKE_PID=""
  trap smoke_cleanup EXIT

  log "throwaway Postgres on port $pg_port"
  docker run -d --rm --name "$SMOKE_PG" -p "127.0.0.1:$pg_port:5432" \
    -e POSTGRES_DB=retroscope -e POSTGRES_USER=retroscope_owner -e POSTGRES_PASSWORD=smoke \
    "$POSTGRES_IMAGE" >/dev/null
  for _ in $(seq 1 60); do
    docker exec "$SMOKE_PG" pg_isready -h 127.0.0.1 -U retroscope_owner -d retroscope >/dev/null 2>&1 && break
    sleep 0.5
  done
  local db_url="Host=127.0.0.1;Port=$pg_port;Database=retroscope;Username=retroscope_owner;Password=smoke"

  log "smoke probe"
  SMOKE_DATABASE_URL="$db_url" "$ARTIFACTS/aot/smoke/retroscope-aot-smoke"

  log "server binary serves /healthz"
  app_port="$(free_port)"
  DOTNET_hostBuilder__reloadConfigOnChange=false ASPNETCORE_URLS="http://127.0.0.1:$app_port" DATABASE_URL="$db_url" DATABASE_OWNER_URL="$db_url" \
    PUBLIC_URL="http://127.0.0.1:$app_port" "$ARTIFACTS/aot/server/retroscope" >"$ARTIFACTS/aot/server-run.log" 2>&1 &
  SMOKE_PID=$!
  wait_http "http://127.0.0.1:$app_port/healthz" || { cat "$ARTIFACTS/aot/server-run.log"; die "/healthz did not answer"; }
  HEALTHCHECK_PORT="$app_port" "$ARTIFACTS/aot/server/retroscope" healthcheck || die "healthcheck subcommand failed"
  kill "$SMOKE_PID"
  SMOKE_PID=""

  log "chiseled image serves /healthz"
  docker build -f deploy/Dockerfile -t retroscope:aot-smoke . >"$ARTIFACTS/aot/image.log" 2>&1 \
    || { tail -50 "$ARTIFACTS/aot/image.log"; die "image build failed"; }
  image_port="$(free_port)"
  docker run -d --rm --name "$SMOKE_APP" -p "127.0.0.1:$image_port:8080" retroscope:aot-smoke >/dev/null
  wait_http "http://127.0.0.1:$image_port/healthz" || { docker logs "$SMOKE_APP"; die "chiseled image did not serve /healthz"; }
  docker exec "$SMOKE_APP" /app/retroscope healthcheck || die "healthcheck subcommand failed inside the image"

  log "aot-smoke passed"
}

cmd_fake_e2e() {
  local glob="${1:?usage: ./build.sh fake-e2e <spec-glob under tests/e2e/fake/>}"
  cmd_web
  log "Playwright (fake server): $glob"
  RS_E2E_TARGET=fake npx playwright test --config tests/e2e/playwright.config.js \
    "tests/e2e/fake/$(glob_to_regex "$glob")$"
}

cmd_e2e() {
  local glob="${1:?usage: ./build.sh e2e <spec-glob under tests/e2e/>}"
  need_docker
  node_deps
  log "compose.test.yml up"
  docker compose -f deploy/compose.test.yml up -d --wait
  trap 'docker compose -f deploy/compose.test.yml down -v >/dev/null 2>&1 || true' EXIT
  log "Playwright (compose stack): $glob"
  RS_E2E_TARGET=compose npx playwright test --config tests/e2e/playwright.config.js \
    "tests/e2e/$(glob_to_regex "$glob")$"
}

case "${1:-all}" in
  all) cmd_test; cmd_web ;;
  test) cmd_test ;;
  web) cmd_web ;;
  aot-smoke) cmd_aot_smoke ;;
  fake-e2e) shift; cmd_fake_e2e "$@" ;;
  e2e) shift; cmd_e2e "$@" ;;
  *) die "unknown verb '$1' (all | test | web | aot-smoke | fake-e2e <glob> | e2e <glob>)" ;;
esac
