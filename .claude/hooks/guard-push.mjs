// PreToolUse tripwire for the Bash tool: denies commands that would push without the pre-push hook (which requires
// ./build.sh verify) or forge its record. It is not a security boundary; it catches the shortcuts an agent might take.
import { readFileSync } from 'node:fs';

const REASON = 'Pushes must pass the pre-push hook. Run ./build.sh verify on a clean tree (as its own background step), '
  + 'then push without --no-verify, a hooksPath override or a hand-made stamp.';

// Drops heredoc bodies (commit messages, scripts fed to an interpreter), then splits on ; && || | and newlines, then
// on whitespace, dropping quotes. Naive about quoting, which only makes it look at more segments, not fewer.
function segments(command) {
  return command.replace(/<<-?\s*(['"]?)(\w+)\1([^\n]*)\n[\s\S]*?\n\s*\2(?=\n|$)/g, '<<$2$3')
    .split(/;|&&|\|\||\||\n/).map((s) => s.trim().split(/\s+/).map((t) => t.replace(/^["']|["']$/g, '')))
    .filter((words) => words.length > 0 && words[0] !== '');
}

function denied(words) {
  const text = words.join(' ');
  let i = 0;
  while (i < words.length && /^[A-Za-z_][A-Za-z0-9_]*=/.test(words[i])) i++;
  const env = words.slice(0, i).join(' ');
  if (/GIT_CONFIG/i.test(env) && /hookspath/i.test(text)) return true;
  const program = words[i]?.split('/').pop();

  if (/rs-verified|\.git\/hooks/.test(text) && !['ls', 'cat', 'stat', 'test', 'build.sh'].includes(program)) return true;
  if (program !== 'git') return false;

  const configs = [];
  let sub = null;
  for (i++; i < words.length && sub === null; i++) {
    const w = words[i];
    if (w === '-c' || w === '-C' || w === '--git-dir' || w === '--work-tree' || w === '--namespace') {
      if (w === '-c') configs.push(words[i + 1] ?? '');
      i++;
    } else if (w.startsWith('-c')) configs.push(w.slice(2));
    else if (w.startsWith('--config-env')) return true;
    else if (!w.startsWith('-')) sub = w;
  }
  if (configs.some((c) => /hookspath/i.test(c))) return true;
  const rest = words.slice(i);
  // Git accepts any unambiguous prefix of a long option, so --no-veri skips hooks too.
  if (sub === 'push') return rest.some((w) => /^--no-v/.test(w));
  if (sub === 'config') return rest.some((w) => /hookspath/i.test(w)) && !rest.some((w) => /^(--get|--list|-l$)/.test(w));
  return false;
}

const input = JSON.parse(readFileSync(0, 'utf8'));
if (segments(input.tool_input?.command ?? '').some(denied)) {
  console.log(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: REASON } }));
}
