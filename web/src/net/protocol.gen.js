// GENERATED from src/Retroscope.Core/Protocol/Catalog.cs by ProtocolCatalogTests.GenerateJs. Do not edit.
// Regenerate: dotnet test tests/Retroscope.Core.Tests --filter Category=Protocol

export const PROTOCOL_VERSION = 1;
export const MAX_FRAME_BYTES = 65536;

/** Command kind → target room, payload members, required members, and whether it is ephemeral. */
export const COMMANDS = Object.freeze({
  'poker.vote.set': Object.freeze({ room: 'poker', fields: ['roundId', 'points', 'high', 'lock'], required: ['roundId', 'points'], ephemeral: false }),
  'poker.lock.set': Object.freeze({ room: 'poker', fields: ['roundId', 'locked'], required: ['roundId', 'locked'], ephemeral: false }),
  'poker.abstain.set': Object.freeze({ room: 'poker', fields: ['roundId', 'abstain'], required: ['roundId', 'abstain'], ephemeral: false }),
  'poker.round.reveal': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.replay': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.create': Object.freeze({ room: 'poker', fields: ['roundId', 'ticketKey', 'pending', 'forceNew'], required: ['roundId'], ephemeral: false }),
  'poker.round.createMany': Object.freeze({ room: 'poker', fields: ['rounds'], required: ['rounds'], ephemeral: false }),
  'poker.round.next': Object.freeze({ room: 'poker', fields: ['fromRoundId', 'newRoundId', 'ticketKey'], required: ['fromRoundId', 'newRoundId'], ephemeral: false }),
  'poker.round.end': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.activate': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.setTicket': Object.freeze({ room: 'poker', fields: ['roundId', 'ticketKey', 'baseVersion'], required: ['roundId', 'ticketKey', 'baseVersion'], ephemeral: false }),
  'poker.round.commitPending': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.discardPending': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.delete': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: false }),
  'poker.round.reorder': Object.freeze({ room: 'poker', fields: ['order'], required: ['order'], ephemeral: false }),
  'poker.rounds.clearAll': Object.freeze({ room: 'poker', fields: ['newRoundId'], required: ['newRoundId'], ephemeral: false }),
  'poker.settings.update': Object.freeze({ room: 'poker', fields: ['expected', 'presenceEnabled', 'followSpotlight'], required: ['expected'], ephemeral: false }),
  'poker.observer.set': Object.freeze({ room: 'poker', fields: ['principalId', 'observer'], required: ['principalId', 'observer'], ephemeral: false }),
  'poker.spotlight.take': Object.freeze({ room: 'poker', fields: ['roundId', 'force'], required: ['roundId', 'force'], ephemeral: false }),
  'poker.spotlight.move': Object.freeze({ room: 'poker', fields: ['roundId'], required: ['roundId'], ephemeral: true }),
  'poker.spotlight.give': Object.freeze({ room: 'poker', fields: ['principalId'], required: ['principalId'], ephemeral: false }),
  'poker.spotlight.release': Object.freeze({ room: 'poker', fields: [], required: [], ephemeral: false }),
  'poker.jira.submitPoints': Object.freeze({ room: 'poker', fields: ['roundId', 'points'], required: ['roundId', 'points'], ephemeral: false }),
  'retro.card.add': Object.freeze({ room: 'board', fields: ['cardId', 'columnId', 'body', 'anonymous'], required: ['cardId', 'columnId', 'body', 'anonymous'], ephemeral: false }),
  'retro.card.edit': Object.freeze({ room: 'board', fields: ['cardId', 'body', 'baseVersion'], required: ['cardId', 'body', 'baseVersion'], ephemeral: false }),
  'retro.card.delete': Object.freeze({ room: 'board', fields: ['cardId'], required: ['cardId'], ephemeral: false }),
  'retro.vote.set': Object.freeze({ room: 'board', fields: ['cardId', 'on'], required: ['cardId', 'on'], ephemeral: false }),
  'retro.comment.add': Object.freeze({ room: 'board', fields: ['commentId', 'cardId', 'body', 'anonymous'], required: ['commentId', 'cardId', 'body', 'anonymous'], ephemeral: false }),
  'retro.comment.delete': Object.freeze({ room: 'board', fields: ['commentId'], required: ['commentId'], ephemeral: false }),
  'retro.column.add': Object.freeze({ room: 'board', fields: ['columnId', 'title', 'color'], required: ['columnId', 'title', 'color'], ephemeral: false }),
  'retro.column.update': Object.freeze({ room: 'board', fields: ['columnId', 'baseVersion', 'title', 'color'], required: ['columnId', 'baseVersion'], ephemeral: false }),
  'retro.column.delete': Object.freeze({ room: 'board', fields: ['columnId'], required: ['columnId'], ephemeral: false }),
  'retro.column.reorder': Object.freeze({ room: 'board', fields: ['order'], required: ['order'], ephemeral: false }),
  'retro.column.setActionItems': Object.freeze({ room: 'board', fields: ['columnId'], required: ['columnId'], ephemeral: false }),
  'retro.board.rename': Object.freeze({ room: 'board', fields: ['title', 'expected'], required: ['title', 'expected'], ephemeral: false }),
  'retro.config.update': Object.freeze({ room: 'board', fields: ['patch', 'expected'], required: ['patch', 'expected'], ephemeral: false }),
  'retro.stage.set': Object.freeze({ room: 'board', fields: ['from', 'to'], required: ['from', 'to'], ephemeral: false }),
  'retro.ready.set': Object.freeze({ room: 'board', fields: ['stage', 'ready'], required: ['stage', 'ready'], ephemeral: false }),
  'retro.focus.set': Object.freeze({ room: 'board', fields: ['cardId', 'from'], required: ['cardId', 'from'], ephemeral: true }),
  'retro.timer.start': Object.freeze({ room: 'board', fields: ['durationMs', 'alarm', 'music', 'from'], required: ['durationMs', 'alarm', 'music', 'from'], ephemeral: true }),
  'retro.timer.pause': Object.freeze({ room: 'board', fields: ['from'], required: ['from'], ephemeral: true }),
  'retro.timer.resume': Object.freeze({ room: 'board', fields: ['from'], required: ['from'], ephemeral: true }),
  'retro.timer.reset': Object.freeze({ room: 'board', fields: ['from'], required: ['from'], ephemeral: true }),
  'retro.timer.setFlags': Object.freeze({ room: 'board', fields: ['alarm', 'music'], required: [], ephemeral: true }),
  'retro.endorse.give': Object.freeze({ room: 'board', fields: ['endorsementId', 'toUserId', 'typeId'], required: ['endorsementId', 'toUserId', 'typeId'], ephemeral: false }),
  'retro.endorse.revoke': Object.freeze({ room: 'board', fields: ['endorsementId'], required: ['endorsementId'], ephemeral: false }),
  'team.action.assign': Object.freeze({ room: 'team', fields: ['itemId', 'userId'], required: ['itemId', 'userId'], ephemeral: false }),
  'team.action.done': Object.freeze({ room: 'team', fields: ['itemId', 'done'], required: ['itemId', 'done'], ephemeral: false }),
  'team.action.delete': Object.freeze({ room: 'team', fields: ['itemId'], required: ['itemId'], ephemeral: false }),
});

