
-- Seed endorsement types for all existing teams that don't have them
DO $$
DECLARE
  t_id uuid;
BEGIN
  FOR t_id IN SELECT id FROM public.teams WHERE id NOT IN (SELECT DISTINCT team_id FROM public.endorsement_types) LOOP
    PERFORM public.seed_default_endorsement_types(t_id);
  END LOOP;
END;
$$;
