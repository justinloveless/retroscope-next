#!/usr/bin/env bash
# Points a Coolify compose resource at a pair of image digests, deploys it and waits until it is healthy.
# Used by .github/workflows/deploy.yml (production) and preview.yml (the preview slot); see COOLIFY_SETUP.md §11.
#
#   coolify-deploy.sh check    validate the environment only; makes no network calls
#   coolify-deploy.sh deploy   set git_branch, git_commit_sha, APP_DIGEST and IMPORTER_DIGEST, deploy, wait,
#                              health-check
#
# Environment (all required unless a default is shown):
#   COOLIFY_URL       https://<coolify host>, without /api/v1
#   COOLIFY_TOKEN     API token with write and deploy permissions; never printed
#   COOLIFY_APP_UUID  the application (compose resource) to deploy
#   GIT_BRANCH        branch Coolify clones
#   GIT_COMMIT        full 40-character commit SHA on GIT_BRANCH; Coolify reads deploy/docker-compose.yml from it, so
#                     the compose file always matches the images (a rollback gets the old compose file too)
#   APP_DIGEST        64 hex characters, without "sha256:"
#   IMPORTER_DIGEST   64 hex characters, without "sha256:"
#   HEALTH_URL        public https URL that must answer 200 after the deploy (…/healthz)
#   DEPLOY_TIMEOUT    seconds to wait for the Coolify deployment to finish (default 900)
#   HEALTH_TIMEOUT    seconds to wait for HEALTH_URL and a running:healthy status (default 300)
#   POLL_INTERVAL     seconds between polls (default 10)
# Writes deployment_uuid to $GITHUB_OUTPUT when that is set. If the run is cancelled (SIGINT/SIGTERM) while its
# deployment is still queued in Coolify, the script tries to cancel that deployment so it cannot block the next run.
set -euo pipefail

die() {
  # In GitHub Actions the ::error:: prefix turns the message into an annotation on the run.
  if [[ "${GITHUB_ACTIONS:-}" == true ]]; then echo "::error::$*" >&2; else echo "error: $*" >&2; fi
  exit 1
}
log() { echo "[coolify-deploy] $*" >&2; }

usage() { echo "usage: $0 check|deploy" >&2; exit 2; }