/** Event kind → rooms that emit it and its payload members. */
export const EVENTS = Object.freeze({
  'poker.vote.state': Object.freeze({ rooms: ['poker'], fields: ['roundId', 'pid', 'points', 'high', 'locked', 'abstained', 'status'] }),
  'poker.round.revealed': Object.freeze({ rooms: ['poker'], fields: ['roundId', 'votes', 'winner', 'at', 'by'] }),
  'poker.round.replayed': Object.freeze({ rooms: ['poker'], fields: ['roundId'] }),
  'poker.round.created': Object.freeze({ rooms: ['poker'], fields: ['round'] }),
  'poker.round.updated': Object.freeze({ rooms: ['poker'], fields: ['round'] }),
  'poker.round.deleted': Object.freeze({ rooms: ['poker'], fields: ['roundId'] }),
  'poker.round.order': Object.freeze({ rooms: ['poker'], fields: ['order'] }),
  'poker.rounds.reset': Object.freeze({ rooms: ['poker'], fields: ['round'] }),
  'poker.pointer': Object.freeze({ rooms: ['poker'], fields: ['roundId', 'cause', 'nav'] }),
  'poker.spotlight': Object.freeze({ rooms: ['poker'], fields: ['holder', 'roundId'] }),
  'poker.settings': Object.freeze({ rooms: ['poker'], fields: ['presenceEnabled', 'followSpotlight'] }),
  'poker.observers': Object.freeze({ rooms: ['poker'], fields: ['ids'] }),
  'poker.roster': Object.freeze({ rooms: ['poker'], fields: ['seats'] }),
  'poker.jira.points': Object.freeze({ rooms: ['poker'], fields: ['roundId', 'key', 'points', 'status', 'by', 'code'] }),
  'poker.session.deleted': Object.freeze({ rooms: ['poker'], fields: [] }),
  'retro.card.added': Object.freeze({ rooms: ['board'], fields: ['card'] }),
  'retro.card.edited': Object.freeze({ rooms: ['board'], fields: ['cardId', 'body', 'version'] }),
  'retro.card.deleted': Object.freeze({ rooms: ['board'], fields: ['cardId'] }),
  'retro.vote': Object.freeze({ rooms: ['board'], fields: ['cardId', 'count', 'voters', 'mine'] }),
  'retro.comment.added': Object.freeze({ rooms: ['board'], fields: ['comment'] }),
  'retro.comment.deleted': Object.freeze({ rooms: ['board'], fields: ['commentId', 'cardId'] }),
  'retro.column.added': Object.freeze({ rooms: ['board'], fields: ['column'] }),
  'retro.column.updated': Object.freeze({ rooms: ['board'], fields: ['column'] }),
  'retro.column.deleted': Object.freeze({ rooms: ['board'], fields: ['columnId'] }),
  'retro.column.order': Object.freeze({ rooms: ['board'], fields: ['order'] }),
  'retro.column.actionItems': Object.freeze({ rooms: ['board'], fields: ['columnId'] }),
  'retro.board.meta': Object.freeze({ rooms: ['board'], fields: ['title', 'visibility', 'archived'] }),
  'retro.config': Object.freeze({ rooms: ['board'], fields: ['config'] }),
  'retro.stage': Object.freeze({ rooms: ['board'], fields: ['stage'] }),
  'retro.readiness': Object.freeze({ rooms: ['board'], fields: ['stage', 'ready'] }),
  'retro.focus': Object.freeze({ rooms: ['board'], fields: ['cardId'] }),
  'retro.timer': Object.freeze({ rooms: ['board'], fields: ['state', 'durationMs', 'alarm', 'music', 'endsAt', 'remainingMs', 'expired'] }),
  'retro.endorsement.added': Object.freeze({ rooms: ['board'], fields: ['endorsement', 'celebrate'] }),
  'retro.endorsement.removed': Object.freeze({ rooms: ['board'], fields: ['endorsementId'] }),
  'team.action.added': Object.freeze({ rooms: ['team'], fields: ['item'] }),
  'team.action.updated': Object.freeze({ rooms: ['team'], fields: ['item'] }),
  'team.action.text': Object.freeze({ rooms: ['team'], fields: ['itemId', 'text', 'sourceVersion'] }),
  'team.action.deleted': Object.freeze({ rooms: ['team'], fields: ['itemId'] }),
  'team.action.comments': Object.freeze({ rooms: ['team'], fields: ['itemId', 'count'] }),
  'team.members': Object.freeze({ rooms: ['team'], fields: ['members'] }),
  'team.boards.changed': Object.freeze({ rooms: ['team'], fields: ['boardId'] }),
  'team.poker.changed': Object.freeze({ rooms: ['team'], fields: ['sessionId', 'pointed', 'total'] }),
  'team.settings': Object.freeze({ rooms: ['poker', 'board', 'team'], fields: ['pointDescriptions', 'maxEndorsements'] }),
  'presence.join': Object.freeze({ rooms: ['poker', 'board'], fields: ['pid', 'kind', 'name', 'avatar'] }),
  'presence.leave': Object.freeze({ rooms: ['poker', 'board'], fields: ['pid'] }),
  'presence.update': Object.freeze({ rooms: ['poker', 'board'], fields: ['pid', 'name', 'avatar'] }),
});

