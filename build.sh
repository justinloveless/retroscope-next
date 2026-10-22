#!/usr/bin/env bash
# Retroscope build entry point (DESIGN §k.1). Verbs:
#   ./build.sh                     restore, build, test, web
#   ./build.sh test                every suite except e2e and soak
#   ./build.sh quick [paths]       the development loop: only the tests that cover the changed files (tools/quick.mjs);
#                                  --plan prints them without running
#   ./build.sh verify              the full pass CI runs (test, web, generated files committed, aot-smoke) on a clean
#                                  tree; records the tree so the pre-push hook lets it through
#   ./build.sh hooks               installs the pre-push hook that requires verify (every verb installs it once the
#                                  hook is committed)
#   ./build.sh web                 esbuild bundle into web/dist (emits manifest.json)
#   ./build.sh aot-smoke           AOT publish with zero warnings, probe against a throwaway Postgres, the
#                                  native server signs a user up and holds a /ws session (hello/welcome,
#                                  protocol PING/PONG), chiseled image serves /healthz (needs Docker)
#   ./build.sh fake-e2e <glob>     Playwright specs under tests/e2e/fake/ against tests/web-fake-server
#   ./build.sh e2e <glob>          Playwright specs under tests/e2e/ against deploy/compose.test.yml
#   ./build.sh preview [fake|real] fake (default): the web build on the demo fake server with simulated teammates,
#                                  http://localhost:5173. real: the server (dotnet run) with Postgres 18 and Mailpit
#                                  from deploy/compose.preview.yml on http://localhost:8080 (needs Docker)
#   ./build.sh screenshots         smoke-checks the demo, then captures every route and key state, light and dark,
#                                  desktop and iPhone, into artifacts/screenshots (index.html is the contact sheet)
#
# Load: everything runs at low priority (RS_NICE, default 10) and test parallelism is capped at RS_JOBS
# (default a third of the cores, at least 2). RS_JOBS=0 lifts the cap for CI.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$ROOT/artifacts"
POSTGRES_IMAGE="postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873"
cd "$ROOT"

renice -n "${RS_NICE:-10}" -p $$ >/dev/null 2>&1 || true
CORES="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
RS_JOBS="${RS_JOBS:-$(( CORES / 3 > 2 ? CORES / 3 : 2 ))}"
if [[ "$RS_JOBS" == 0 ]]; then
  BUILD_LIMIT=() TEST_LIMIT=() PW_LIMIT=()
else
  BUILD_LIMIT=("-maxcpucount:$RS_JOBS")
  TEST_LIMIT=(--max-parallel-test-modules "$RS_JOBS" --max-threads "$RS_JOBS")
  PW_LIMIT=("--workers=$RS_JOBS")
fi

if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.rd/docker.sock" ]]; then
  export DOCKER_HOST="unix://$HOME/.rd/docker.sock"
fi
if [[ "${DOCKER_HOST:-}" == "unix://$HOME/.rd/docker.sock" ]]; then
  # Rancher Desktop: Ryuk must bind-mount the socket path as seen inside the VM.
  export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-/var/run/docker.sock}"
fi

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'build.sh: %s\n' "$*" >&2; exit 1; }

