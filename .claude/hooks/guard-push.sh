#!/usr/bin/env bash
# Runs guard-push.mjs with whichever node is installed; hooks may start without the login shell's PATH (nvm).
node="$(command -v node || ls -d "$HOME"/.nvm/versions/node/*/bin/node 2>/dev/null | tail -1)"
if [[ -n "$node" ]]; then
  exec "$node" "$(dirname "$0")/guard-push.mjs"
fi
# Without node, a crude check on the raw payload: deny anything that looks like a hook bypass.
if grep -Eiq 'push.*--no-v|hookspath|rs-verified'; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Pushes must pass the pre-push hook (./build.sh verify); node was not found to check this command precisely."}}'
fi
