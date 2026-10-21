-- Add vote_emoji to retro_board_config so teams can customize the upvote icon
alter table if exists public.retro_board_config
  add column if not exists vote_emoji text default '👍';


