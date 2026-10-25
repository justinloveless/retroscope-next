# Retroscope wire protocol

<!-- GENERATED from src/Retroscope.Core/Protocol/Catalog.cs by ProtocolCatalogTests.GenerateDocs. Do not edit. -->

Protocol version 1. One same-origin WebSocket per tab at `GET /ws`; every frame is one UTF-8 JSON object of at most 65536 bytes. Members may appear in any order. Ids are lower-case hyphenated UUID strings, room keys are `poker:<uuid>`, `board:<uuid>` or `team:<uuid>`, epochs are 16 hex digits, and timestamps are Unix milliseconds of server time. Members marked `?` are omitted when empty; unknown members are refused. Rules and rationale: DESIGN.md §c.3 and §c.4.

## Frames

| `t` | From | Members | Notes |
|---|---|---|---|
| `hello` | client | v, cid, subs:[{r, e?, s?}] | First frame; resumes every room at once. |
| `sub` | client | r, e?, s? | Subscribe (idempotent); `e`/`s` resume from a known point. |
| `unsub` | client | r |  |
| `cmd` | client | r, q, k, d | `q` strictly increases per `cid`; `d` is the payload of command `k`. |
| `ping` | client | c | Clock sample only (liveness is protocol PING/PONG). |
| `welcome` | server | conn, me:{pid, kind, name, avatar?}, n | `n` seeds the clock offset. |
| `snap` | server | r, e, s, lastQ, d, o? | `d` is the room snapshot, `o` the per-viewer overlay (poker, board). |
| `replay` | server | r, e, from, lastQ, txs:[{s, ev}] | Missed tx frames; empty `txs` means current. |
| `tx` | server | r, s, ev:[{k, d}] | One committed command or internal transition; `ev` may be empty. |
| `ack` | server | r, q, s, dup? | Sent after the tx frame it produced. |
| `nack` | server | r, q, code, retry, msg?, d? | `retry` follows from `code`; `stale` carries `d.current`. |
| `x` | server | r, k, d | Unsequenced, idempotent cache fill. |
| `pong` | server | c, n | Clock offset estimation. |
| `kick` | server | r, code | The room is dropped for this connection. |
| `bye` | server | code, retryMs | Sent before a graceful close; reconnect after `retryMs`. |

## Commands

