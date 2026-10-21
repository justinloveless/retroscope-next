-- Purges look recent_activity up by entity (team purge, quick-room retention); the primary key leads with user_id.
create index recent_activity_entity_ix on recent_activity (entity_kind, entity_id);