/** Unsequenced (`x` frame) kind → rooms and payload members. */
export const UNSEQUENCED = Object.freeze({
  'jira.meta': Object.freeze({ rooms: ['poker'], fields: ['key', 'summary', 'issueType', 'status', 'parentKey', 'parentSummary'] }),
});

/** Kinds the outbox drops instead of resending after 10 s offline. */
export const EPHEMERAL_KINDS = Object.freeze(['poker.spotlight.move', 'retro.focus.set', 'retro.timer.start', 'retro.timer.pause', 'retro.timer.resume', 'retro.timer.reset', 'retro.timer.setFlags']);

/** Nack code → whether the client keeps the outbox entry and retries. */
export const NACK = Object.freeze({
  forbidden: Object.freeze({ retry: false }),
  not_subscribed: Object.freeze({ retry: false }),
  not_found: Object.freeze({ retry: false }),
  invalid: Object.freeze({ retry: false }),
  stale: Object.freeze({ retry: false }),
  round_revealed: Object.freeze({ retry: false }),
  round_not_voting: Object.freeze({ retry: false }),
  round_inactive: Object.freeze({ retry: false }),
  round_pending: Object.freeze({ retry: false }),
  vote_locked: Object.freeze({ retry: false }),
  observer: Object.freeze({ retry: false }),
  stage_forbids: Object.freeze({ retry: false }),
  board_archived: Object.freeze({ retry: false }),
  vote_limit: Object.freeze({ retry: false }),
  self_vote: Object.freeze({ retry: false }),
  endorse_limit: Object.freeze({ retry: false }),
  spotlight_held: Object.freeze({ retry: false }),
  not_holder: Object.freeze({ retry: false }),
  jira_not_configured: Object.freeze({ retry: false }),
  jira_pending: Object.freeze({ retry: false }),
  too_large: Object.freeze({ retry: false }),
  cid_reuse: Object.freeze({ retry: false }),
  internal: Object.freeze({ retry: false }),
  rate_limited: Object.freeze({ retry: true }),
  server_busy: Object.freeze({ retry: true }),
  jira_unavailable: Object.freeze({ retry: true }),
});

