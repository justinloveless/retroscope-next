-- UX18 (docs/decisions/UX18.md): every room belongs to a team and only its members open it. Quick rooms, board
-- visibility and passwords, access grants, guest tokens, team favorites and the cross-team recent rooms are gone.
-- One release with no expand/contract split: the previous binary cannot run on this schema (deploy/RUNBOOK.md §11).
-- Every principal stays, guests and imported Slack voters included: their cards, votes, seats and readiness are history.

drop table board_access_grants;
drop table guest_tokens;
drop table team_favorites;

-- Client cursors and recent activity have no foreign key to the rooms, so the quick rooms' rows go by key. Recent
-- activity keeps only teams (me.lastTeamId); its board and poker rows fed the removed dashboard.
delete from client_cursors where room_key in (select 'poker:' || id::text from poker_sessions where team_id is null);
delete from client_cursors where room_key in (select 'board:' || id::text from retro_boards where team_id is null);
delete from recent_activity where entity_kind <> 'team';
alter table recent_activity
  drop constraint recent_activity_entity_kind_check,
  add constraint recent_activity_entity_kind_check check (entity_kind = 'team');

-- Quick rooms, with everything on them by cascade: rounds, votes and observers; columns, cards, comments, votes,
-- readiness, mentions and images. The partial indexes that found them go after the deletes that use them.
delete from poker_sessions where team_id is null;
delete from retro_boards where team_id is null;
drop index poker_sessions_quick_idle_ix;
drop index retro_boards_quick_idle_ix;

-- Dropping the board columns drops the checks that tie them together (and the one that tied 'team' to a team).
alter table poker_sessions alter column team_id set not null;
alter table retro_boards
  alter column team_id set not null,
  drop column visibility,
  drop column password_hash,
  drop column password_set_at;
