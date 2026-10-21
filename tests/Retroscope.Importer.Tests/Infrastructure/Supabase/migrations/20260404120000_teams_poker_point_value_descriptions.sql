-- Optional per-point labels for the planning poker deck (shown under "Your Hand" when set).
-- Shape: { "1": "half a day", "2": "...", ... } — keys are stringified point values.

ALTER TABLE public.teams
  ADD COLUMN IF NOT EXISTS poker_point_value_descriptions jsonb NOT NULL DEFAULT '{}'::jsonb;

COMMENT ON COLUMN public.teams.poker_point_value_descriptions IS
  'JSON map of deck point value (string key) to short team-defined label for poker UI.';