node_deps() {
  if [[ ! -f node_modules/.package-lock.json ]] || [[ package-lock.json -nt node_modules/.package-lock.json ]]; then
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

port_in_use() {
  node -e 'const s=require("net").connect(Number(process.argv[1]),"127.0.0.1");s.on("connect",()=>process.exit(0));s.on("error",()=>process.exit(1))' "$1"
}

wait_http() {
  local url="$1"
  for _ in $(seq 1 60); do
    curl -fsS "$url" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  return 1
}

retry() {
  local attempts="$1"
  shift
  for _ in $(seq 1 "$attempts"); do
    "$@" && return 0
    sleep 1
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
  dotnet build Retroscope.slnx ${BUILD_LIMIT[@]+"${BUILD_LIMIT[@]}"}

  log "dotnet test (unit, integration, importer)"
  dotnet test --solution Retroscope.slnx --no-build ${TEST_LIMIT[@]+"${TEST_LIMIT[@]}"}

  log "simulation (PR seeds)"
  dotnet run --no-build --project tests/Retroscope.Sim -- --seeds 2000 --strict

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

cmd_quick() {
  node_deps
  RS_JOBS="$RS_JOBS" node tools/quick.mjs "$@"
}

stamp_dir() {
  echo "$(cd "$(git rev-parse --git-common-dir)" && pwd)/rs-verified"
}

# The stamp names the tree, not the commit, so a rebase or amend that keeps the content does not retest.
cmd_verify() {
  [[ -z "$(git status --porcelain)" ]] || die "verify needs a clean working tree, so that it tests exactly what gets pushed"
  local tree
  tree="$(git rev-parse 'HEAD^{tree}')"
  cmd_test
  cmd_web
  log "generated files are committed"
  git diff --exit-code
  [[ -z "$(git status --porcelain)" ]] || { git status --short; die "the pass left untracked files behind"; }
  cmd_aot_smoke
  mkdir -p "$(stamp_dir)"
  touch "$(stamp_dir)/$tree"
  log "verify passed for tree ${tree:0:12}"
}

# Hooks live in the common .git, so one install covers every worktree. The shim prefers the pushing worktree's copy
# and falls back to the main checkout's, so a branch cut before the hook existed is still gated.
cmd_hooks() {
  local hooks shim tmp
  hooks="$(cd "$(git rev-parse --git-common-dir)" && pwd)/hooks"
  shim="$hooks/pre-push"
  if [[ -n "$(git config --get core.hooksPath || true)" ]]; then
    printf 'build.sh: core.hooksPath is set, so the pre-push hook in %s is not used\n' "$hooks" >&2
    return 0
  fi
  mkdir -p "$hooks"
  tmp="$(mktemp "$hooks/pre-push.XXXXXX")"
  cat >"$tmp" <<'SHIM'
#!/usr/bin/env bash
# Installed by ./build.sh hooks.
hook="$(git rev-parse --show-toplevel)/tools/git-hooks/pre-push"
[[ -x "$hook" ]] || hook="$(git rev-parse --git-common-dir)/../tools/git-hooks/pre-push"
[[ -x "$hook" ]] || { echo "pre-push: no tools/git-hooks/pre-push here; run ./build.sh verify by hand" >&2; exit 1; }
exec "$hook" "$@"
SHIM
  chmod +x "$tmp"
  if cmp -s "$tmp" "$shim"; then
    rm -f "$tmp"
  else
    mv "$tmp" "$shim"
    log "installed the pre-push hook ($shim)"
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

# Against the native server at $1: signs a user up and in through the real routes, then speaks the wire protocol over
# a raw socket, so the WebSocket control frames are visible: welcome after hello, a PONG for the client's PING, a
# server PING (keep-alive, every 15 s) answered with a PONG, and an app-level ping/pong on the same live socket.
ws_smoke() {
  SMOKE_URL="$1" node - <<'JS'
const crypto = require("node:crypto");
const http = require("node:http");
const net = require("node:net");

const base = new URL(process.env.SMOKE_URL);
const origin = base.origin;
const fail = (message) => { console.error(`ws smoke: ${message}`); process.exit(1); };

function post(path, body) {
  const payload = Buffer.from(JSON.stringify(body));
  return new Promise((resolve, reject) => {
    const request = http.request(new URL(path, base), {
      method: "POST",
      headers: { "Content-Type": "application/json", "Content-Length": payload.length, "X-RS": "1", Origin: origin },
    }, (response) => {
      response.resume();
      response.on("end", () => resolve(response));
    });
    request.on("error", reject);
    request.end(payload);
  });
}

function frame(opcode, payload) {
  const mask = crypto.randomBytes(4);
  const header = payload.length < 126 ? Buffer.from([0x80 | opcode, 0x80 | payload.length])
    : Buffer.from([0x80 | opcode, 0x80 | 126, payload.length >> 8, payload.length & 0xff]);
  const masked = Buffer.from(payload.map((byte, i) => byte ^ mask[i % 4]));
  return Buffer.concat([header, mask, masked]);
}

class Socket {
  constructor(socket, rest) {
    this.socket = socket;
    this.buffer = rest;
    this.waiters = [];
    socket.on("data", (data) => { this.buffer = Buffer.concat([this.buffer, data]); this.pump(); });
    socket.on("close", () => this.waiters.splice(0).forEach((w) => w.reject(new Error("socket closed"))));
  }

  pump() {
    while (this.waiters.length > 0 && this.buffer.length >= 2) {
      let length = this.buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) { length = this.buffer.readUInt16BE(2); offset = 4; }
      if (length === 127) { length = Number(this.buffer.readBigUInt64BE(2)); offset = 10; }
      if (this.buffer.length < offset + length) return;
      const opcode = this.buffer[0] & 0x0f;
      const payload = this.buffer.subarray(offset, offset + length);
      this.buffer = this.buffer.subarray(offset + length);
      this.waiters.shift().resolve({ opcode, payload });
    }
  }

  next(ms) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`no frame within ${ms} ms`)), ms);
      this.waiters.push({ resolve: (f) => { clearTimeout(timer); resolve(f); }, reject });
      this.pump();
    });
  }

  send(opcode, payload) { this.socket.write(frame(opcode, Buffer.from(payload))); }
}

