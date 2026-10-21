

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;


-- Note: Supabase-specific extension not available in standard Postgres
-- CREATE EXTENSION IF NOT EXISTS "pg_cron" WITH SCHEMA "pg_catalog";

-- Create necessary schemas for compatibility
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS extensions;
CREATE SCHEMA IF NOT EXISTS vault;
CREATE SCHEMA IF NOT EXISTS graphql;

-- Create Supabase roles for RLS policies
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'anon') THEN
        CREATE ROLE anon NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'authenticated') THEN
        CREATE ROLE authenticated NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'service_role') THEN
        CREATE ROLE service_role NOLOGIN;
    END IF;
END
$$;

-- Grant necessary permissions to roles
GRANT USAGE ON SCHEMA public TO anon, authenticated, service_role;
GRANT ALL ON ALL TABLES IN SCHEMA public TO anon, authenticated, service_role;
GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO anon, authenticated, service_role;
GRANT ALL ON ALL FUNCTIONS IN SCHEMA public TO anon, authenticated, service_role;

-- Create minimal auth.users table for foreign key constraints
CREATE TABLE IF NOT EXISTS auth.users (
    id uuid PRIMARY KEY,
    email text,
    created_at timestamptz DEFAULT now()
);

-- Create auth helper functions for RLS policies
CREATE OR REPLACE FUNCTION auth.uid() RETURNS uuid
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(
        current_setting('request.jwt.claim.sub', TRUE),
        (current_setting('request.jwt.claims', TRUE)::jsonb->>'sub')
    )::uuid;
$$;

CREATE OR REPLACE FUNCTION auth.role() RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(
        current_setting('request.jwt.claim.role', TRUE),
        (current_setting('request.jwt.claims', TRUE)::jsonb->>'role'),
        'anon'
    )::text;
$$;

COMMENT ON SCHEMA "public" IS 'standard public schema';



-- Note: Supabase-specific extension not available in standard Postgres
-- CREATE EXTENSION IF NOT EXISTS "pg_graphql" WITH SCHEMA "graphql";






CREATE EXTENSION IF NOT EXISTS "pg_stat_statements" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "extensions";






-- Note: Supabase-specific extension not available in standard Postgres
-- CREATE EXTENSION IF NOT EXISTS "supabase_vault" WITH SCHEMA "vault";






CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA "extensions";






-- Note: Supabase-specific extension not available in standard Postgres
-- CREATE EXTENSION IF NOT EXISTS "wrappers" WITH SCHEMA "extensions";






CREATE OR REPLACE FUNCTION "public"."accept_team_invitation"("invitation_token" "text") RETURNS "json"
    LANGUAGE "plpgsql" SECURITY DEFINER
    AS $$
DECLARE
  invitation_record public.team_invitations;
  result json;
BEGIN
  -- Get the invitation
  SELECT * INTO invitation_record
  FROM public.team_invitations
  WHERE token = invitation_token
    AND status = 'pending'
    AND is_active = true
    AND expires_at > now();
  
  IF invitation_record.id IS NULL THEN
    RETURN json_build_object('success', false, 'error', 'Invalid or expired invitation');
  END IF;
  
  -- Check if user is already a team member
  IF EXISTS (
    SELECT 1 FROM public.team_members
    WHERE team_id = invitation_record.team_id
      AND user_id = auth.uid()
  ) THEN
    RETURN json_build_object('success', false, 'error', 'Already a team member');
  END IF;
  
  -- Add user to team
  INSERT INTO public.team_members (team_id, user_id, role)
  VALUES (invitation_record.team_id, auth.uid(), 'member');
  
  -- Update invitation status only for email invitations
  -- Link invitations remain active for others to use
  IF invitation_record.invite_type = 'email' THEN
    UPDATE public.team_invitations
    SET status = 'accepted'
    WHERE id = invitation_record.id;
  END IF;
  
  RETURN json_build_object(
    'success', true, 
    'team_id', invitation_record.team_id,
    'team_name', (SELECT name FROM public.teams WHERE id = invitation_record.team_id)
  );
END;
$$;


