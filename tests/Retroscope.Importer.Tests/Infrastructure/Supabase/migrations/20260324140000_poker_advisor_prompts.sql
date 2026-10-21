-- Team-wide and per-user instructions prepended to local advisor requests (planning poker context).

ALTER TABLE public.teams
  ADD COLUMN IF NOT EXISTS poker_advisor_team_prompt text;

COMMENT ON COLUMN public.teams.poker_advisor_team_prompt IS
  'Optional instructions sent with each local poker advisor request for this team (e.g. CLI skills, estimation norms).';

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS poker_advisor_personal_prompt text;

COMMENT ON COLUMN public.profiles.poker_advisor_personal_prompt IS
  'Optional personal instructions merged with team prompt for local poker advisor POST /advise.';