function upgrade(cookie) {
  const key = crypto.randomBytes(16).toString("base64");
  const expected = crypto.createHash("sha1").update(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  return new Promise((resolve, reject) => {
    const socket = net.connect(Number(base.port), base.hostname, () => {
      socket.write([
        "GET /ws HTTP/1.1", `Host: ${base.host}`, "Upgrade: websocket", "Connection: Upgrade",
        `Sec-WebSocket-Key: ${key}`, "Sec-WebSocket-Version: 13", `Origin: ${origin}`, `Cookie: ${cookie}`, "", "",
      ].join("\r\n"));
    });
    let head = Buffer.alloc(0);
    const onData = (data) => {
      head = Buffer.concat([head, data]);
      const end = head.indexOf("\r\n\r\n");
      if (end < 0) return;
      socket.off("data", onData);
      const lines = head.subarray(0, end).toString().split("\r\n");
      if (!lines[0].startsWith("HTTP/1.1 101")) return reject(new Error(`upgrade refused: ${lines[0]}`));
      const accept = lines.find((l) => l.toLowerCase().startsWith("sec-websocket-accept:"));
      if (accept?.split(":")[1].trim() !== expected) return reject(new Error("bad Sec-WebSocket-Accept"));
      resolve(new Socket(socket, head.subarray(end + 4)));
    };
    socket.on("data", onData);
    socket.on("error", reject);
  });
}

(async () => {
  const email = `smoke-${crypto.randomUUID()}@example.com`;
  const password = "correct horse battery staple";
  const signup = await post("/api/auth/signup", { email, password, fullName: "Smoke Test" });
  if (signup.statusCode !== 202) fail(`signup answered ${signup.statusCode}`);
  const login = await post("/api/auth/login", { email, password });
  if (login.statusCode !== 200) fail(`login answered ${login.statusCode}`);
  const session = (login.headers["set-cookie"] ?? []).map((c) => c.split(";")[0]).find((c) => c.startsWith("__Host-rs_sid="));
  if (!session) fail("login set no session cookie");
  console.log("signed up and logged in through the real routes");

  const ws = await upgrade(session);
  ws.send(0x1, JSON.stringify({ t: "hello", v: 1, cid: crypto.randomUUID(), subs: [] }));
  const welcome = await ws.next(5000);
  const hello = welcome.opcode === 0x1 ? JSON.parse(welcome.payload) : null;
  if (hello?.t !== "welcome" || hello.me.kind !== "user" || hello.me.name !== "Smoke Test") fail(`expected welcome, got ${welcome.payload}`);
  console.log("hello -> welcome as the signed-up user");

  ws.send(0x9, "rs-smoke");
  const pong = await ws.next(5000);
  if (pong.opcode !== 0xa || pong.payload.toString() !== "rs-smoke") fail(`expected PONG rs-smoke, got opcode ${pong.opcode}`);
  console.log("client PING -> server PONG");

  const ping = await ws.next(25000);
  if (ping.opcode !== 0x9) fail(`expected a server PING, got opcode ${ping.opcode}`);
  ws.send(0xa, ping.payload);
  console.log("server keep-alive PING -> client PONG");

  ws.send(0x1, JSON.stringify({ t: "ping", c: 42 }));
  const clock = await ws.next(5000);
  const reply = clock.opcode === 0x1 ? JSON.parse(clock.payload) : null;
  if (reply?.t !== "pong" || reply.c !== 42) fail(`expected pong 42, got ${clock.payload}`);
  console.log("app ping -> pong on the same live socket");

  ws.send(0x8, Buffer.from([0x03, 0xe8]));
  const close = await ws.next(5000);
  if (close.opcode !== 0x8) fail(`expected close, got opcode ${close.opcode}`);
  ws.socket.destroy();
})().catch((e) => fail(e.message));
JS
}