ALTER FUNCTION "public"."accept_team_invitation"("invitation_token" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."cleanup_previous_stage_readiness"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    AS $$
BEGIN
    -- When stage changes, remove readiness records for the previous stage
    IF OLD.retro_stage IS DISTINCT FROM NEW.retro_stage THEN
        DELETE FROM public.retro_user_readiness 
        WHERE board_id = NEW.id 
        AND current_stage = OLD.retro_stage;
    END IF;
    
    RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."cleanup_previous_stage_readiness"() OWNER TO "postgres";


COMMENT ON FUNCTION "public"."cleanup_previous_stage_readiness"() IS 'Automatically cleans up readiness records when stage changes';



CREATE OR REPLACE FUNCTION "public"."cleanup_stale_poker_sessions"() RETURNS "void"
    LANGUAGE "plpgsql"
    AS $$begin
  delete from public.poker_sessions
  where team_id is null and last_activity_at < now() - interval '1 hour';
end;$$;


ALTER FUNCTION "public"."cleanup_stale_poker_sessions"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."create_columns_from_template"("board_id" "uuid", "template_id" "uuid") RETURNS "void"
    LANGUAGE "plpgsql"
    AS $$
BEGIN
  IF template_id IS NOT NULL THEN
    -- Create columns from template (including is_action_items)
    INSERT INTO public.retro_columns (board_id, title, color, position, is_action_items)
    SELECT create_columns_from_template.board_id, tc.title, tc.color, tc.position, tc.is_action_items
    FROM public.template_columns tc
    WHERE tc.template_id = create_columns_from_template.template_id
    ORDER BY tc.position;
  ELSE
    -- Create default columns (with proper is_action_items values)
    INSERT INTO public.retro_columns (board_id, title, color, position, is_action_items) VALUES
      (create_columns_from_template.board_id, 'Good', 'bg-green-100 border-green-300', 1, false),
      (create_columns_from_template.board_id, 'Bad', 'bg-red-100 border-red-300', 2, false),
      (create_columns_from_template.board_id, 'Kudos', 'bg-blue-100 border-blue-300', 3, false),
      (create_columns_from_template.board_id, 'Action Items', 'bg-yellow-100 border-yellow-300', 4, true);
  END IF;
END;
$$;


ALTER FUNCTION "public"."create_columns_from_template"("board_id" "uuid", "template_id" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."create_default_columns"("board_id" "uuid") RETURNS "void"
    LANGUAGE "plpgsql"
    AS $$
BEGIN
  INSERT INTO public.retro_columns (board_id, title, color, position) VALUES
    (board_id, 'Good', 'bg-green-100 border-green-300', 1),
    (board_id, 'Bad', 'bg-red-100 border-red-300', 2),
    (board_id, 'Kudos', 'bg-blue-100 border-blue-300', 3),
    (board_id, 'Action Items', 'bg-yellow-100 border-yellow-300', 4);
END;
$$;


ALTER FUNCTION "public"."create_default_columns"("board_id" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."get_current_tenant_id"() RETURNS "uuid"
    LANGUAGE "plpgsql" STABLE
    SET "search_path" TO 'public'
    AS $$
BEGIN
  RETURN COALESCE(
    (current_setting('request.headers')::json->>'x-tenant')::uuid,
    '00000000-0000-0000-0000-000000000000'::uuid  -- Use null UUID for shared tenant
  );
EXCEPTION
  WHEN OTHERS THEN
    RETURN '00000000-0000-0000-0000-000000000000'::uuid;  -- Fallback to null UUID
END;
$$;


ALTER FUNCTION "public"."get_current_tenant_id"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") RETURNS "json"
    LANGUAGE "plpgsql" SECURITY DEFINER
    AS $$
DECLARE
    total_active_users integer;
    ready_users integer;
    result json;
BEGIN
    -- Count active users in the last 5 minutes (from presence or recent activity)
    SELECT COUNT(DISTINCT COALESCE(user_id::text, session_id))
    INTO total_active_users
    FROM public.retro_user_readiness
    WHERE board_id = board_id_param 
    AND current_stage = stage_param
    AND updated_at > now() - interval '5 minutes';
    
    -- If no recent readiness records, we can't determine active users accurately
    -- So return a basic count from the readiness table
    IF total_active_users = 0 THEN
        SELECT COUNT(DISTINCT COALESCE(user_id::text, session_id))
        INTO total_active_users
        FROM public.retro_user_readiness
        WHERE board_id = board_id_param 
        AND current_stage = stage_param;
    END IF;
    
    -- Count ready users
    SELECT COUNT(*)
    INTO ready_users
    FROM public.retro_user_readiness
    WHERE board_id = board_id_param 
    AND current_stage = stage_param
    AND is_ready = true;
    
    -- Build result
    result := json_build_object(
        'total_users', total_active_users,
        'ready_users', ready_users,
        'all_ready', (ready_users > 0 AND ready_users = total_active_users),
        'ready_percentage', CASE 
            WHEN total_active_users > 0 THEN (ready_users::float / total_active_users::float * 100)::int
            ELSE 0
        END
    );
    
    RETURN result;
END;
$$;


ALTER FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") OWNER TO "postgres";


COMMENT ON FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") IS 'Returns readiness statistics for a board and stage';



CREATE OR REPLACE FUNCTION "public"."get_user_email_if_admin"("target_user" "uuid") RETURNS "text"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public', 'auth'
    AS $$
declare
  caller_is_admin boolean;
  result text;
begin
  select exists (
    select 1 from public.profiles p
    where p.id = auth.uid() and p.role = 'admin'
  ) into caller_is_admin;

  if not caller_is_admin then
    raise exception 'forbidden';
  end if;

  select u.email into result
  from auth.users u
  where u.id = target_user;

  return result;
end;
$$;


ALTER FUNCTION "public"."get_user_email_if_admin"("target_user" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."handle_new_board"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
DECLARE
  default_template_id uuid;
BEGIN
  -- Get the default template for the team if it exists
  SELECT id INTO default_template_id
  FROM public.board_templates
  WHERE team_id = NEW.team_id AND is_default = true
  LIMIT 1;
  
  -- Create columns from template or use defaults
  PERFORM create_columns_from_template(NEW.id, default_template_id);
  RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."handle_new_board"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."handle_new_team"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    AS $$
BEGIN
  -- Add creator as team owner
  INSERT INTO public.team_members (team_id, user_id, role) 
  VALUES (NEW.id, NEW.creator_id, 'owner');
  
  -- Create default settings for the team
  INSERT INTO public.team_default_settings (team_id) 
  VALUES (NEW.id);
  
  RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."handle_new_team"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."handle_new_user"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    AS $$
BEGIN
  INSERT INTO public.profiles (id, full_name, avatar_url)
  VALUES (
    NEW.id,
    NEW.raw_user_meta_data->>'full_name',
    NEW.raw_user_meta_data->>'avatar_url'
  );
  RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."handle_new_user"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_team_admin"("team_id" "uuid", "user_id" "uuid") RETURNS boolean
    LANGUAGE "sql" STABLE SECURITY DEFINER
    AS $_$
  SELECT EXISTS (
    SELECT 1 FROM public.team_members 
    WHERE team_members.team_id = $1 
    AND team_members.user_id = $2
    AND team_members.role IN ('owner', 'admin')
  );
$_$;


ALTER FUNCTION "public"."is_team_admin"("team_id" "uuid", "user_id" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_team_admin_or_owner"("team_id" "uuid", "user_id" "uuid") RETURNS boolean
    LANGUAGE "sql" STABLE SECURITY DEFINER
    AS $_$
  SELECT EXISTS (
    SELECT 1 FROM public.team_members 
    WHERE team_members.team_id = $1 
    AND team_members.user_id = $2
    AND team_members.role IN ('owner', 'admin')
  );
$_$;


ALTER FUNCTION "public"."is_team_admin_or_owner"("team_id" "uuid", "user_id" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_team_member"("team_id" "uuid", "user_id" "uuid") RETURNS boolean
    LANGUAGE "sql" STABLE SECURITY DEFINER
    AS $_$
  SELECT EXISTS (
    SELECT 1 FROM public.team_members 
    WHERE team_members.team_id = $1 
    AND team_members.user_id = $2
  );
$_$;


ALTER FUNCTION "public"."is_team_member"("team_id" "uuid", "user_id" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."set_tenant_id"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$
BEGIN
  -- Get tenant_id from request headers (X-Tenant)
  BEGIN
    NEW.tenant_id = COALESCE(
      (current_setting('request.headers')::json->>'x-tenant')::uuid,
      '00000000-0000-0000-0000-000000000000'::uuid
    );
  EXCEPTION
    WHEN OTHERS THEN
      NEW.tenant_id = '00000000-0000-0000-0000-000000000000'::uuid;
  END;
  
  RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."set_tenant_id"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."trigger_set_timestamp"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    SET "search_path" TO ''
    AS $$
BEGIN
      NEW.updated_at = NOW();
      RETURN NEW;
END;
$$;


ALTER FUNCTION "public"."trigger_set_timestamp"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."update_item_vote_count"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    UPDATE public.retro_items 
    SET votes = votes + 1 
    WHERE id = NEW.item_id;
    RETURN NEW;
  ELSIF TG_OP = 'DELETE' THEN
    UPDATE public.retro_items 
    SET votes = votes - 1 
    WHERE id = OLD.item_id;
    RETURN OLD;
  END IF;
  RETURN NULL;
END;
$$;


ALTER FUNCTION "public"."update_item_vote_count"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."update_last_activity"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
begin
  NEW.last_activity_at = now();
  return NEW;
end;
$$;


ALTER FUNCTION "public"."update_last_activity"() OWNER TO "postgres";

SET default_tablespace = '';

SET default_table_access_method = "heap";


CREATE TABLE IF NOT EXISTS "public"."app_config" (
    "key" "text" NOT NULL,
    "value" "text"
);


ALTER TABLE "public"."app_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."board_presence" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid",
    "user_id" "uuid",
    "user_name" "text" NOT NULL,
    "last_seen" timestamp with time zone DEFAULT "now"()
);


ALTER TABLE "public"."board_presence" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."board_templates" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid",
    "name" "text" NOT NULL,
    "is_default" boolean DEFAULT false,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "allow_anonymous" boolean DEFAULT true,
    "voting_enabled" boolean DEFAULT true,
    "max_votes_per_user" integer,
    "show_author_names" boolean DEFAULT true,
    "retro_stages_enabled" boolean DEFAULT false,
    "enforce_stage_readiness" boolean DEFAULT false,
    "allow_self_votes" boolean DEFAULT true,
    "vote_emoji" "text" DEFAULT '👍'::"text",
    "tenant_id" "uuid"
);


ALTER TABLE "public"."board_templates" OWNER TO "postgres";


COMMENT ON COLUMN "public"."board_templates"."retro_stages_enabled" IS 'Whether retro stages feature is enabled for boards created from this template';



CREATE TABLE IF NOT EXISTS "public"."feature_flags" (
    "flag_name" "text" NOT NULL,
    "description" "text",
    "is_enabled" boolean DEFAULT false NOT NULL
);


ALTER TABLE "public"."feature_flags" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."feedback_reports" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "user_id" "uuid",
    "email" "text",
    "type" "text" DEFAULT 'bug'::"text" NOT NULL,
    "title" "text" NOT NULL,
    "description" "text" NOT NULL,
    "page_url" "text",
    "status" "text" DEFAULT 'new'::"text" NOT NULL,
    "github_issue_url" "text"
);


ALTER TABLE "public"."feedback_reports" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."notifications" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "user_id" "uuid" NOT NULL,
    "type" "text" NOT NULL,
    "title" "text" NOT NULL,
    "message" "text",
    "url" "text",
    "is_read" boolean DEFAULT false NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."notifications" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."poker_session_chat" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "session_id" "uuid" NOT NULL,
    "round_number" integer NOT NULL,
    "user_id" "uuid",
    "user_name" "text" NOT NULL,
    "message" "text" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "reply_to_message_id" "uuid"
);


ALTER TABLE "public"."poker_session_chat" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."poker_session_chat_message_reactions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "message_id" "uuid" NOT NULL,
    "user_id" "uuid" NOT NULL,
    "user_name" "text" NOT NULL,
    "emoji" "text" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "session_id" "uuid" NOT NULL
);

ALTER TABLE ONLY "public"."poker_session_chat_message_reactions" REPLICA IDENTITY FULL;


ALTER TABLE "public"."poker_session_chat_message_reactions" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."poker_session_chat_with_details" WITH ("security_invoker"='on') AS
 SELECT "m"."id",
    "m"."session_id",
    "m"."round_number",
    "m"."user_id",
    "m"."user_name",
    "m"."message",
    "m"."created_at",
    "m"."reply_to_message_id",
    "reply_to"."user_name" AS "reply_to_message_user",
    "reply_to"."message" AS "reply_to_message_content",
    COALESCE(( SELECT "json_agg"("json_build_object"('user_id', "r"."user_id", 'user_name', "r"."user_name", 'emoji', "r"."emoji")) AS "json_agg"
           FROM "public"."poker_session_chat_message_reactions" "r"
          WHERE ("r"."message_id" = "m"."id")), '[]'::"json") AS "reactions"
   FROM ("public"."poker_session_chat" "m"
     LEFT JOIN "public"."poker_session_chat" "reply_to" ON (("m"."reply_to_message_id" = "reply_to"."id")))
  ORDER BY "m"."created_at";


ALTER TABLE "public"."poker_session_chat_with_details" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."poker_session_rounds" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "session_id" "uuid" NOT NULL,
    "round_number" integer NOT NULL,
    "selections" "jsonb" DEFAULT '{}'::"jsonb" NOT NULL,
    "average_points" numeric DEFAULT 0 NOT NULL,
    "ticket_number" "text",
    "ticket_title" "text",
    "completed_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "game_state" "text" DEFAULT 'Selection'::"text" NOT NULL,
    "slack_message_ts" "text",
    "slack_channel_id" "text"
);


ALTER TABLE "public"."poker_session_rounds" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."poker_sessions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "room_id" "text",
    "last_activity_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "current_round_number" integer DEFAULT 1 NOT NULL,
    "presence_enabled" boolean DEFAULT true,
    "send_to_slack" boolean DEFAULT false
);


