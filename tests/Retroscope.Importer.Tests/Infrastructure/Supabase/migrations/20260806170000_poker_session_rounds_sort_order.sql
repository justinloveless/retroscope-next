-- Display order for poker round selector chips (independent of stable round_number identity).
ALTER TABLE public.poker_session_rounds
  ADD COLUMN IF NOT EXISTS sort_order integer;

UPDATE public.poker_session_rounds
SET sort_order = round_number
WHERE sort_order IS NULL;

ALTER TABLE public.poker_session_rounds
  ALTER COLUMN sort_order SET DEFAULT 0,
  ALTER COLUMN sort_order SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_poker_session_rounds_sort_order
  ON public.poker_session_rounds(session_id, sort_order);

COMMENT ON COLUMN public.poker_session_rounds.sort_order IS
  'Order of this round in the poker session round selector strip; may differ from round_number after reordering.';