smoke_cleanup() {
  [[ -n "${SMOKE_PID:-}" ]] && kill "$SMOKE_PID" 2>/dev/null
  docker rm -f "$SMOKE_PG" "$SMOKE_APP" >/dev/null 2>&1 || true
  docker rmi "$SMOKE_IMAGE" >/dev/null 2>&1 || true
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
  # Per run, so an aot-smoke in another worktree cannot retag the image this run probes.
  SMOKE_IMAGE="retroscope:aot-smoke-$$"
  SMOKE_PID=""
  trap smoke_cleanup EXIT

  image_port="$(free_port)"
  log "throwaway Postgres on port $pg_port"
  # The chiseled app container joins this container's network namespace, so its port is published here.
  docker run -d --rm --name "$SMOKE_PG" -p "127.0.0.1:$pg_port:5432" -p "127.0.0.1:$image_port:8080" \
    -e POSTGRES_DB=retroscope -e POSTGRES_USER=retroscope_owner -e POSTGRES_PASSWORD=smoke \
    "$POSTGRES_IMAGE" >/dev/null
  for _ in $(seq 1 60); do
    docker exec "$SMOKE_PG" pg_isready -h 127.0.0.1 -U retroscope_owner -d retroscope >/dev/null 2>&1 && break
    sleep 0.5
  done
  local db_url="Host=127.0.0.1;Port=$pg_port;Database=retroscope;Username=retroscope_owner;Password=smoke"
  local app_db_url="Host=127.0.0.1;Port=$pg_port;Database=retroscope;Username=retroscope_app;Password=smoke-app"

  log "smoke probe"
  SMOKE_DATABASE_URL="$db_url" "$ARTIFACTS/aot/smoke/retroscope-aot-smoke"

  log "server binary serves /healthz"
  app_port="$(free_port)"
  local app_keys
  app_keys="k1:$(node -e 'process.stdout.write(require("crypto").randomBytes(32).toString("base64"))')"
  DOTNET_hostBuilder__reloadConfigOnChange=false ASPNETCORE_URLS="http://127.0.0.1:$app_port" DATABASE_URL="$app_db_url" DATABASE_OWNER_URL="$db_url" \
    PUBLIC_URL="http://127.0.0.1:$app_port" APP_KEYS="$app_keys" "$ARTIFACTS/aot/server/retroscope" >"$ARTIFACTS/aot/server-run.log" 2>&1 &
  SMOKE_PID=$!
  wait_http "http://127.0.0.1:$app_port/healthz" || { cat "$ARTIFACTS/aot/server-run.log"; die "/healthz did not answer"; }
  HEALTHCHECK_PORT="$app_port" retry 5 "$ARTIFACTS/aot/server/retroscope" healthcheck || die "healthcheck subcommand failed"

  log "server binary: signup, login and a /ws session"
  ws_smoke "http://127.0.0.1:$app_port" || { cat "$ARTIFACTS/aot/server-run.log"; die "the /ws smoke failed"; }
  kill "$SMOKE_PID"
  SMOKE_PID=""

  log "chiseled image serves /healthz"
  docker build -f deploy/Dockerfile -t "$SMOKE_IMAGE" . >"$ARTIFACTS/aot/image.log" 2>&1 \
    || { tail -50 "$ARTIFACTS/aot/image.log"; die "image build failed"; }
  docker run -d --name "$SMOKE_APP" --network "container:$SMOKE_PG" \
    -e DATABASE_URL="Host=127.0.0.1;Port=5432;Database=retroscope;Username=retroscope_app;Password=smoke-app" \
    -e DATABASE_OWNER_URL="Host=127.0.0.1;Port=5432;Database=retroscope;Username=retroscope_owner;Password=smoke" \
    -e PUBLIC_URL="http://127.0.0.1:$image_port" \
    "$SMOKE_IMAGE" >/dev/null
  wait_http "http://127.0.0.1:$image_port/healthz" || { docker logs "$SMOKE_APP"; die "chiseled image did not serve /healthz"; }
  retry 5 docker exec "$SMOKE_APP" /app/retroscope healthcheck || die "healthcheck subcommand failed inside the image"

  log "aot-smoke passed"
}