export const KICK_CODES = Object.freeze(['access_revoked', 'deleted', 'not_found', 'forbidden', 'too_many_rooms']);
export const BYE_CODES = Object.freeze(['restart']);

/** WebSocket close codes. */
export const CLOSE = Object.freeze({
  RESTART: 1012,
  SLOW_CONSUMER: 4008,
  PROTOCOL_VIOLATION: 4400,
  UNAUTHENTICATED: 4401,
  BAD_ORIGIN: 4403,
  CID_IN_USE: 4409,
  FRAME_TOO_LARGE: 4413,
  RATE_LIMITED: 4429,
});

/** Wire values of every enum used in payloads, in declaration order. */
export const ENUMS = Object.freeze({
  roomKind: Object.freeze(['poker', 'board', 'team']),
  principalKind: Object.freeze(['user', 'guest', 'external']),
  teamRole: Object.freeze(['member', 'admin', 'owner']),
  roundState: Object.freeze(['voting', 'revealed']),
  voteStatus: Object.freeze(['empty', 'selected', 'locked']),
  pointerCause: Object.freeze(['next', 'create', 'createMany', 'commit', 'reset', 'repair']),
  jiraSubmitStatus: Object.freeze(['pending', 'ok', 'failed']),
  retroStage: Object.freeze(['thinking', 'voting', 'discussing', 'closed']),
  timerState: Object.freeze(['idle', 'running', 'paused']),
  boardVisibility: Object.freeze(['link', 'team', 'password']),
  columnColor: Object.freeze(['green', 'red', 'blue', 'yellow', 'purple', 'pink', 'indigo', 'orange', 'gray']),
});

export function isRetryable(code) {
  return NACK[code]?.retry === true;
}
