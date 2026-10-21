-- Add is_active flag to poker_session_rounds so multiple rounds can be active concurrently.
-- Backfill: for each session_id, mark the latest round_number as active.

ALTER TABLE public.poker_session_rounds
ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT false;

WITH latest AS (
  SELECT
    session_id,
    MAX(round_number) AS max_round_number
  FROM public.poker_session_rounds
  GROUP BY session_id
)
UPDATE public.poker_session_rounds r
SET is_active = (r.round_number = l.max_round_number)
FROM latest l
WHERE r.session_id = l.session_id;

