-- Legacy rows had NULL room_id; UI used `/teams/.../poker/${room_id}` which became `/poker/null`.
UPDATE public.poker_sessions
SET room_id = id::text
WHERE room_id IS NULL;