ALTER TABLE "public"."poker_sessions" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."profiles" (
    "id" "uuid" NOT NULL,
    "full_name" "text",
    "avatar_url" "text",
    "created_at" timestamp with time zone DEFAULT "now"(),
    "updated_at" timestamp with time zone DEFAULT "now"(),
    "theme_preference" "text" DEFAULT 'light'::"text",
    "background_preference" "jsonb",
    "role" "text" DEFAULT 'user'::"text" NOT NULL,
    "tenant_id" "uuid",
    CONSTRAINT "check_role" CHECK (("role" = ANY (ARRAY['admin'::"text", 'user'::"text"]))),
    CONSTRAINT "profiles_theme_preference_check" CHECK (("theme_preference" = ANY (ARRAY['light'::"text", 'dark'::"text"])))
);


ALTER TABLE "public"."profiles" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."realtime_demo" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "payload" "text" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."realtime_demo" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."retro_board_config" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid" NOT NULL,
    "allow_anonymous" boolean DEFAULT true,
    "voting_enabled" boolean DEFAULT true,
    "max_votes_per_user" integer,
    "show_author_names" boolean DEFAULT true,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "retro_stages_enabled" boolean DEFAULT false,
    "enforce_stage_readiness" boolean DEFAULT false,
    "allow_self_votes" boolean DEFAULT true,
    "vote_emoji" "text" DEFAULT '👍'::"text"
);

ALTER TABLE ONLY "public"."retro_board_config" REPLICA IDENTITY FULL;


ALTER TABLE "public"."retro_board_config" OWNER TO "postgres";


COMMENT ON COLUMN "public"."retro_board_config"."retro_stages_enabled" IS 'Whether retro stages feature is enabled for this board';



COMMENT ON COLUMN "public"."retro_board_config"."enforce_stage_readiness" IS 'When enabled, users cannot freely navigate between stages - they must wait for all users to be ready before advancing';



CREATE TABLE IF NOT EXISTS "public"."retro_board_sessions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid" NOT NULL,
    "started_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "started_by" "uuid",
    "team_id" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."retro_board_sessions" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."retro_boards" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "room_id" "text" NOT NULL,
    "title" "text" DEFAULT 'Team Retrospective'::"text" NOT NULL,
    "is_private" boolean DEFAULT false,
    "password_hash" "text",
    "creator_id" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"(),
    "updated_at" timestamp with time zone DEFAULT "now"(),
    "team_id" "uuid",
    "archived" boolean DEFAULT false,
    "archived_at" timestamp with time zone,
    "archived_by" "uuid",
    "deleted" boolean DEFAULT false,
    "retro_stage" "text" DEFAULT 'thinking'::"text",
    "tenant_id" "uuid",
    CONSTRAINT "retro_boards_retro_stage_check" CHECK (("retro_stage" = ANY (ARRAY['thinking'::"text", 'voting'::"text", 'discussing'::"text", 'closed'::"text"])))
);


ALTER TABLE "public"."retro_boards" OWNER TO "postgres";


COMMENT ON COLUMN "public"."retro_boards"."retro_stage" IS 'Current stage of the retrospective: thinking, voting, discussing, or closed';



CREATE TABLE IF NOT EXISTS "public"."retro_columns" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid",
    "title" "text" NOT NULL,
    "color" "text" NOT NULL,
    "position" integer NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"(),
    "sort_order" integer DEFAULT 0,
    "is_action_items" boolean DEFAULT false
);


ALTER TABLE "public"."retro_columns" OWNER TO "postgres";


COMMENT ON COLUMN "public"."retro_columns"."is_action_items" IS 'Whether this column is designated as the action items column';



CREATE TABLE IF NOT EXISTS "public"."retro_comments" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "item_id" "uuid" NOT NULL,
    "author" "text" DEFAULT 'Anonymous'::"text" NOT NULL,
    "author_id" "uuid",
    "text" "text" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "session_id" "text"
);

ALTER TABLE ONLY "public"."retro_comments" REPLICA IDENTITY FULL;


ALTER TABLE "public"."retro_comments" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."retro_items" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid",
    "column_id" "uuid",
    "text" "text" NOT NULL,
    "author" "text" DEFAULT 'Anonymous'::"text" NOT NULL,
    "author_id" "uuid",
    "votes" integer DEFAULT 0,
    "created_at" timestamp with time zone DEFAULT "now"(),
    "updated_at" timestamp with time zone DEFAULT "now"(),
    "session_id" "text"
);


ALTER TABLE "public"."retro_items" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."retro_user_readiness" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "board_id" "uuid" NOT NULL,
    "user_id" "uuid",
    "session_id" "text",
    "current_stage" "text" NOT NULL,
    "is_ready" boolean DEFAULT false,
    "created_at" timestamp with time zone DEFAULT "now"(),
    "updated_at" timestamp with time zone DEFAULT "now"(),
    CONSTRAINT "retro_user_readiness_check" CHECK (((("user_id" IS NOT NULL) AND ("session_id" IS NULL)) OR (("user_id" IS NULL) AND ("session_id" IS NOT NULL)))),
    CONSTRAINT "retro_user_readiness_current_stage_check" CHECK (("current_stage" = ANY (ARRAY['thinking'::"text", 'voting'::"text", 'discussing'::"text", 'closed'::"text"])))
);


ALTER TABLE "public"."retro_user_readiness" OWNER TO "postgres";


COMMENT ON TABLE "public"."retro_user_readiness" IS 'Tracks when users are ready to advance to the next retro stage';



CREATE TABLE IF NOT EXISTS "public"."retro_votes" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "item_id" "uuid",
    "user_id" "uuid",
    "session_id" "text",
    "created_at" timestamp with time zone DEFAULT "now"(),
    "board_id" "uuid"
);


ALTER TABLE "public"."retro_votes" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."team_action_items" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid" NOT NULL,
    "text" "text" NOT NULL,
    "source_board_id" "uuid",
    "source_item_id" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "created_by" "uuid",
    "done" boolean DEFAULT false NOT NULL,
    "done_at" timestamp with time zone,
    "done_by" "uuid",
    "assigned_to" "uuid"
);


ALTER TABLE "public"."team_action_items" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."team_default_settings" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid" NOT NULL,
    "allow_anonymous" boolean DEFAULT true,
    "voting_enabled" boolean DEFAULT true,
    "max_votes_per_user" integer,
    "show_author_names" boolean DEFAULT true,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL
);

ALTER TABLE ONLY "public"."team_default_settings" REPLICA IDENTITY FULL;


ALTER TABLE "public"."team_default_settings" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."team_invitations" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid" NOT NULL,
    "email" "text" NOT NULL,
    "invited_by" "uuid" NOT NULL,
    "token" "text" DEFAULT ("gen_random_uuid"())::"text" NOT NULL,
    "status" "text" DEFAULT 'pending'::"text",
    "expires_at" timestamp with time zone DEFAULT ("now"() + '7 days'::interval) NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "invite_type" "text" DEFAULT 'email'::"text",
    "is_active" boolean DEFAULT true,
    CONSTRAINT "team_invitations_invite_type_check" CHECK (("invite_type" = ANY (ARRAY['email'::"text", 'link'::"text"]))),
    CONSTRAINT "team_invitations_status_check" CHECK (("status" = ANY (ARRAY['pending'::"text", 'accepted'::"text", 'declined'::"text"])))
);

ALTER TABLE ONLY "public"."team_invitations" REPLICA IDENTITY FULL;


ALTER TABLE "public"."team_invitations" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."team_members" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "team_id" "uuid" NOT NULL,
    "user_id" "uuid" NOT NULL,
    "role" "text" DEFAULT 'member'::"text",
    "joined_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "team_members_role_check" CHECK (("role" = ANY (ARRAY['owner'::"text", 'admin'::"text", 'member'::"text"])))
);

ALTER TABLE ONLY "public"."team_members" REPLICA IDENTITY FULL;


ALTER TABLE "public"."team_members" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."teams" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "name" "text" NOT NULL,
    "description" "text",
    "creator_id" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "slack_webhook_url" "text",
    "jira_domain" "text",
    "jira_email" "text",
    "jira_api_key" "text",
    "jira_ticket_prefix" "text",
    "slack_bot_token" "text",
    "slack_channel_id" "text",
    "tenant_id" "uuid"
);

ALTER TABLE ONLY "public"."teams" REPLICA IDENTITY FULL;


ALTER TABLE "public"."teams" OWNER TO "postgres";


COMMENT ON COLUMN "public"."teams"."slack_bot_token" IS 'Slack Bot Token for workspace integration.';



COMMENT ON COLUMN "public"."teams"."slack_channel_id" IS 'Slack Channel ID where notifications should be posted.';



CREATE TABLE IF NOT EXISTS "public"."template_columns" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "template_id" "uuid" NOT NULL,
    "title" "text" NOT NULL,
    "color" "text" NOT NULL,
    "position" integer NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "is_action_items" boolean DEFAULT false
);


ALTER TABLE "public"."template_columns" OWNER TO "postgres";


COMMENT ON COLUMN "public"."template_columns"."is_action_items" IS 'Whether this template column is designated as the action items column';



CREATE TABLE IF NOT EXISTS "public"."tenants" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "name" "text" NOT NULL,
    "subdomain" "text" NOT NULL,
    "database_type" "text" DEFAULT 'shared'::"text" NOT NULL,
    "database_config" "jsonb",
    "features" "jsonb" DEFAULT '{}'::"jsonb" NOT NULL,
    "settings" "jsonb" DEFAULT '{}'::"jsonb" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "created_by" "uuid",
    CONSTRAINT "tenants_database_type_check" CHECK (("database_type" = ANY (ARRAY['shared'::"text", 'isolated'::"text", 'custom'::"text"])))
);