cmd_fake_e2e() {
  local glob="${1:?usage: ./build.sh fake-e2e <spec-glob under tests/e2e/fake/>}"
  cmd_web
  log "Playwright (fake server): $glob"
  RS_E2E_TARGET=fake npx playwright test ${PW_LIMIT[@]+"${PW_LIMIT[@]}"} --config tests/e2e/playwright.config.js \
    "tests/e2e/fake/$(glob_to_regex "$glob")$"
}

cmd_e2e() {
  local glob="${1:?usage: ./build.sh e2e <spec-glob under tests/e2e/>}"
  need_docker
  node_deps
  log "compose.test.yml up"
  docker compose -f deploy/compose.test.yml up -d --wait --build
  trap 'docker compose -f deploy/compose.test.yml down -v >/dev/null 2>&1 || true' EXIT
  log "Playwright (compose stack): $glob"
  export RS_E2E_BASE_URL="https://localhost:${RS_TEST_HTTPS_PORT:-18443}"
  RS_E2E_TARGET=compose npx playwright test ${PW_LIMIT[@]+"${PW_LIMIT[@]}"} --config tests/e2e/playwright.config.js \
    "tests/e2e/$(glob_to_regex "$glob")$"
}

cmd_preview() {
  case "${1:-fake}" in
    fake) preview_fake ;;
    real) preview_real ;;
    *) die "unknown preview mode '$1' (fake | real)" ;;
  esac
}

preview_fake() {
  if port_in_use "${PREVIEW_PORT:-5173}"; then
    die "port ${PREVIEW_PORT:-5173} is in use; pick another with PREVIEW_PORT=<port>"
  fi
  cmd_web
  log "demo preview"
  exec node tests/web-fake-server/demo/main.js --port "${PREVIEW_PORT:-5173}"
}

PREVIEW_COMPOSE=(docker compose -f deploy/compose.preview.yml)

preview_cleanup() {
  if [[ -n "${PREVIEW_PID:-}" ]]; then
    kill "$PREVIEW_PID" 2>/dev/null || true
    wait "$PREVIEW_PID" 2>/dev/null || true
  fi
  log "removing the preview containers and volume"
  "${PREVIEW_COMPOSE[@]}" down -v >/dev/null 2>&1 || true
}

