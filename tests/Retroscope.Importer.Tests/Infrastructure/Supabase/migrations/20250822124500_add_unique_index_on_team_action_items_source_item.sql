-- Ensure we don't duplicate team action items when backfilling
create unique index if not exists team_action_items_source_item_id_unique
  on public.team_action_items(source_item_id)
  where source_item_id is not null;