| Kind | Rooms | `d` | Summary |
|---|---|---|---|
| `poker.vote.set` | poker | `roundId`: uuid (RoundId)<br>`points`: integer<br>`high?`: integer<br>`lock?`: boolean | Select a card (and `high` for a between vote); `lock:true` selects and locks at once. |
| `poker.lock.set` | poker | `roundId`: uuid (RoundId)<br>`locked`: boolean | Lock or unlock the caller's vote. |
| `poker.abstain.set` | poker | `roundId`: uuid (RoundId)<br>`abstain`: boolean | Abstain (locked) or un-abstain (empties the slot). |
| `poker.round.reveal` | poker | `roundId`: uuid (RoundId) | Reveal; unlocked votes become abstains. |
| `poker.round.replay` | poker | `roundId`: uuid (RoundId) | Back to voting with non-abstain votes kept and locked. |
| `poker.round.create` | poker | `roundId`: uuid (RoundId)<br>`ticketKey?`: string<br>`pending?`: boolean<br>`forceNew?`: boolean | New round, or fill the placeholder; `pending` previews for the spotlight holder. |
| `poker.round.createMany` | poker | `rounds`: NewRound[] | Batch add (at most 50) from a Jira bucket. |
| `poker.round.next` | poker | `fromRoundId`: uuid (RoundId)<br>`newRoundId`: uuid (RoundId)<br>`ticketKey?`: string | Next round from `fromRoundId` (must still be active, else `stale`). |
| `poker.round.end` | poker | `roundId`: uuid (RoundId) | Deactivate a round. |
| `poker.round.activate` | poker | `roundId`: uuid (RoundId) | Reactivate a voting round. |
| `poker.round.setTicket` | poker | `roundId`: uuid (RoundId)<br>`ticketKey`: string \| null<br>`baseVersion`: integer | Set or clear the ticket key; `baseVersion` precondition. |
| `poker.round.commitPending` | poker | `roundId`: uuid (RoundId) | Turn the caller's pending preview into a real round. |
| `poker.round.discardPending` | poker | `roundId`: uuid (RoundId) | Delete the caller's pending preview. |
| `poker.round.delete` | poker | `roundId`: uuid (RoundId) | Delete a round (spotlight and pointer repaired in the same transaction). |
| `poker.round.reorder` | poker | `order`: uuid (RoundId)[] | New order; must be a permutation of the current rounds, else `stale`. |
| `poker.rounds.clearAll` | poker | `newRoundId`: uuid (RoundId) | Delete every round and start again at round 1. |
| `poker.settings.update` | poker | `expected`: PokerSettingsValues<br>`presenceEnabled?`: boolean<br>`followSpotlight?`: boolean | Change session settings; `expected` precondition per field. |
| `poker.observer.set` | poker | `principalId`: uuid (PrincipalId)<br>`observer`: boolean | Make a principal an observer or a voter. |
| `poker.spotlight.take` | poker | `roundId`: uuid (RoundId)<br>`force`: boolean | Take the spotlight; without `force` a held spotlight nacks `spotlight_held`. |
| `poker.spotlight.move` | poker | `roundId`: uuid (RoundId) | Move the spotlight (holder's bound tab only; ephemeral-durable). Ephemeral. |
| `poker.spotlight.give` | poker | `principalId`: uuid (PrincipalId) | Hand the spotlight to a present principal. |
| `poker.spotlight.release` | poker | `{}` | Release the spotlight. |
| `poker.jira.submitPoints` | poker | `roundId`: uuid (RoundId)<br>`points`: number | Write story points to the round's Jira issue. |
| `retro.card.add` | board | `cardId`: uuid (CardId)<br>`columnId`: uuid (ColumnId)<br>`body`: string<br>`anonymous`: boolean | Add a card. |
| `retro.card.edit` | board | `cardId`: uuid (CardId)<br>`body`: string<br>`baseVersion`: integer | Edit a card; `baseVersion` precondition. |
| `retro.card.delete` | board | `cardId`: uuid (CardId) | Delete a card. |
| `retro.vote.set` | board | `cardId`: uuid (CardId)<br>`on`: boolean | Vote on (or un-vote) a card. |
| `retro.comment.add` | board | `commentId`: uuid (CommentId)<br>`cardId`: uuid (CardId)<br>`body`: string<br>`anonymous`: boolean | Comment on a card. |
| `retro.comment.delete` | board | `commentId`: uuid (CommentId) | Delete a comment. |
| `retro.column.add` | board | `columnId`: uuid (ColumnId)<br>`title`: string<br>`color`: ColumnColor | Add a column (facilitator). |
| `retro.column.update` | board | `columnId`: uuid (ColumnId)<br>`baseVersion`: integer<br>`title?`: string<br>`color?`: ColumnColor | Rename or recolour a column; `baseVersion` precondition. |
| `retro.column.delete` | board | `columnId`: uuid (ColumnId) | Delete a column and its cards. |
| `retro.column.reorder` | board | `order`: uuid (ColumnId)[] | New column order; must be a permutation, else `stale`. |
| `retro.column.setActionItems` | board | `columnId`: uuid (ColumnId) \| null | Choose the action-items column (or none). |
| `retro.board.rename` | board | `title`: string<br>`expected`: string | Rename the board; `expected` precondition. |
| `retro.config.update` | board | `patch`: RetroConfigPatch<br>`expected`: RetroConfigPatch | Patch board settings; `expected` precondition per field. |
| `retro.stage.set` | board | `from`: RetroStage<br>`to`: RetroStage | Change stage (compare-and-set on `from`). |
| `retro.ready.set` | board | `stage`: RetroStage<br>`ready`: boolean | Mark the caller ready for the current stage. |
| `retro.focus.set` | board | `cardId`: uuid (CardId) \| null<br>`from`: uuid (CardId) \| null | Focus a card (compare-and-set on `from`). Ephemeral. |
| `retro.timer.start` | board | `durationMs`: integer<br>`alarm`: boolean<br>`music`: boolean<br>`from`: TimerState | Start the timer (compare-and-set on `from`). Ephemeral. |
| `retro.timer.pause` | board | `from`: TimerState | Pause the timer. Ephemeral. |
| `retro.timer.resume` | board | `from`: TimerState | Resume the timer. Ephemeral. |
| `retro.timer.reset` | board | `from`: TimerState | Reset the timer to idle. Ephemeral. |
| `retro.timer.setFlags` | board | `alarm?`: boolean<br>`music?`: boolean | Toggle the alarm and music flags. Ephemeral. |
| `retro.endorse.give` | board | `endorsementId`: uuid (EndorsementId)<br>`toUserId`: uuid (PrincipalId)<br>`typeId`: uuid (EndorsementTypeId) | Endorse a team member (team boards). |
| `retro.endorse.revoke` | board | `endorsementId`: uuid (EndorsementId) | Withdraw the caller's endorsement. |
| `team.action.assign` | team | `itemId`: uuid (ActionItemId)<br>`userId`: uuid (PrincipalId) \| null | Assign an action item (or unassign). |
| `team.action.done` | team | `itemId`: uuid (ActionItemId)<br>`done`: boolean | Mark an action item done or open. |
| `team.action.delete` | team | `itemId`: uuid (ActionItemId) | Delete an action item (admin). |

## Events

| Kind | Rooms | `d` | Summary |
|---|---|---|---|
| `poker.vote.state` | poker | `roundId`: uuid (RoundId)<br>`pid`: uuid (PrincipalId)<br>`points?`: integer<br>`high?`: integer<br>`locked?`: boolean<br>`abstained?`: boolean<br>`status?`: VoteStatus | A vote changed: values to the voter, status only to everyone else. |
| `poker.round.revealed` | poker | `roundId`: uuid (RoundId)<br>`votes`: RevealedVote[]<br>`winner`: integer<br>`at`: integer<br>`by?`: uuid (PrincipalId) | Every vote with values, and the winner. |
| `poker.round.replayed` | poker | `roundId`: uuid (RoundId) | The round is voting again. |
| `poker.round.created` | poker | `round`: RoundInfo | A new round. |
| `poker.round.updated` | poker | `round`: RoundInfo | A round's new state (whole round). |
| `poker.round.deleted` | poker | `roundId`: uuid (RoundId) | A round was deleted. |
| `poker.round.order` | poker | `order`: uuid (RoundId)[] | The new round order. |
| `poker.rounds.reset` | poker | `round`: RoundInfo | Every round was replaced by this round 1. |
| `poker.pointer` | poker | `roundId`: uuid (RoundId)<br>`cause`: PointerCause<br>`nav`: boolean | The current-round pointer moved (`nav` rule, R13). |
| `poker.spotlight` | poker | `holder?`: uuid (PrincipalId)<br>`roundId?`: uuid (RoundId) | Spotlight holder and round; empty = released. |
| `poker.settings` | poker | `presenceEnabled`: boolean<br>`followSpotlight`: boolean | Session settings. |
| `poker.observers` | poker | `ids`: uuid (PrincipalId)[] | Every observer. |
| `poker.roster` | poker | `seats`: PrincipalInfo[] | Team members shown as seats. |
| `poker.jira.points` | poker | `roundId`: uuid (RoundId)<br>`key`: string<br>`points`: number<br>`status`: JiraSubmitStatus<br>`by`: uuid (PrincipalId)<br>`code?`: string | Jira story-point submit state, shared by all. |
| `poker.session.deleted` | poker | `{}` | The session is gone; a kick follows. |
| `retro.card.added` | board | `card`: CardInfo | A new card. |
| `retro.card.edited` | board | `cardId`: uuid (CardId)<br>`body`: string<br>`version`: integer | A card's new body and version. |
| `retro.card.deleted` | board | `cardId`: uuid (CardId) | A card was deleted. |
| `retro.vote` | board | `cardId`: uuid (CardId)<br>`count`: integer<br>`voters?`: uuid (PrincipalId)[]<br>`mine?`: boolean | A card's vote count (voters when shown). |
| `retro.comment.added` | board | `comment`: CommentInfo | A new comment. |
| `retro.comment.deleted` | board | `commentId`: uuid (CommentId)<br>`cardId`: uuid (CardId) | A comment was deleted. |
| `retro.column.added` | board | `column`: ColumnInfo | A new column. |
| `retro.column.updated` | board | `column`: ColumnInfo | A column's new state. |
| `retro.column.deleted` | board | `columnId`: uuid (ColumnId) | A column and its cards were deleted. |
| `retro.column.order` | board | `order`: uuid (ColumnId)[] | The new column order. |
| `retro.column.actionItems` | board | `columnId?`: uuid (ColumnId) | The action-items column; empty = none. |
| `retro.board.meta` | board | `title`: string<br>`archived`: boolean | Title and archived flag. |
| `retro.config` | board | `config`: RetroConfig | The whole board configuration. |
| `retro.stage` | board | `stage`: RetroStage | The current stage. |
| `retro.readiness` | board | `stage`: RetroStage<br>`ready`: uuid (PrincipalId)[] | Who is ready for the stage. |
| `retro.focus` | board | `cardId?`: uuid (CardId) | The focused card; empty = none. |
| `retro.timer` | board | `state`: TimerState<br>`durationMs`: integer<br>`alarm`: boolean<br>`music`: boolean<br>`endsAt?`: integer<br>`remainingMs?`: integer<br>`expired?`: boolean | Timer state on the server clock. |
| `retro.endorsement.added` | board | `endorsement`: EndorsementInfo<br>`celebrate?`: boolean | An endorsement (team members only). |
| `retro.endorsement.removed` | board | `endorsementId`: uuid (EndorsementId) | An endorsement was withdrawn. |
| `retro.members` | board | `members`: MemberInfo[] | The team's members with roles (team members only). |
| `team.action.added` | team | `item`: ActionItemInfo | A new action item. |
| `team.action.updated` | team | `item`: ActionItemInfo | An action item's new state. |
| `team.action.text` | team | `itemId`: uuid (ActionItemId)<br>`text`: string<br>`sourceVersion`: integer | Text synced from the source card. |
| `team.action.deleted` | team | `itemId`: uuid (ActionItemId) | An action item was deleted. |
| `team.action.comments` | team | `itemId`: uuid (ActionItemId)<br>`count`: integer | Comment count changed; refetch the comments over HTTP. |
| `team.members` | team | `members`: MemberInfo[] | Every member with role. |
| `team.boards.changed` | team | `boardId`: uuid (BoardId) | Refetch the board list. |
| `team.poker.changed` | team | `sessionId`: uuid (SessionId)<br>`pointed`: integer<br>`total`: integer | A session's progress changed. |
| `team.settings` | poker, board, team | `pointDescriptions?`: {string: string}<br>`maxEndorsements?`: integer | Team settings that this room uses. |
| `presence.join` | poker, board | `pid`: uuid (PrincipalId)<br>`kind`: PrincipalKind<br>`name`: string<br>`avatar?`: uuid (BlobId) | A principal arrived. |
| `presence.leave` | poker, board | `pid`: uuid (PrincipalId) | A principal left (after the grace period). |
| `presence.update` | poker, board | `pid`: uuid (PrincipalId)<br>`name`: string<br>`avatar?`: uuid (BlobId) | A present principal's name or avatar changed. |

## Unsequenced

| Kind | Rooms | `d` | Summary |
|---|---|---|---|
| `jira.meta` | poker | `key`: string<br>`summary?`: string<br>`issueType?`: string<br>`status?`: string<br>`parentKey?`: string<br>`parentSummary?`: string | Jira issue title, type, status and parent (in-memory cache fill). |

## Snapshots

| Room | `d` | `o` |
|---|---|---|
| poker | `PokerSnapshot` | `PokerOverlay` |
| board | `RetroSnapshot` | `RetroOverlay` |
| team | `TeamSnapshot` | none |

## Shapes

### ActionItemInfo

| Member | Type |
|---|---|
| `id` | uuid (ActionItemId) |
| `text` | string |
| `sourceVersion` | integer |
| `createdAt` | integer |
| `commentCount` | integer |
| `sourceBoardId?` | uuid (BoardId) |
| `sourceCardId?` | uuid (CardId) |
| `assignedTo?` | uuid (PrincipalId) |
| `doneAt?` | integer |
| `doneBy?` | uuid (PrincipalId) |
| `createdBy?` | uuid (PrincipalId) |

### CardInfo

| Member | Type |
|---|---|
| `id` | uuid (CardId) |
| `columnId` | uuid (ColumnId) |
| `body` | string |
| `anonymous` | boolean |
| `version` | integer |
| `createdAt` | integer |
| `author?` | AuthorInfo |
| `mine?` | boolean |

### AuthorInfo

| Member | Type |
|---|---|
| `name` | string |
| `guest` | boolean |
| `pid?` | uuid (PrincipalId) |
| `avatar?` | uuid (BlobId) |

### ColumnInfo

| Member | Type |
|---|---|
| `id` | uuid (ColumnId) |
| `title` | string |
| `color` | ColumnColor |
| `actionItems` | boolean |
| `version` | integer |

### CommentInfo

| Member | Type |
|---|---|
| `id` | uuid (CommentId) |
| `cardId` | uuid (CardId) |
| `body` | string |
| `anonymous` | boolean |
| `createdAt` | integer |
| `author?` | AuthorInfo |
| `mine?` | boolean |

### EndorsementInfo

| Member | Type |
|---|---|
| `id` | uuid (EndorsementId) |
| `typeId` | uuid (EndorsementTypeId) |
| `from` | uuid (PrincipalId) |
| `to` | uuid (PrincipalId) |
| `createdAt` | integer |

### MemberInfo

| Member | Type |
|---|---|
| `pid` | uuid (PrincipalId) |
| `name` | string |
| `role` | TeamRole |
| `avatar?` | uuid (BlobId) |

### NewRound

| Member | Type |
|---|---|
| `roundId` | uuid (RoundId) |
| `ticketKey?` | string |

### PokerOverlay

| Member | Type |
|---|---|
| `own` | PokerVoteState[] |
| `heldByThisTab` | boolean |

### PokerSettingsValues

| Member | Type |
|---|---|
| `presenceEnabled?` | boolean |
| `followSpotlight?` | boolean |

### PokerSnapshot

| Member | Type |
|---|---|
| `sessionId` | uuid (SessionId) |
| `code` | string |
| `presenceEnabled` | boolean |
| `followSpotlight` | boolean |
| `observers` | uuid (PrincipalId)[] |
| `rounds` | RoundInfo[] |
| `votes` | PokerVoteState[] |
| `roster` | PrincipalInfo[] |
| `presence` | PrincipalInfo[] |
| `teamId` | uuid (TeamId) |
| `spotlight?` | SpotlightInfo |
| `currentRoundId?` | uuid (RoundId) |
| `pointDescriptions?` | {string | string} |
| `jiraSubmits?` | PokerJiraPoints[] |

### PokerJiraPoints

| Member | Type |
|---|---|
| `roundId` | uuid (RoundId) |
| `key` | string |
| `points` | number |
| `status` | JiraSubmitStatus |
| `by` | uuid (PrincipalId) |
| `code?` | string |

### PokerVoteState

| Member | Type |
|---|---|
| `roundId` | uuid (RoundId) |
| `pid` | uuid (PrincipalId) |
| `points?` | integer |
| `high?` | integer |
| `locked?` | boolean |
| `abstained?` | boolean |
| `status?` | VoteStatus |

### PrincipalInfo

| Member | Type |
|---|---|
| `pid` | uuid (PrincipalId) |
| `kind` | PrincipalKind |
| `name` | string |
| `avatar?` | uuid (BlobId) |

### RetroConfig

| Member | Type |
|---|---|
| `allowAnonymous` | boolean |
| `votingEnabled` | boolean |
| `showAuthorNames` | boolean |
| `stagesEnabled` | boolean |
| `enforceReadiness` | boolean |
| `allowSelfVotes` | boolean |
| `voteEmoji` | string |
| `sortChronologically` | boolean |
| `maxVotes?` | integer |

### RetroConfigPatch

| Member | Type |
|---|---|
| `allowAnonymous?` | boolean |
| `votingEnabled?` | boolean |
| `maxVotes?` | integer |
| `showAuthorNames?` | boolean |
| `stagesEnabled?` | boolean |
| `enforceReadiness?` | boolean |
| `allowSelfVotes?` | boolean |
| `voteEmoji?` | string |
| `sortChronologically?` | boolean |

### RetroOverlay

| Member | Type |
|---|---|
| `mineCards` | uuid (CardId)[] |
| `mineComments` | uuid (CommentId)[] |
| `myVotes` | uuid (CardId)[] |

### RetroSnapshot

| Member | Type |
|---|---|
| `boardId` | uuid (BoardId) |
| `code` | string |
| `meta` | RetroBoardMeta |
| `config` | RetroConfig |
| `stage` | RetroStage |
| `timer` | RetroTimer |
| `columns` | ColumnInfo[] |
| `cards` | CardInfo[] |
| `comments` | CommentInfo[] |
| `votes` | RetroVoteChanged[] |
| `readiness` | RetroReadiness |
| `presence` | PrincipalInfo[] |
| `teamId` | uuid (TeamId) |
| `members` | MemberInfo[] |
| `endorsements` | EndorsementInfo[] |
| `endorsementTypes` | EndorsementTypeInfo[] |
| `maxEndorsements` | integer |
| `focus?` | uuid (CardId) |

### EndorsementTypeInfo

| Member | Type |
|---|---|
| `id` | uuid (EndorsementTypeId) |
| `name` | string |
| `emoji` | string |
| `archived` | boolean |
| `description?` | string |

### RetroBoardMeta

| Member | Type |
|---|---|
| `title` | string |
| `archived` | boolean |

### RetroReadiness

| Member | Type |
|---|---|
| `stage` | RetroStage |
| `ready` | uuid (PrincipalId)[] |

### RetroTimer

| Member | Type |
|---|---|
| `state` | TimerState |
| `durationMs` | integer |
| `alarm` | boolean |
| `music` | boolean |
| `endsAt?` | integer |
| `remainingMs?` | integer |
| `expired?` | boolean |

### RetroVoteChanged

| Member | Type |
|---|---|
| `cardId` | uuid (CardId) |
| `count` | integer |
| `voters?` | uuid (PrincipalId)[] |
| `mine?` | boolean |

### RevealedVote

| Member | Type |
|---|---|
| `pid` | uuid (PrincipalId) |
| `abstained` | boolean |
| `points?` | integer |
| `high?` | integer |

### RoundInfo

| Member | Type |
|---|---|
| `id` | uuid (RoundId) |
| `number` | integer |
| `version` | integer |
| `state` | RoundState |
| `active` | boolean |
| `pending` | boolean |
| `autoReveal` | boolean |
| `ticketKey?` | string |
| `parentKey?` | string |
| `pendingOwner?` | uuid (PrincipalId) |
| `winner?` | integer |
| `revealedAt?` | integer |
| `revealedBy?` | uuid (PrincipalId) |
| `jira?` | JiraPointsInfo |

### JiraPointsInfo

| Member | Type |
|---|---|
| `points` | number |
| `at` | integer |
| `by` | uuid (PrincipalId) |

### SpotlightInfo

| Member | Type |
|---|---|
| `holder` | uuid (PrincipalId) |
| `roundId` | uuid (RoundId) |

### TeamSnapshot

| Member | Type |
|---|---|
| `teamId` | uuid (TeamId) |
| `name` | string |
| `members` | MemberInfo[] |
| `actionItems` | ActionItemInfo[] |
| `endorsementTypes` | EndorsementTypeInfo[] |
| `pointDescriptions` | {string | string} |
| `maxEndorsements` | integer |

## Nack codes

| Code | Retry |
|---|---|
| `forbidden` | no |
| `not_subscribed` | no |
| `not_found` | no |
| `invalid` | no |
| `stale` | no |
| `round_revealed` | no |
| `round_not_voting` | no |
| `round_inactive` | no |
| `round_pending` | no |
| `vote_locked` | no |
| `observer` | no |
| `stage_forbids` | no |
| `board_archived` | no |
| `vote_limit` | no |
| `self_vote` | no |
| `endorse_limit` | no |
| `spotlight_held` | no |
| `not_holder` | no |
| `jira_not_configured` | no |
| `jira_pending` | no |
| `too_large` | no |
| `cid_reuse` | no |
| `internal` | no |
| `rate_limited` | yes |
| `server_busy` | yes |
| `jira_unavailable` | yes |

## Kick and bye codes

Kick: `access_revoked`, `deleted`, `not_found`, `forbidden`, `too_many_rooms`. Bye: `restart`.

## Close codes

| Code | Meaning |
|---|---|
| 1012 | Restart |
| 4008 | SlowConsumer |
| 4400 | ProtocolViolation |
| 4401 | Unauthenticated |
| 4403 | BadOrigin |
| 4409 | CidInUse |
| 4413 | FrameTooLarge |
| 4429 | RateLimited |

## Enums

| Enum | Values |
|---|---|
| RoomKind | `poker`, `board`, `team` |
| PrincipalKind | `user`, `guest`, `external` |
| TeamRole | `member`, `admin`, `owner` |
| RoundState | `voting`, `revealed` |
| VoteStatus | `empty`, `selected`, `locked` |
| PointerCause | `next`, `create`, `createMany`, `commit`, `reset`, `repair` |
| JiraSubmitStatus | `pending`, `ok`, `failed` |
| RetroStage | `thinking`, `voting`, `discussing`, `closed` |
| TimerState | `idle`, `running`, `paused` |
| ColumnColor | `green`, `red`, `blue`, `yellow`, `purple`, `pink`, `indigo`, `orange`, `gray` |