ALTER TABLE "public"."tenants" OWNER TO "postgres";


ALTER TABLE ONLY "public"."app_config"
    ADD CONSTRAINT "app_config_pkey" PRIMARY KEY ("key");



ALTER TABLE ONLY "public"."board_presence"
    ADD CONSTRAINT "board_presence_board_id_user_id_key" UNIQUE ("board_id", "user_id");



ALTER TABLE ONLY "public"."board_presence"
    ADD CONSTRAINT "board_presence_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."board_templates"
    ADD CONSTRAINT "board_templates_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."feature_flags"
    ADD CONSTRAINT "feature_flags_pkey" PRIMARY KEY ("flag_name");



ALTER TABLE ONLY "public"."feedback_reports"
    ADD CONSTRAINT "feedback_reports_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."notifications"
    ADD CONSTRAINT "notifications_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."poker_session_chat_message_reactions"
    ADD CONSTRAINT "poker_session_chat_message_reactions_pkey" PRIMARY KEY ("message_id", "user_id", "emoji");



ALTER TABLE ONLY "public"."poker_session_chat"
    ADD CONSTRAINT "poker_session_chat_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."poker_session_rounds"
    ADD CONSTRAINT "poker_session_rounds_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."poker_sessions"
    ADD CONSTRAINT "poker_sessions_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."poker_sessions"
    ADD CONSTRAINT "poker_sessions_room_id_key" UNIQUE ("room_id");



ALTER TABLE ONLY "public"."poker_sessions"
    ADD CONSTRAINT "poker_sessions_team_id_key" UNIQUE ("team_id");



ALTER TABLE ONLY "public"."profiles"
    ADD CONSTRAINT "profiles_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."realtime_demo"
    ADD CONSTRAINT "realtime_demo_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_board_config"
    ADD CONSTRAINT "retro_board_config_board_id_key" UNIQUE ("board_id");



ALTER TABLE ONLY "public"."retro_board_config"
    ADD CONSTRAINT "retro_board_config_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_board_sessions"
    ADD CONSTRAINT "retro_board_sessions_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_room_id_key" UNIQUE ("room_id");



ALTER TABLE ONLY "public"."retro_columns"
    ADD CONSTRAINT "retro_columns_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_comments"
    ADD CONSTRAINT "retro_comments_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_items"
    ADD CONSTRAINT "retro_items_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_user_readiness"
    ADD CONSTRAINT "retro_user_readiness_board_id_session_id_current_stage_key" UNIQUE ("board_id", "session_id", "current_stage");



ALTER TABLE ONLY "public"."retro_user_readiness"
    ADD CONSTRAINT "retro_user_readiness_board_id_user_id_current_stage_key" UNIQUE ("board_id", "user_id", "current_stage");



ALTER TABLE ONLY "public"."retro_user_readiness"
    ADD CONSTRAINT "retro_user_readiness_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_item_id_session_id_key" UNIQUE ("item_id", "session_id");



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_item_id_user_id_key" UNIQUE ("item_id", "user_id");



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."team_action_items"
    ADD CONSTRAINT "team_action_items_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."team_default_settings"
    ADD CONSTRAINT "team_default_settings_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."team_default_settings"
    ADD CONSTRAINT "team_default_settings_team_id_key" UNIQUE ("team_id");



ALTER TABLE ONLY "public"."team_invitations"
    ADD CONSTRAINT "team_invitations_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."team_invitations"
    ADD CONSTRAINT "team_invitations_token_key" UNIQUE ("token");



ALTER TABLE ONLY "public"."team_members"
    ADD CONSTRAINT "team_members_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."team_members"
    ADD CONSTRAINT "team_members_team_id_user_id_key" UNIQUE ("team_id", "user_id");



ALTER TABLE ONLY "public"."teams"
    ADD CONSTRAINT "teams_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."template_columns"
    ADD CONSTRAINT "template_columns_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."tenants"
    ADD CONSTRAINT "tenants_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."tenants"
    ADD CONSTRAINT "tenants_subdomain_key" UNIQUE ("subdomain");



CREATE INDEX "idx_board_templates_tenant_id" ON "public"."board_templates" USING "btree" ("tenant_id");



CREATE INDEX "idx_poker_session_chat_created_at" ON "public"."poker_session_chat" USING "btree" ("created_at");



CREATE INDEX "idx_poker_session_chat_session_round" ON "public"."poker_session_chat" USING "btree" ("session_id", "round_number");



CREATE INDEX "idx_poker_session_rounds_round_number" ON "public"."poker_session_rounds" USING "btree" ("session_id", "round_number");



CREATE INDEX "idx_poker_session_rounds_session_id" ON "public"."poker_session_rounds" USING "btree" ("session_id");



CREATE INDEX "idx_profiles_tenant_id" ON "public"."profiles" USING "btree" ("tenant_id");



CREATE INDEX "idx_retro_boards_tenant_id" ON "public"."retro_boards" USING "btree" ("tenant_id");



CREATE UNIQUE INDEX "idx_retro_columns_action_items_per_board" ON "public"."retro_columns" USING "btree" ("board_id") WHERE ("is_action_items" = true);



CREATE INDEX "idx_retro_user_readiness_board_stage" ON "public"."retro_user_readiness" USING "btree" ("board_id", "current_stage");



CREATE INDEX "idx_retro_user_readiness_session" ON "public"."retro_user_readiness" USING "btree" ("session_id");



CREATE INDEX "idx_retro_user_readiness_user" ON "public"."retro_user_readiness" USING "btree" ("user_id");



CREATE INDEX "idx_teams_slack_channel_id" ON "public"."teams" USING "btree" ("slack_channel_id") WHERE ("slack_channel_id" IS NOT NULL);



CREATE INDEX "idx_teams_tenant_id" ON "public"."teams" USING "btree" ("tenant_id");



CREATE UNIQUE INDEX "idx_template_columns_action_items_per_template" ON "public"."template_columns" USING "btree" ("template_id") WHERE ("is_action_items" = true);



CREATE INDEX "idx_tenants_database_type" ON "public"."tenants" USING "btree" ("database_type");



CREATE INDEX "idx_tenants_subdomain" ON "public"."tenants" USING "btree" ("subdomain");



CREATE INDEX "team_action_items_assigned_to_idx" ON "public"."team_action_items" USING "btree" ("assigned_to");



CREATE INDEX "team_action_items_created_at_idx" ON "public"."team_action_items" USING "btree" ("created_at" DESC);



CREATE UNIQUE INDEX "team_action_items_source_item_id_unique" ON "public"."team_action_items" USING "btree" ("source_item_id") WHERE ("source_item_id" IS NOT NULL);



CREATE INDEX "team_action_items_team_id_done_idx" ON "public"."team_action_items" USING "btree" ("team_id", "done");



CREATE OR REPLACE TRIGGER "cleanup_readiness_on_stage_change" AFTER UPDATE OF "retro_stage" ON "public"."retro_boards" FOR EACH ROW EXECUTE FUNCTION "public"."cleanup_previous_stage_readiness"();



CREATE OR REPLACE TRIGGER "on_board_created" AFTER INSERT ON "public"."retro_boards" FOR EACH ROW EXECUTE FUNCTION "public"."handle_new_board"();



CREATE OR REPLACE TRIGGER "on_poker_session_update" BEFORE UPDATE ON "public"."poker_sessions" FOR EACH ROW EXECUTE FUNCTION "public"."update_last_activity"();



CREATE OR REPLACE TRIGGER "on_team_created" AFTER INSERT ON "public"."teams" FOR EACH ROW EXECUTE FUNCTION "public"."handle_new_team"();



CREATE OR REPLACE TRIGGER "on_vote_change" AFTER INSERT OR DELETE ON "public"."retro_votes" FOR EACH ROW EXECUTE FUNCTION "public"."update_item_vote_count"();



CREATE OR REPLACE TRIGGER "set_tenant_id_board_templates" BEFORE INSERT ON "public"."board_templates" FOR EACH ROW EXECUTE FUNCTION "public"."set_tenant_id"();



CREATE OR REPLACE TRIGGER "set_tenant_id_profiles" BEFORE INSERT ON "public"."profiles" FOR EACH ROW EXECUTE FUNCTION "public"."set_tenant_id"();



CREATE OR REPLACE TRIGGER "set_tenant_id_retro_boards" BEFORE INSERT ON "public"."retro_boards" FOR EACH ROW EXECUTE FUNCTION "public"."set_tenant_id"();



CREATE OR REPLACE TRIGGER "set_tenant_id_teams" BEFORE INSERT ON "public"."teams" FOR EACH ROW EXECUTE FUNCTION "public"."set_tenant_id"();



CREATE OR REPLACE TRIGGER "set_timestamp" BEFORE UPDATE ON "public"."poker_sessions" FOR EACH ROW EXECUTE FUNCTION "public"."trigger_set_timestamp"();