# Signs demo@example.test up through the real route, reads the verification mail from Mailpit's API and
# consumes its token with a session of that account, so the account starts verified.
preview_seed() {
  SEED_URL="$1" MAILPIT_URL="$2" SEED_PASSWORD="$3" node - <<'JS'
const base = process.env.SEED_URL;
const mailpit = process.env.MAILPIT_URL;
const email = 'demo@example.test';
const password = process.env.SEED_PASSWORD;
const fail = (message) => { console.error(`seed: ${message}`); process.exit(1); };
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const post = (path, body, cookie) => fetch(new URL(path, base), {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-RS': '1', Origin: new URL(base).origin, ...(cookie ? { Cookie: cookie } : {}) },
  body: JSON.stringify(body),
});

(async () => {
  const signup = await post('/api/auth/signup', { email, password, fullName: 'Demo User' });
  if (signup.status !== 202) fail(`signup answered ${signup.status}`);
  let token = null;
  for (let i = 0; i < 60 && !token; i++) {
    const list = await (await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json();
    for (const summary of list.messages ?? []) {
      const message = await (await fetch(`${mailpit}/api/v1/message/${summary.ID}`)).json();
      token = /\/auth\/verify#t=([A-Za-z0-9_-]+)/.exec(`${message.Text ?? ''} ${message.HTML ?? ''}`)?.[1] ?? token;
    }
    if (!token) await sleep(500);
  }
  if (!token) fail('the verification mail did not reach Mailpit');
  console.log('verification mail landed in Mailpit');
  const login = await post('/api/auth/login', { email, password });
  if (login.status !== 200) fail(`login answered ${login.status}`);
  const session = login.headers.getSetCookie().map((c) => c.split(';')[0]).find((c) => c.startsWith('__Host-rs_sid='));
  if (!session) fail('login set no session cookie');
  const verify = await post('/api/auth/verify/consume', { token }, session);
  if (!verify.ok) fail(`verify answered ${verify.status}`);
  console.log(`signed up, logged in and verified ${email}`);
})().catch((error) => fail(error.message));
JS
}

random_key() {
  node -e 'process.stdout.write(require("crypto").randomBytes(32).toString("base64"))'
}

preview_real() {
  need_docker
  local port="${PREVIEW_PORT:-8080}"
  local url="http://localhost:$port" mailpit="http://localhost:${PREVIEW_MAILPIT_PORT:-8025}"
  local password log_file="$ARTIFACTS/preview/server.log"
  password="preview-$(node -e 'process.stdout.write(require("crypto").randomBytes(6).toString("hex"))')"
  export PREVIEW_PG_PORT="${PREVIEW_PG_PORT:-55432}" PREVIEW_SMTP_PORT="${PREVIEW_SMTP_PORT:-1025}" PREVIEW_MAILPIT_PORT="${PREVIEW_MAILPIT_PORT:-8025}"
  local name
  for name in PREVIEW_PORT:$port PREVIEW_PG_PORT:$PREVIEW_PG_PORT PREVIEW_SMTP_PORT:$PREVIEW_SMTP_PORT PREVIEW_MAILPIT_PORT:$PREVIEW_MAILPIT_PORT; do
    if port_in_use "${name#*:}"; then
      die "port ${name#*:} is in use; pick another with ${name%%:*}=<port>"
    fi
  done
  cmd_web

  PREVIEW_PID=""
  trap preview_cleanup EXIT
  trap 'exit 130' INT TERM
  log "compose.preview.yml up (Postgres 18, Mailpit)"
  "${PREVIEW_COMPOSE[@]}" up -d --wait

  log "dotnet run (the real server, JIT)"
  mkdir -p "$ARTIFACTS/preview"
  PUBLIC_URL="$url" ASPNETCORE_URLS="http://127.0.0.1:$port" WEB_ROOT="$ROOT/web/dist" \
    DATABASE_URL="Host=127.0.0.1;Port=$PREVIEW_PG_PORT;Database=retroscope;Username=retroscope_app;Password=preview-app" \
    DATABASE_OWNER_URL="Host=127.0.0.1;Port=$PREVIEW_PG_PORT;Database=retroscope;Username=retroscope_owner;Password=preview-owner" \
    APP_KEYS="k1:$(random_key)" ANON_TAG_KEY="$(random_key)" \
    SMTP_HOST=127.0.0.1 SMTP_PORT="$PREVIEW_SMTP_PORT" SMTP_SECURITY=none SMTP_FROM="Retroscope <noreply@example.test>" \
    dotnet run --project src/Retroscope.Server >"$log_file" 2>&1 &
  PREVIEW_PID=$!
  for _ in $(seq 1 240); do
    curl -fsS "$url/healthz" >/dev/null 2>&1 && break
    kill -0 "$PREVIEW_PID" 2>/dev/null || { tail -40 "$log_file"; die "the server exited"; }
    sleep 0.5
  done
  curl -fsS "$url/healthz" >/dev/null 2>&1 || { tail -40 "$log_file"; die "/healthz did not answer"; }

  log "seeding demo@example.test"
  preview_seed "$url" "$mailpit" "$password" || { tail -40 "$log_file"; die "seeding failed"; }

  cat <<TEXT

Retroscope (real server) is running on $url
  Sign in:  demo@example.test / $password
  Mailpit:  $mailpit (sign-in links, password resets and verification mails land here)
  Log:      $log_file

Works at this stage: sign-up, sign-in and sign-out, magic links, password reset, email verification
(all through Mailpit), account settings and sessions, and the shell pages.
Arrives in wave 5:
  - realtime rooms: an UnavailableRoomGateway stub is still registered, so the WebSocket does not reach
    poker or retro rooms yet;
  - teams, the dashboard and team lists, and creating poker sessions and boards over HTTP (U24, U25);
  - the Jira proxy (U26).
For the whole UI with data, use ./build.sh preview (fake mode).
Ctrl-C stops the server and removes the containers and the volume.
TEXT
  wait "$PREVIEW_PID"
}

cmd_screenshots() {
  cmd_web
  local mode port url log_file="$ARTIFACTS/screenshots-server.log"
  rm -rf "$ARTIFACTS/screenshots"
  mkdir -p "$ARTIFACTS/screenshots"
  SHOTS_PID=""
  trap 'if [[ -n "$SHOTS_PID" ]]; then kill "$SHOTS_PID" 2>/dev/null || true; fi' EXIT

  for mode in smoke capture; do
    port="$(free_port)"
    url="http://localhost:$port"
    # The smoke wants the simulated teammates; the capture wants the seeded state to hold still.
    local flags=()
    [[ "$mode" == capture ]] && flags=(--quiet-peers)
    node tests/web-fake-server/demo/main.js --port "$port" ${flags[@]+"${flags[@]}"} >"$log_file" 2>&1 &
    SHOTS_PID=$!
    wait_http "$url/__demo/state" || { cat "$log_file"; die "the demo server did not start"; }
    log "Playwright: $mode ($url)"
    RS_PREVIEW_URL="$url" npx playwright test ${PW_LIMIT[@]+"${PW_LIMIT[@]}"} --config tests/e2e/screenshots/playwright.config.js "$mode.spec.js"
    kill "$SHOTS_PID"
    wait "$SHOTS_PID" 2>/dev/null || true
    SHOTS_PID=""
  done
  node tests/e2e/screenshots/contact-sheet.mjs "$ARTIFACTS/screenshots"
}

# Only a committed hook is installed, so a work-in-progress copy never gates the other worktrees.
if [[ -z "${CI:-}" ]] && git cat-file -e HEAD:tools/git-hooks/pre-push 2>/dev/null; then
  cmd_hooks
fi

case "${1:-all}" in
  all) cmd_test; cmd_web ;;
  test) cmd_test ;;
  quick) shift; cmd_quick "$@" ;;
  verify) cmd_verify ;;
  hooks) ;;
  web) cmd_web ;;
  aot-smoke) cmd_aot_smoke ;;
  fake-e2e) shift; cmd_fake_e2e "$@" ;;
  e2e) shift; cmd_e2e "$@" ;;
  preview) shift; cmd_preview "$@" ;;
  screenshots) cmd_screenshots ;;
  *) die "unknown verb '$1' (all | test | quick [paths] | verify | hooks | web | aot-smoke | fake-e2e <glob> | e2e <glob> | preview [fake|real] | screenshots)" ;;
esac
