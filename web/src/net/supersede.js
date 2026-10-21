// Supersede keys for the outbox (docs/decisions/U28.md): commands that overwrite one target share a key, and a
// retried entry is dropped once a later entry with its key exists, so a retry never reverses a newer intent.
// Every kind in COMMANDS has a row; tests/web-unit/net/supersede.test.js keeps the table complete.

const on = (...fields) => Object.freeze({ group: null, fields });
const as = (group, ...fields) => Object.freeze({ group, fields });

/**
 * Kind → `{group, fields}` (the key is `group ?? kind` plus those payload members) or null. Null covers creates
 * (client-generated ids: always retried, the server acks a repeat), one-shot actions and deletes, and overwrites
 * whose precondition names the value they replace (`from`) or that patch only some fields: there the newer
 * command may depend on the older one having applied, so dropping the older one would lose both.
 */
export const SUPERSEDE = Object.freeze({
  'poker.vote.set': on('roundId'),
  'poker.lock.set': on('roundId'),
  // Abstaining and voting write the same seat, so either supersedes the other.
  'poker.abstain.set': as('poker.vote.set', 'roundId'),
  'poker.round.reveal': null,
  'poker.round.replay': null,
  'poker.round.create': null,
  'poker.round.createMany': null,
  'poker.round.next': null,
  // Ending and reactivating set the same flag.
  'poker.round.end': as('poker.round.activate', 'roundId'),
  'poker.round.activate': on('roundId'),
  // The screens take baseVersion and expected from the view, which already folds pending entries in: a newer edit
  // can name a version only the older one produces, so dropping the older one would lose both.
  'poker.round.setTicket': null,
  'poker.round.commitPending': null,
  'poker.round.discardPending': null,
  'poker.round.delete': null,
  'poker.round.reorder': on(),
  'poker.rounds.clearAll': null,
  'poker.settings.update': null,
  'poker.observer.set': on('principalId'),
  'poker.spotlight.take': null,
  'poker.spotlight.move': on(),
  'poker.spotlight.give': null,
  'poker.spotlight.release': null,
  'poker.jira.submitPoints': on('roundId'),
  'retro.card.add': null,
  'retro.card.edit': on('cardId'),
  'retro.card.delete': null,
  'retro.vote.set': on('cardId'),
  'retro.comment.add': null,
  'retro.comment.delete': null,
  'retro.column.add': null,
  'retro.column.update': null,
  'retro.column.delete': null,
  'retro.column.reorder': on(),
  'retro.column.setActionItems': on(),
  'retro.board.rename': null,
  'retro.config.update': null,
  'retro.stage.set': null,
  'retro.ready.set': on('stage'),
  'retro.focus.set': null,
  'retro.timer.start': null,
  'retro.timer.pause': null,
  'retro.timer.resume': null,
  'retro.timer.reset': null,
  'retro.timer.setFlags': null,
  'retro.endorse.give': null,
  'retro.endorse.revoke': null,
  'team.action.assign': on('itemId'),
  'team.action.done': on('itemId'),
  'team.action.delete': null,
});

/** `poker.vote.set:<roundId>`, `retro.column.reorder`, … or null when the kind has no supersede key. */
export function supersedeKey(k, d) {
  const row = SUPERSEDE[k];
  if (!row) {
    return null;
  }
  return [row.group ?? k, ...row.fields.map((field) => d?.[field])].join(':');
}