ALTER TABLE ONLY "public"."board_presence"
    ADD CONSTRAINT "board_presence_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."board_presence"
    ADD CONSTRAINT "board_presence_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "auth"."users"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."board_templates"
    ADD CONSTRAINT "board_templates_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."board_templates"
    ADD CONSTRAINT "board_templates_tenant_id_fkey" FOREIGN KEY ("tenant_id") REFERENCES "public"."tenants"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_session_chat_message_reactions"
    ADD CONSTRAINT "fk_session" FOREIGN KEY ("session_id") REFERENCES "public"."poker_sessions"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."notifications"
    ADD CONSTRAINT "notifications_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."profiles"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_session_chat_message_reactions"
    ADD CONSTRAINT "poker_session_chat_message_reactions_message_id_fkey" FOREIGN KEY ("message_id") REFERENCES "public"."poker_session_chat"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_session_chat_message_reactions"
    ADD CONSTRAINT "poker_session_chat_message_reactions_session_id_fkey" FOREIGN KEY ("session_id") REFERENCES "public"."poker_sessions"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_session_chat"
    ADD CONSTRAINT "poker_session_chat_reply_to_message_id_fkey" FOREIGN KEY ("reply_to_message_id") REFERENCES "public"."poker_session_chat"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."poker_session_chat"
    ADD CONSTRAINT "poker_session_chat_session_id_fkey" FOREIGN KEY ("session_id") REFERENCES "public"."poker_sessions"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_session_chat"
    ADD CONSTRAINT "poker_session_chat_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "auth"."users"("id");



ALTER TABLE ONLY "public"."poker_session_rounds"
    ADD CONSTRAINT "poker_session_rounds_session_id_fkey" FOREIGN KEY ("session_id") REFERENCES "public"."poker_sessions"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."poker_sessions"
    ADD CONSTRAINT "poker_sessions_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."profiles"
    ADD CONSTRAINT "profiles_id_fkey" FOREIGN KEY ("id") REFERENCES "auth"."users"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."profiles"
    ADD CONSTRAINT "profiles_tenant_id_fkey" FOREIGN KEY ("tenant_id") REFERENCES "public"."tenants"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_board_config"
    ADD CONSTRAINT "retro_board_config_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_board_sessions"
    ADD CONSTRAINT "retro_board_sessions_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_board_sessions"
    ADD CONSTRAINT "retro_board_sessions_started_by_fkey" FOREIGN KEY ("started_by") REFERENCES "auth"."users"("id");



ALTER TABLE ONLY "public"."retro_board_sessions"
    ADD CONSTRAINT "retro_board_sessions_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id");



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_archived_by_fkey" FOREIGN KEY ("archived_by") REFERENCES "auth"."users"("id");



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_creator_id_fkey" FOREIGN KEY ("creator_id") REFERENCES "auth"."users"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."retro_boards"
    ADD CONSTRAINT "retro_boards_tenant_id_fkey" FOREIGN KEY ("tenant_id") REFERENCES "public"."tenants"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_columns"
    ADD CONSTRAINT "retro_columns_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_comments"
    ADD CONSTRAINT "retro_comments_author_id_fkey" FOREIGN KEY ("author_id") REFERENCES "public"."profiles"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."retro_comments"
    ADD CONSTRAINT "retro_comments_item_id_fkey" FOREIGN KEY ("item_id") REFERENCES "public"."retro_items"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_items"
    ADD CONSTRAINT "retro_items_author_id_fkey" FOREIGN KEY ("author_id") REFERENCES "public"."profiles"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."retro_items"
    ADD CONSTRAINT "retro_items_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_items"
    ADD CONSTRAINT "retro_items_column_id_fkey" FOREIGN KEY ("column_id") REFERENCES "public"."retro_columns"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_user_readiness"
    ADD CONSTRAINT "retro_user_readiness_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_user_readiness"
    ADD CONSTRAINT "retro_user_readiness_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "auth"."users"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_board_id_fkey" FOREIGN KEY ("board_id") REFERENCES "public"."retro_boards"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_item_id_fkey" FOREIGN KEY ("item_id") REFERENCES "public"."retro_items"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."retro_votes"
    ADD CONSTRAINT "retro_votes_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "auth"."users"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."team_action_items"
    ADD CONSTRAINT "team_action_items_assigned_to_fkey" FOREIGN KEY ("assigned_to") REFERENCES "public"."profiles"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."team_action_items"
    ADD CONSTRAINT "team_action_items_source_board_id_fkey" FOREIGN KEY ("source_board_id") REFERENCES "public"."retro_boards"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."team_action_items"
    ADD CONSTRAINT "team_action_items_source_item_id_fkey" FOREIGN KEY ("source_item_id") REFERENCES "public"."retro_items"("id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."team_action_items"
    ADD CONSTRAINT "team_action_items_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."team_default_settings"
    ADD CONSTRAINT "team_default_settings_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."team_invitations"
    ADD CONSTRAINT "team_invitations_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."team_members"
    ADD CONSTRAINT "team_members_team_id_fkey" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."team_members"
    ADD CONSTRAINT "team_members_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."profiles"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."teams"
    ADD CONSTRAINT "teams_tenant_id_fkey" FOREIGN KEY ("tenant_id") REFERENCES "public"."tenants"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."template_columns"
    ADD CONSTRAINT "template_columns_template_id_fkey" FOREIGN KEY ("template_id") REFERENCES "public"."board_templates"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."tenants"
    ADD CONSTRAINT "tenants_created_by_fkey" FOREIGN KEY ("created_by") REFERENCES "public"."profiles"("id") ON DELETE SET NULL;



CREATE POLICY "Admins can insert notifications" ON "public"."notifications" FOR INSERT TO "authenticated" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))));



CREATE POLICY "Admins can manage all tenants" ON "public"."tenants" USING ((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))));



CREATE POLICY "Admins can view all tenants" ON "public"."tenants" FOR SELECT USING ((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))));



CREATE POLICY "Allow admins to insert app config" ON "public"."app_config" FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))));



CREATE POLICY "Allow admins to update app config" ON "public"."app_config" FOR UPDATE USING ((( SELECT "auth"."uid"() AS "uid") IN ( SELECT "profiles"."id"
   FROM "public"."profiles"
  WHERE ("profiles"."role" = 'admin'::"text"))));



CREATE POLICY "Allow all users to read" ON "public"."app_config" FOR SELECT USING (true);



CREATE POLICY "Allow authenticated users to read chat messages" ON "public"."poker_session_chat" FOR SELECT TO "authenticated" USING (true);