validate() {
  COOLIFY_URL="${COOLIFY_URL:-}" COOLIFY_TOKEN="${COOLIFY_TOKEN:-}" COOLIFY_APP_UUID="${COOLIFY_APP_UUID:-}"
  GIT_BRANCH="${GIT_BRANCH:-}" GIT_COMMIT="${GIT_COMMIT:-}" APP_DIGEST="${APP_DIGEST:-}" IMPORTER_DIGEST="${IMPORTER_DIGEST:-}" HEALTH_URL="${HEALTH_URL:-}"
  local missing=() v
  for v in COOLIFY_URL COOLIFY_TOKEN COOLIFY_APP_UUID GIT_BRANCH GIT_COMMIT APP_DIGEST IMPORTER_DIGEST HEALTH_URL; do
    [[ -n "${!v:-}" ]] || missing+=("$v")
  done
  ((${#missing[@]} == 0)) || die "missing environment: ${missing[*]}"

  [[ "$COOLIFY_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?/?$ ]] || die "COOLIFY_URL must be https://<host> without a path"
  COOLIFY_URL="${COOLIFY_URL%/}"
  [[ "$HEALTH_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?/[A-Za-z0-9._/-]*$ ]] || die "HEALTH_URL must be an https URL with a path"
  [[ "$COOLIFY_APP_UUID" =~ ^[A-Za-z0-9]{8,64}$ ]] || die "COOLIFY_APP_UUID is not a Coolify uuid"
  [[ "$GIT_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "GIT_COMMIT must be a full 40-character lower-case commit SHA"
  [[ "$APP_DIGEST" =~ ^[0-9a-f]{64}$ ]] || die "APP_DIGEST must be 64 lower-case hex characters without sha256:"
  [[ "$IMPORTER_DIGEST" =~ ^[0-9a-f]{64}$ ]] || die "IMPORTER_DIGEST must be 64 lower-case hex characters without sha256:"
  # A conservative subset of git's branch rules; jq builds every JSON body, so this guards Coolify, not the shell.
  [[ "$GIT_BRANCH" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$ && "$GIT_BRANCH" != *..* && "$GIT_BRANCH" != *.lock \
     && "$GIT_BRANCH" != */ ]] || die "GIT_BRANCH is not an acceptable branch name"

  DEPLOY_TIMEOUT="${DEPLOY_TIMEOUT:-900}"
  HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-300}"
  POLL_INTERVAL="${POLL_INTERVAL:-10}"
  local n
  for n in DEPLOY_TIMEOUT HEALTH_TIMEOUT POLL_INTERVAL; do
    [[ "${!n}" =~ ^[1-9][0-9]{0,4}$ ]] || die "$n must be a positive number of seconds"
  done
  command -v curl >/dev/null || die "curl is required"
  command -v jq >/dev/null || die "jq is required"
}

# api METHOD PATH [JSON_BODY] -> response body on stdout. The token travels in a header file, never on a command line.
# $WORK/body never outlives a call: curl leaves an existing -o file untouched when no response arrives, so a stale body
# (for example the env listing, which can hold secret values) would otherwise be logged after a connection failure.
api() {
  local method="$1" path="$2" body="${3:-}" code
  rm -f "$WORK/body"
  local args=(--silent --show-error --fail-with-body --connect-timeout 10 --max-time 60
    -H @"$WORK/auth" -H 'Accept: application/json' -X "$method" -o "$WORK/body" -w '%{http_code}')
  # GETs are safe to retry, except /deploy, which queues a deployment.
  if [[ "$method" == GET && "$path" != /deploy* ]]; then args+=(--retry 3 --retry-delay 5 --retry-all-errors); fi
  if [[ -n "$body" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "$body"); fi
  if ! code="$(curl "${args[@]}" "$COOLIFY_URL/api/v1$path")"; then
    if [[ "$code" =~ ^[1-9][0-9]{2}$ && -f "$WORK/body" ]]; then
      # An HTTP error body from this call is a message or validation errors; cap it so nothing large reaches the log.
      log "$method $path failed (HTTP $code): $(head -c 500 "$WORK/body" || true)"
    else
      log "$method $path failed: no HTTP response"
    fi
    rm -f "$WORK/body"
    return 1
  fi
  cat "$WORK/body"
  rm -f "$WORK/body"
}

# Coolify clones git_branch and then checks out git_commit_sha (Application::setGitImportSettings) on every deploy.
ensure_source() {
  local current want body
  want="$(jq -nc --arg b "$GIT_BRANCH" --arg c "$GIT_COMMIT" '{git_branch: $b, git_commit_sha: $c}')"
  current="$(api GET "/applications/$COOLIFY_APP_UUID" | jq -c '{git_branch, git_commit_sha}')"
  if [[ "$current" == "$want" ]]; then
    log "source is already $GIT_BRANCH at $GIT_COMMIT"
  else
    log "switching source from $current to $want"
    api PATCH "/applications/$COOLIFY_APP_UUID" "$want" >/dev/null
    current="$(api GET "/applications/$COOLIFY_APP_UUID" | jq -c '{git_branch, git_commit_sha}')"
    [[ "$current" == "$want" ]] || die "the source is still $current after the update"
  fi
}

# The env listing can carry secret values (when the token may read them), so it stays in a file and only jq reads it.
fetch_envs() { api GET "/applications/$COOLIFY_APP_UUID/envs" >"$WORK/envs"; }

set_env() {
  local key="$1" value="$2" body method
  body="$(jq -nc --arg k "$key" --arg v "$value" '{key: $k, value: $v, is_preview: false, is_literal: true}')"
  if jq -e --arg k "$key" 'any(.[]; .key == $k and ((.is_preview == true or .is_preview == 1) | not))' "$WORK/envs" >/dev/null; then
    method=PATCH
  else
    method=POST
  fi
  log "$method env $key"
  api "$method" "/applications/$COOLIFY_APP_UUID/envs" "$body" >/dev/null
}

verify_env() {
  local key="$1" want="$2" got
  got="$(jq -r --arg k "$key" '[.[] | select(.key == $k and ((.is_preview == true or .is_preview == 1) | not))][0] | if . == null then "<missing>" else (.value // "<hidden>") end' "$WORK/envs")"
  case "$got" in
    "$want") log "verified $key" ;;
    "<hidden>") log "warning: the token cannot read values, so $key is checked by its write response only" ;;
    *) die "$key did not take the new value" ;;
  esac
}

# The Coolify deployment this run queued and its last polled status, for on_abort.
DEPLOYMENT="" DEPLOYMENT_STATUS=""

# A cancelled GitHub run does not cancel its Coolify deployment. One still queued would sit ahead of the next run's
# deploy, so cancel it. One already in progress is left to finish: stopping it half-way could leave the stack down.
on_abort() {
  trap - INT TERM
  if [[ -n "$DEPLOYMENT" && "$DEPLOYMENT_STATUS" == queued ]]; then
    log "run cancelled; cancelling queued deployment $DEPLOYMENT"
    # shellcheck disable=SC2310
    api POST "/deployments/$DEPLOYMENT/cancel" >/dev/null || log "could not cancel deployment $DEPLOYMENT"
  fi
  exit 130
}

wait_for_deployment() {
  local dep="$1" deadline status
  deadline=$((SECONDS + DEPLOY_TIMEOUT))
  while :; do
    # The deployment record carries build logs; read only its status. A failed poll is retried until the deadline.
    # shellcheck disable=SC2310
    status="$(api GET "/deployments/$dep" | jq -r '.status // empty')" || status="<unreachable>"
    DEPLOYMENT_STATUS="$status"
    case "$status" in
      finished) log "deployment $dep finished"; return 0 ;;
      failed | cancelled*) die "deployment $dep ended as $status; see the resource's Deployments tab in Coolify" ;;
      *) ;;
    esac
    ((SECONDS < deadline)) || die "deployment $dep still ${status:-<unknown>} after ${DEPLOY_TIMEOUT}s"
    log "deployment $dep: ${status:-<unknown>}"
    sleep "$POLL_INTERVAL"
  done
}

wait_for_health() {
  local deadline code status
  deadline=$((SECONDS + HEALTH_TIMEOUT))
  # No auth header here: HEALTH_URL is public and must never see the token.
  while :; do
    code="$(curl --silent --output /dev/null --write-out '%{http_code}' --connect-timeout 5 --max-time 10 "$HEALTH_URL" || true)"
    [[ "$code" == 200 ]] && break
    ((SECONDS < deadline)) || die "$HEALTH_URL answered ${code:-nothing} for ${HEALTH_TIMEOUT}s"
    log "$HEALTH_URL: ${code:-no answer}"
    sleep "$POLL_INTERVAL"
  done
  log "$HEALTH_URL answered 200"
  # Coolify refreshes container status on its own schedule, so allow it to catch up within the same budget.
  while :; do
    # shellcheck disable=SC2310
    status="$(api GET "/applications/$COOLIFY_APP_UUID" | jq -r '.status // empty')" || status="<unreachable>"
    [[ "$status" == running:healthy ]] && break
    ((SECONDS < deadline)) || die "application status is ${status:-<unknown>}, not running:healthy"
    log "application status: ${status:-<unknown>}"
    sleep "$POLL_INTERVAL"
  done
  log "application status: running:healthy"
  # This shows the site is up after Coolify finished the deployment. It cannot tell the old containers from the new:
  # both answer /healthz, and the status may be from before the deploy. The finished deployment is the evidence.
}

deploy() {
  WORK="$(mktemp -d)"
  trap 'rm -rf "$WORK"' EXIT
  (umask 077 && printf 'Authorization: Bearer %s\n' "$COOLIFY_TOKEN" >"$WORK/auth")

  ensure_source

  fetch_envs
  set_env APP_DIGEST "$APP_DIGEST"
  set_env IMPORTER_DIGEST "$IMPORTER_DIGEST"
  fetch_envs
  verify_env APP_DIGEST "$APP_DIGEST"
  verify_env IMPORTER_DIGEST "$IMPORTER_DIGEST"
  rm -f "$WORK/envs"

  trap on_abort INT TERM
  local response dep
  response="$(api GET "/deploy?uuid=$COOLIFY_APP_UUID&force=false")"
  dep="$(jq -r '.deployments[0].deployment_uuid // empty' <<<"$response")"
  if [[ ! "$dep" =~ ^[A-Za-z0-9]+$ ]]; then
    die "Coolify queued no deployment: $(jq -r '.deployments[0].message // .message // "no message"' <<<"$response" || true)"
  fi
  log "queued deployment $dep"
  DEPLOYMENT="$dep" DEPLOYMENT_STATUS=queued
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then echo "deployment_uuid=$dep" >>"$GITHUB_OUTPUT"; fi

  wait_for_deployment "$dep"
  wait_for_health
}

(($# == 1)) || usage
case "$1" in
  check) validate; log "environment is valid; no request was made" ;;
  deploy) validate; deploy ;;
  *) usage ;;
esac