CREATE POLICY "Allow delete access for user who created the reaction" ON "public"."poker_session_chat_message_reactions" FOR DELETE USING ((("auth"."uid"() = "user_id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Allow insert access for authenticated users" ON "public"."poker_session_chat_message_reactions" FOR INSERT WITH CHECK (((("auth"."uid"() = "user_id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))) AND ("session_id" IS NOT NULL)));



CREATE POLICY "Allow members to create a session for their team" ON "public"."poker_sessions" FOR INSERT WITH CHECK (("team_id" IN ( SELECT "team_members"."team_id"
   FROM "public"."team_members"
  WHERE ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))));



CREATE POLICY "Allow members to update their team's session" ON "public"."poker_sessions" FOR UPDATE USING (("team_id" IN ( SELECT "team_members"."team_id"
   FROM "public"."team_members"
  WHERE ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))));



CREATE POLICY "Allow members to view their team's session" ON "public"."poker_sessions" FOR SELECT USING (("team_id" IN ( SELECT "team_members"."team_id"
   FROM "public"."team_members"
  WHERE ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))));



CREATE POLICY "Allow public insert access" ON "public"."poker_sessions" FOR INSERT WITH CHECK (true);



CREATE POLICY "Allow public read access" ON "public"."poker_sessions" FOR SELECT USING (true);



CREATE POLICY "Allow read access for all session participants" ON "public"."poker_session_rounds" FOR SELECT USING (((NOT (EXISTS ( SELECT 1
   FROM ("public"."poker_sessions" "ps"
     JOIN "public"."teams" "t" ON (("ps"."room_id" = ("t"."id")::"text")))
  WHERE ("ps"."id" = "poker_session_rounds"."session_id")))) OR (EXISTS ( SELECT 1
   FROM ("public"."poker_sessions" "ps"
     JOIN "public"."team_members" "tm" ON (("ps"."room_id" = ("tm"."team_id")::"text")))
  WHERE (("ps"."id" = "poker_session_rounds"."session_id") AND ("tm"."user_id" = "auth"."uid"()))))));



CREATE POLICY "Allow read access to authenticated users" ON "public"."poker_session_chat_message_reactions" FOR SELECT USING (("auth"."role"() = 'authenticated'::"text"));



CREATE POLICY "Allow team owners to read jira credentials" ON "public"."teams" FOR SELECT TO "authenticated" USING (((( SELECT "team_members"."role"
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_members"."id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))) = 'owner'::"text") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Allow team owners to update jira credentials" ON "public"."teams" FOR UPDATE TO "authenticated" USING (((( SELECT "team_members"."role"
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_members"."id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))) = 'owner'::"text") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))) WITH CHECK (((( SELECT "team_members"."role"
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_members"."id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))) = 'owner'::"text") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Allow update access for user who created the reaction" ON "public"."poker_session_chat_message_reactions" FOR UPDATE USING ((("auth"."uid"() = "user_id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))) WITH CHECK ((("auth"."uid"() = "user_id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Allow update for all session participants" ON "public"."poker_session_rounds" FOR UPDATE USING (((NOT (EXISTS ( SELECT 1
   FROM ("public"."poker_sessions" "ps"
     JOIN "public"."teams" "t" ON (("ps"."room_id" = ("t"."id")::"text")))
  WHERE ("ps"."id" = "poker_session_rounds"."session_id")))) OR (EXISTS ( SELECT 1
   FROM ("public"."poker_sessions" "ps"
     JOIN "public"."team_members" "tm" ON (("ps"."room_id" = ("tm"."team_id")::"text")))
  WHERE (("ps"."id" = "poker_session_rounds"."session_id") AND ("tm"."user_id" = "auth"."uid"()))))));



CREATE POLICY "Allow users to send messages as themselves" ON "public"."poker_session_chat" FOR INSERT TO "authenticated" WITH CHECK ((("auth"."uid"() = "user_id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Allow users to update their own selections" ON "public"."poker_sessions" FOR UPDATE USING (true) WITH CHECK (true);



CREATE POLICY "Anyone can create poker session chat messages" ON "public"."poker_session_chat" FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can create retro boards" ON "public"."retro_boards" FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can create retro columns" ON "public"."retro_columns" FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can create retro items" ON "public"."retro_items" FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can create retro votes" ON "public"."retro_votes" FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can delete retro columns" ON "public"."retro_columns" FOR DELETE USING (true);



CREATE POLICY "Anyone can delete retro items" ON "public"."retro_items" FOR DELETE USING (true);



CREATE POLICY "Anyone can delete retro votes" ON "public"."retro_votes" FOR DELETE USING (true);



CREATE POLICY "Anyone can update retro columns" ON "public"."retro_columns" FOR UPDATE USING (true);



CREATE POLICY "Anyone can update retro items" ON "public"."retro_items" FOR UPDATE USING (true);



CREATE POLICY "Anyone can view board config" ON "public"."retro_board_config" FOR SELECT USING (true);



CREATE POLICY "Anyone can view board presence" ON "public"."board_presence" FOR SELECT USING (true);



CREATE POLICY "Anyone can view comments" ON "public"."retro_comments" FOR SELECT USING (true);



CREATE POLICY "Anyone can view poker session chat" ON "public"."poker_session_chat" FOR SELECT USING (true);



CREATE POLICY "Anyone can view poker session rounds" ON "public"."poker_session_rounds" FOR SELECT USING (true);



CREATE POLICY "Anyone can view retro boards" ON "public"."retro_boards" FOR SELECT USING (true);



CREATE POLICY "Anyone can view retro columns" ON "public"."retro_columns" FOR SELECT USING (true);



CREATE POLICY "Anyone can view retro items" ON "public"."retro_items" FOR SELECT USING (true);



CREATE POLICY "Anyone can view retro votes" ON "public"."retro_votes" FOR SELECT USING (true);



CREATE POLICY "Authenticated users can create poker session rounds" ON "public"."poker_session_rounds" FOR INSERT WITH CHECK (true);



CREATE POLICY "Authenticated users can create teams" ON "public"."teams" FOR INSERT WITH CHECK (((("auth"."uid"() IS NOT NULL) AND ("creator_id" = "auth"."uid"())) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Board creators can update their boards" ON "public"."retro_boards" FOR UPDATE USING ((("creator_id" = ( SELECT "auth"."uid"() AS "uid")) OR ("creator_id" IS NULL) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "DEBUG: Allow authenticated users to read team members" ON "public"."team_members" FOR SELECT TO "authenticated" USING (true);



CREATE POLICY "DEBUG: Users can view all tables" ON "public"."teams" FOR SELECT USING ((EXISTS ( SELECT 1
   FROM "public"."team_members" "tm"
  WHERE (("tm"."team_id" = "teams"."id") AND ("tm"."user_id" = "auth"."uid"())))));



CREATE POLICY "Enable read access for all users" ON "public"."feature_flags" FOR SELECT USING (true);



CREATE POLICY "Enable update access for admins" ON "public"."feature_flags" FOR UPDATE USING ((( SELECT "auth"."uid"() AS "uid") IN ( SELECT "profiles"."id"
   FROM "public"."profiles"
  WHERE ("profiles"."role" = 'admin'::"text"))));



CREATE POLICY "Only team admins can archive boards" ON "public"."retro_boards" FOR UPDATE USING (
CASE
    WHEN ("team_id" IS NOT NULL) THEN ("public"."is_team_admin_or_owner"("team_id", "auth"."uid"()) OR (EXISTS ( SELECT 1
       FROM "public"."profiles" "p"
      WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))
    ELSE ("creator_id" = ( SELECT "auth"."uid"() AS "uid"))
END);



CREATE POLICY "Only team admins can delete boards" ON "public"."retro_boards" FOR DELETE USING (
CASE
    WHEN ("team_id" IS NOT NULL) THEN ("public"."is_team_admin_or_owner"("team_id", "auth"."uid"()) OR (EXISTS ( SELECT 1
       FROM "public"."profiles" "p"
      WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))
    ELSE ("creator_id" = ( SELECT "auth"."uid"() AS "uid"))
END);



CREATE POLICY "Public profiles are viewable by everyone" ON "public"."profiles" FOR SELECT USING (true);



CREATE POLICY "Session members can delete chat messages" ON "public"."poker_session_chat" FOR DELETE USING (((( SELECT "poker_sessions"."team_id"
   FROM "public"."poker_sessions"
  WHERE ("poker_sessions"."id" = "poker_session_chat"."session_id")) IS NULL) OR ("auth"."uid"() IN ( SELECT "tm"."user_id"
   FROM ("public"."team_members" "tm"
     JOIN "public"."poker_sessions" "ps" ON (("tm"."team_id" = "ps"."team_id")))
  WHERE ("ps"."id" = "poker_session_chat"."session_id")))));



CREATE POLICY "Session members can delete chat reactions" ON "public"."poker_session_chat_message_reactions" FOR DELETE USING (((( SELECT "poker_sessions"."team_id"
   FROM "public"."poker_sessions"
  WHERE ("poker_sessions"."id" = "poker_session_chat_message_reactions"."session_id")) IS NULL) OR ("auth"."uid"() IN ( SELECT "tm"."user_id"
   FROM ("public"."team_members" "tm"
     JOIN "public"."poker_sessions" "ps" ON (("tm"."team_id" = "ps"."team_id")))
  WHERE ("ps"."id" = "poker_session_chat_message_reactions"."session_id")))));



CREATE POLICY "Team admins can create invitations" ON "public"."team_invitations" FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_invitations"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")) AND ("team_members"."role" = ANY (ARRAY['owner'::"text", 'admin'::"text"]))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can delete action items" ON "public"."team_action_items" FOR DELETE USING (("public"."is_team_admin_or_owner"("team_id", "auth"."uid"()) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can delete invitations" ON "public"."team_invitations" FOR DELETE USING (((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_invitations"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")) AND ("team_members"."role" = ANY (ARRAY['owner'::"text", 'admin'::"text"]))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can delete members" ON "public"."team_members" FOR DELETE USING (("public"."is_team_admin_or_owner"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can insert members" ON "public"."team_members" FOR INSERT WITH CHECK (("public"."is_team_admin_or_owner"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can manage membership" ON "public"."team_members" USING (("public"."is_team_admin"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can manage template columns" ON "public"."template_columns" USING (((EXISTS ( SELECT 1
   FROM "public"."board_templates" "bt"
  WHERE (("bt"."id" = "template_columns"."template_id") AND "public"."is_team_admin_or_owner"("bt"."team_id", ( SELECT "auth"."uid"() AS "uid"))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can manage templates" ON "public"."board_templates" TO "authenticated" USING ("public"."is_team_admin_or_owner"("team_id", ( SELECT "auth"."uid"() AS "uid")));



CREATE POLICY "Team admins can update invitations" ON "public"."team_invitations" FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_invitations"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")) AND ("team_members"."role" = ANY (ARRAY['owner'::"text", 'admin'::"text"]))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can update members" ON "public"."team_members" FOR UPDATE USING (("public"."is_team_admin_or_owner"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team admins can view team invitations" ON "public"."team_invitations" FOR SELECT USING (("public"."is_team_admin_or_owner"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team creators can update teams" ON "public"."teams" FOR UPDATE USING ((("tenant_id" = "public"."get_current_tenant_id"()) AND ("creator_id" = "auth"."uid"())));



CREATE POLICY "Team members can create action items for their teams" ON "public"."team_action_items" FOR INSERT WITH CHECK ((("team_id" IN ( SELECT "tm"."team_id"
   FROM "public"."team_members" "tm"
  WHERE ("tm"."user_id" = "auth"."uid"()))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can manage their team's poker sessions" ON "public"."poker_sessions" TO "authenticated" USING ((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "poker_sessions"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "poker_sessions"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid"))))));



CREATE POLICY "Team members can view default settings" ON "public"."team_default_settings" FOR SELECT USING (("public"."is_team_member"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view invitations for their teams" ON "public"."team_invitations" FOR SELECT USING (("public"."is_team_member"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view team action items" ON "public"."team_action_items" FOR SELECT USING ((("team_id" IN ( SELECT "tm"."team_id"
   FROM "public"."team_members" "tm"
  WHERE ("tm"."user_id" = "auth"."uid"()))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view team invitations" ON "public"."team_invitations" FOR SELECT USING (((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "team_invitations"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid")) AND ("team_members"."role" = ANY (ARRAY['owner'::"text", 'admin'::"text"]))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view team members" ON "public"."team_members" FOR SELECT USING (("public"."is_team_member"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view team templates" ON "public"."board_templates" FOR SELECT TO "authenticated" USING ((EXISTS ( SELECT 1
   FROM "public"."team_members"
  WHERE (("team_members"."team_id" = "board_templates"."team_id") AND ("team_members"."user_id" = ( SELECT "auth"."uid"() AS "uid"))))));



CREATE POLICY "Team members can view template columns" ON "public"."template_columns" FOR SELECT USING (((EXISTS ( SELECT 1
   FROM ("public"."board_templates" "bt"
     JOIN "public"."team_members" "tm" ON (("bt"."team_id" = "tm"."team_id")))
  WHERE (("bt"."id" = "template_columns"."template_id") AND ("tm"."user_id" = ( SELECT "auth"."uid"() AS "uid"))))) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team members can view their teams" ON "public"."teams" FOR SELECT USING (("public"."is_team_member"("id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team owners and admins can manage default settings" ON "public"."team_default_settings" USING (("public"."is_team_admin"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team owners and admins can manage invitations" ON "public"."team_invitations" USING (("public"."is_team_admin"("team_id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team owners and admins can update teams" ON "public"."teams" FOR UPDATE USING (("public"."is_team_admin"("id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Team owners can delete teams" ON "public"."teams" FOR DELETE USING (("public"."is_team_admin"("id", ( SELECT "auth"."uid"() AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Tenant creators can manage their tenants" ON "public"."tenants" USING (("created_by" = "auth"."uid"())) WITH CHECK (("created_by" = "auth"."uid"()));



CREATE POLICY "Users and admins can update notifications" ON "public"."notifications" FOR UPDATE TO "authenticated" USING ((("user_id" = "auth"."uid"()) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))) WITH CHECK ((("user_id" = "auth"."uid"()) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can create board config" ON "public"."retro_board_config" FOR INSERT WITH CHECK (true);



CREATE POLICY "Users can create comments" ON "public"."retro_comments" FOR INSERT WITH CHECK (true);



CREATE POLICY "Users can create sessions for their team boards" ON "public"."retro_board_sessions" FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))) OR ("team_id" IN ( SELECT "tm"."team_id"
   FROM "public"."team_members" "tm"
  WHERE ("tm"."user_id" = ( SELECT "auth"."uid"() AS "uid")))) OR ("board_id" IN ( SELECT "rb"."id"
   FROM "public"."retro_boards" "rb"
  WHERE ("rb"."creator_id" = ( SELECT "auth"."uid"() AS "uid"))))));



CREATE POLICY "Users can delete board config" ON "public"."retro_board_config" FOR DELETE USING (true);



CREATE POLICY "Users can delete their own comments" ON "public"."retro_comments" FOR DELETE USING (((( SELECT "auth"."uid"() AS "uid") = "author_id") OR ("author_id" IS NULL) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can delete their own presence" ON "public"."board_presence" FOR DELETE USING (true);



CREATE POLICY "Users can insert their own presence" ON "public"."board_presence" FOR INSERT WITH CHECK (true);



CREATE POLICY "Users can insert their own profile" ON "public"."profiles" FOR INSERT WITH CHECK (((( SELECT "auth"."uid"() AS "uid") = "id") OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can manage their own readiness" ON "public"."retro_user_readiness" USING (((("auth"."uid"() IS NOT NULL) AND ("user_id" = "auth"."uid"())) OR (("auth"."uid"() IS NULL) AND ("session_id" IS NOT NULL)) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))) WITH CHECK (((("auth"."uid"() IS NOT NULL) AND ("user_id" = "auth"."uid"())) OR (("auth"."uid"() IS NULL) AND ("session_id" IS NOT NULL)) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can read own notifications" ON "public"."notifications" FOR SELECT TO "authenticated" USING (("user_id" = "auth"."uid"()));



CREATE POLICY "Users can update action items assigned to them or team admins c" ON "public"."team_action_items" FOR UPDATE USING ((("assigned_to" = "auth"."uid"()) OR "public"."is_team_admin_or_owner"("team_id", "auth"."uid"()) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text")))))) WITH CHECK ((("assigned_to" = "auth"."uid"()) OR "public"."is_team_admin_or_owner"("team_id", "auth"."uid"()) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can update board config" ON "public"."retro_board_config" FOR UPDATE USING (true);



CREATE POLICY "Users can update their own comments" ON "public"."retro_comments" FOR UPDATE USING (((( SELECT "auth"."uid"() AS "uid") = "author_id") OR ("author_id" IS NULL) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can update their own presence" ON "public"."board_presence" FOR UPDATE USING (true);



CREATE POLICY "Users can update their own profile" ON "public"."profiles" FOR UPDATE USING ((("tenant_id" = "public"."get_current_tenant_id"()) AND ("id" = "auth"."uid"())));



CREATE POLICY "Users can view boards they have access to" ON "public"."retro_boards" FOR SELECT USING ((("tenant_id" = "public"."get_current_tenant_id"()) AND (("is_private" = false) OR (EXISTS ( SELECT 1
   FROM "public"."team_members" "tm"
  WHERE (("tm"."team_id" = "retro_boards"."team_id") AND ("tm"."user_id" = "auth"."uid"())))))));



CREATE POLICY "Users can view readiness for boards they can access" ON "public"."retro_user_readiness" FOR SELECT USING ((EXISTS ( SELECT 1
   FROM "public"."retro_boards" "rb"
  WHERE (("rb"."id" = "retro_user_readiness"."board_id") AND (("rb"."is_private" = false) OR ("rb"."creator_id" = "auth"."uid"()) OR (EXISTS ( SELECT 1
           FROM "public"."team_members" "tm"
          WHERE (("tm"."team_id" = "rb"."team_id") AND ("tm"."user_id" = "auth"."uid"())))))))));



CREATE POLICY "Users can view sessions for their team boards" ON "public"."retro_board_sessions" FOR SELECT USING ((("team_id" IN ( SELECT "tm"."team_id"
   FROM "public"."team_members" "tm"
  WHERE ("tm"."user_id" = ( SELECT "auth"."uid"() AS "uid")))) OR ("board_id" IN ( SELECT "rb"."id"
   FROM "public"."retro_boards" "rb"
  WHERE ("rb"."creator_id" = ( SELECT "auth"."uid"() AS "uid"))))));



CREATE POLICY "Users can view teams they are members of" ON "public"."teams" FOR SELECT USING ((("tenant_id" = "public"."get_current_tenant_id"()) AND (EXISTS ( SELECT 1
   FROM "public"."team_members" "tm"
  WHERE (("tm"."team_id" = "teams"."id") AND ("tm"."user_id" = "auth"."uid"()))))));



CREATE POLICY "Users can view their own profile" ON "public"."profiles" FOR SELECT USING ((("tenant_id" = "public"."get_current_tenant_id"()) AND ("id" = "auth"."uid"())));



CREATE POLICY "Users can view their own team memberships" ON "public"."team_members" FOR SELECT USING ((("user_id" = ( SELECT ( SELECT "auth"."uid"() AS "uid") AS "uid")) OR (EXISTS ( SELECT 1
   FROM "public"."profiles" "p"
  WHERE (("p"."id" = "auth"."uid"()) AND ("p"."role" = 'admin'::"text"))))));



CREATE POLICY "Users can view their own tenant" ON "public"."tenants" FOR SELECT USING (("id" IN ( SELECT "profiles"."tenant_id"
   FROM "public"."profiles"
  WHERE ("profiles"."id" = "auth"."uid"()))));



ALTER TABLE "public"."app_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."board_presence" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."board_templates" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."feature_flags" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "feedback_insert_anyone" ON "public"."feedback_reports" FOR INSERT WITH CHECK (true);



ALTER TABLE "public"."feedback_reports" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "feedback_select_own" ON "public"."feedback_reports" FOR SELECT TO "authenticated" USING (("user_id" = "auth"."uid"()));



ALTER TABLE "public"."notifications" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."poker_session_chat" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."poker_session_chat_message_reactions" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."poker_session_rounds" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."poker_sessions" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."profiles" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_board_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_board_sessions" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_boards" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_columns" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_comments" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_items" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_user_readiness" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."retro_votes" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."team_action_items" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."team_default_settings" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."team_invitations" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."team_members" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."teams" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."template_columns" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."tenants" ENABLE ROW LEVEL SECURITY;

-- Create supabase_realtime publication for realtime subscriptions
CREATE PUBLICATION supabase_realtime;

ALTER PUBLICATION "supabase_realtime" OWNER TO "postgres";






ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."board_presence";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."notifications";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."poker_session_chat";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."poker_session_chat_message_reactions";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."profiles";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."realtime_demo";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_board_config";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_boards";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_columns";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_comments";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_items";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."retro_votes";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."team_action_items";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."team_default_settings";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."team_invitations";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."team_members";



ALTER PUBLICATION "supabase_realtime" ADD TABLE ONLY "public"."teams";






GRANT USAGE ON SCHEMA "public" TO "postgres";
GRANT USAGE ON SCHEMA "public" TO "anon";
GRANT USAGE ON SCHEMA "public" TO "authenticated";
GRANT USAGE ON SCHEMA "public" TO "service_role";



































































































































































































































































































GRANT ALL ON FUNCTION "public"."accept_team_invitation"("invitation_token" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."accept_team_invitation"("invitation_token" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."accept_team_invitation"("invitation_token" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."cleanup_previous_stage_readiness"() TO "anon";
GRANT ALL ON FUNCTION "public"."cleanup_previous_stage_readiness"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."cleanup_previous_stage_readiness"() TO "service_role";



GRANT ALL ON FUNCTION "public"."cleanup_stale_poker_sessions"() TO "anon";
GRANT ALL ON FUNCTION "public"."cleanup_stale_poker_sessions"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."cleanup_stale_poker_sessions"() TO "service_role";



GRANT ALL ON FUNCTION "public"."create_columns_from_template"("board_id" "uuid", "template_id" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."create_columns_from_template"("board_id" "uuid", "template_id" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."create_columns_from_template"("board_id" "uuid", "template_id" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."create_default_columns"("board_id" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."create_default_columns"("board_id" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."create_default_columns"("board_id" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."get_current_tenant_id"() TO "anon";
GRANT ALL ON FUNCTION "public"."get_current_tenant_id"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."get_current_tenant_id"() TO "service_role";



GRANT ALL ON FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."get_readiness_summary"("board_id_param" "uuid", "stage_param" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."get_user_email_if_admin"("target_user" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."get_user_email_if_admin"("target_user" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."get_user_email_if_admin"("target_user" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."handle_new_board"() TO "anon";
GRANT ALL ON FUNCTION "public"."handle_new_board"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."handle_new_board"() TO "service_role";



GRANT ALL ON FUNCTION "public"."handle_new_team"() TO "anon";
GRANT ALL ON FUNCTION "public"."handle_new_team"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."handle_new_team"() TO "service_role";



GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "anon";
GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "service_role";



GRANT ALL ON FUNCTION "public"."is_team_admin"("team_id" "uuid", "user_id" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."is_team_admin"("team_id" "uuid", "user_id" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_team_admin"("team_id" "uuid", "user_id" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."is_team_admin_or_owner"("team_id" "uuid", "user_id" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."is_team_admin_or_owner"("team_id" "uuid", "user_id" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_team_admin_or_owner"("team_id" "uuid", "user_id" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."is_team_member"("team_id" "uuid", "user_id" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."is_team_member"("team_id" "uuid", "user_id" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_team_member"("team_id" "uuid", "user_id" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."set_tenant_id"() TO "anon";
GRANT ALL ON FUNCTION "public"."set_tenant_id"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."set_tenant_id"() TO "service_role";



GRANT ALL ON FUNCTION "public"."trigger_set_timestamp"() TO "anon";
GRANT ALL ON FUNCTION "public"."trigger_set_timestamp"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."trigger_set_timestamp"() TO "service_role";



GRANT ALL ON FUNCTION "public"."update_item_vote_count"() TO "anon";
GRANT ALL ON FUNCTION "public"."update_item_vote_count"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."update_item_vote_count"() TO "service_role";



GRANT ALL ON FUNCTION "public"."update_last_activity"() TO "anon";
GRANT ALL ON FUNCTION "public"."update_last_activity"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."update_last_activity"() TO "service_role";



























GRANT ALL ON TABLE "public"."app_config" TO "anon";
GRANT ALL ON TABLE "public"."app_config" TO "authenticated";
GRANT ALL ON TABLE "public"."app_config" TO "service_role";



GRANT ALL ON TABLE "public"."board_presence" TO "anon";
GRANT ALL ON TABLE "public"."board_presence" TO "authenticated";
GRANT ALL ON TABLE "public"."board_presence" TO "service_role";



GRANT ALL ON TABLE "public"."board_templates" TO "anon";
GRANT ALL ON TABLE "public"."board_templates" TO "authenticated";
GRANT ALL ON TABLE "public"."board_templates" TO "service_role";



GRANT ALL ON TABLE "public"."feature_flags" TO "anon";
GRANT ALL ON TABLE "public"."feature_flags" TO "authenticated";
GRANT ALL ON TABLE "public"."feature_flags" TO "service_role";



GRANT ALL ON TABLE "public"."feedback_reports" TO "anon";
GRANT ALL ON TABLE "public"."feedback_reports" TO "authenticated";
GRANT ALL ON TABLE "public"."feedback_reports" TO "service_role";



GRANT ALL ON TABLE "public"."notifications" TO "anon";
GRANT ALL ON TABLE "public"."notifications" TO "authenticated";
GRANT ALL ON TABLE "public"."notifications" TO "service_role";



GRANT ALL ON TABLE "public"."poker_session_chat" TO "anon";
GRANT ALL ON TABLE "public"."poker_session_chat" TO "authenticated";
GRANT ALL ON TABLE "public"."poker_session_chat" TO "service_role";



GRANT ALL ON TABLE "public"."poker_session_chat_message_reactions" TO "anon";
GRANT ALL ON TABLE "public"."poker_session_chat_message_reactions" TO "authenticated";
GRANT ALL ON TABLE "public"."poker_session_chat_message_reactions" TO "service_role";



GRANT ALL ON TABLE "public"."poker_session_chat_with_details" TO "anon";
GRANT ALL ON TABLE "public"."poker_session_chat_with_details" TO "authenticated";
GRANT ALL ON TABLE "public"."poker_session_chat_with_details" TO "service_role";



GRANT ALL ON TABLE "public"."poker_session_rounds" TO "anon";
GRANT ALL ON TABLE "public"."poker_session_rounds" TO "authenticated";
GRANT ALL ON TABLE "public"."poker_session_rounds" TO "service_role";



GRANT ALL ON TABLE "public"."poker_sessions" TO "anon";
GRANT ALL ON TABLE "public"."poker_sessions" TO "authenticated";
GRANT ALL ON TABLE "public"."poker_sessions" TO "service_role";



GRANT ALL ON TABLE "public"."profiles" TO "anon";
GRANT ALL ON TABLE "public"."profiles" TO "authenticated";
GRANT ALL ON TABLE "public"."profiles" TO "service_role";



GRANT ALL ON TABLE "public"."realtime_demo" TO "anon";
GRANT ALL ON TABLE "public"."realtime_demo" TO "authenticated";
GRANT ALL ON TABLE "public"."realtime_demo" TO "service_role";



GRANT ALL ON TABLE "public"."retro_board_config" TO "anon";
GRANT ALL ON TABLE "public"."retro_board_config" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_board_config" TO "service_role";



GRANT ALL ON TABLE "public"."retro_board_sessions" TO "anon";
GRANT ALL ON TABLE "public"."retro_board_sessions" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_board_sessions" TO "service_role";



GRANT ALL ON TABLE "public"."retro_boards" TO "anon";
GRANT ALL ON TABLE "public"."retro_boards" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_boards" TO "service_role";



GRANT ALL ON TABLE "public"."retro_columns" TO "anon";
GRANT ALL ON TABLE "public"."retro_columns" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_columns" TO "service_role";



GRANT ALL ON TABLE "public"."retro_comments" TO "anon";
GRANT ALL ON TABLE "public"."retro_comments" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_comments" TO "service_role";



GRANT ALL ON TABLE "public"."retro_items" TO "anon";
GRANT ALL ON TABLE "public"."retro_items" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_items" TO "service_role";



GRANT ALL ON TABLE "public"."retro_user_readiness" TO "anon";
GRANT ALL ON TABLE "public"."retro_user_readiness" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_user_readiness" TO "service_role";



GRANT ALL ON TABLE "public"."retro_votes" TO "anon";
GRANT ALL ON TABLE "public"."retro_votes" TO "authenticated";
GRANT ALL ON TABLE "public"."retro_votes" TO "service_role";



GRANT ALL ON TABLE "public"."team_action_items" TO "anon";
GRANT ALL ON TABLE "public"."team_action_items" TO "authenticated";
GRANT ALL ON TABLE "public"."team_action_items" TO "service_role";



GRANT ALL ON TABLE "public"."team_default_settings" TO "anon";
GRANT ALL ON TABLE "public"."team_default_settings" TO "authenticated";
GRANT ALL ON TABLE "public"."team_default_settings" TO "service_role";



GRANT ALL ON TABLE "public"."team_invitations" TO "anon";
GRANT ALL ON TABLE "public"."team_invitations" TO "authenticated";
GRANT ALL ON TABLE "public"."team_invitations" TO "service_role";



GRANT ALL ON TABLE "public"."team_members" TO "anon";
GRANT ALL ON TABLE "public"."team_members" TO "authenticated";
GRANT ALL ON TABLE "public"."team_members" TO "service_role";



GRANT ALL ON TABLE "public"."teams" TO "anon";
GRANT ALL ON TABLE "public"."teams" TO "authenticated";
GRANT ALL ON TABLE "public"."teams" TO "service_role";



GRANT ALL ON TABLE "public"."template_columns" TO "anon";
GRANT ALL ON TABLE "public"."template_columns" TO "authenticated";
GRANT ALL ON TABLE "public"."template_columns" TO "service_role";



GRANT ALL ON TABLE "public"."tenants" TO "anon";
GRANT ALL ON TABLE "public"."tenants" TO "authenticated";
GRANT ALL ON TABLE "public"."tenants" TO "service_role";









ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES  TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES  TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES  TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES  TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS  TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS  TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS  TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS  TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES  TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES  TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES  TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES  TO "service_role";






























RESET ALL;
