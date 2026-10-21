--
-- PostgreSQL database dump
--


-- Dumped from database version 18.6
-- Dumped by pg_dump version 18.6

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: archive; Type: SCHEMA; Schema: -; Owner: retroscope_owner
--

CREATE SCHEMA archive;


ALTER SCHEMA archive OWNER TO retroscope_owner;

--
-- Name: rs_fence(bigint); Type: FUNCTION; Schema: public; Owner: retroscope_owner
--

CREATE FUNCTION public.rs_fence(expected bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
begin
  perform pg_advisory_xact_lock_shared(1381123153);         -- K_FENCE
  if (select leader_epoch from system_state) <> expected then
    raise exception 'fencing_lost' using errcode = 'RS001';
  end if;
end $$;


ALTER FUNCTION public.rs_fence(expected bigint) OWNER TO retroscope_owner;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: excluded_tables; Type: TABLE; Schema: archive; Owner: retroscope_owner
--

CREATE TABLE archive.excluded_tables (
    source_table text NOT NULL,
    row_count bigint NOT NULL,
    reason text NOT NULL,
    import_run uuid NOT NULL
);


ALTER TABLE archive.excluded_tables OWNER TO retroscope_owner;

--
-- Name: import_runs; Type: TABLE; Schema: archive; Owner: retroscope_owner
--

CREATE TABLE archive.import_runs (
    id uuid NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    finished_at timestamp with time zone,
    status text NOT NULL,
    source_fingerprint text NOT NULL,
    seal_key_id text,
    report jsonb,
    CONSTRAINT import_runs_status_check CHECK ((status = ANY (ARRAY['running'::text, 'succeeded'::text, 'failed'::text])))
);


ALTER TABLE archive.import_runs OWNER TO retroscope_owner;

--
-- Name: rows; Type: TABLE; Schema: archive; Owner: retroscope_owner
--

CREATE TABLE archive.rows (
    source_table text NOT NULL,
    source_pk text NOT NULL,
    "row" jsonb NOT NULL,
    import_run uuid NOT NULL
);


ALTER TABLE archive.rows OWNER TO retroscope_owner;

--
-- Name: storage_cache; Type: TABLE; Schema: archive; Owner: retroscope_owner
--

CREATE TABLE archive.storage_cache (
    bucket text NOT NULL,
    path text NOT NULL,
    sha256 bytea NOT NULL,
    bytes bytea NOT NULL
);


ALTER TABLE archive.storage_cache OWNER TO retroscope_owner;

--
-- Name: storage_objects; Type: TABLE; Schema: archive; Owner: retroscope_owner
--

CREATE TABLE archive.storage_objects (
    bucket text NOT NULL,
    path text NOT NULL,
    byte_size bigint NOT NULL,
    sha256 bytea,
    disposition text NOT NULL,
    blob_id uuid,
    import_run uuid NOT NULL,
    CONSTRAINT storage_objects_disposition_check CHECK ((disposition = ANY (ARRAY['imported'::text, 'manifest_only'::text, 'skipped'::text])))
);


ALTER TABLE archive.storage_objects OWNER TO retroscope_owner;

--
-- Name: audit_log; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.audit_log (
    id bigint NOT NULL,
    at timestamp with time zone DEFAULT now() NOT NULL,
    actor_id uuid,
    team_id uuid,
    action text NOT NULL,
    target text,
    meta jsonb DEFAULT '{}'::jsonb NOT NULL
);


ALTER TABLE public.audit_log OWNER TO retroscope_owner;

--
-- Name: audit_log_id_seq; Type: SEQUENCE; Schema: public; Owner: retroscope_owner
--

ALTER TABLE public.audit_log ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.audit_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: auth_tokens; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.auth_tokens (
    token_hash bytea NOT NULL,
    purpose text NOT NULL,
    user_id uuid,
    email text NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    consumed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT auth_tokens_purpose_check CHECK ((purpose = ANY (ARRAY['magic_link'::text, 'password_reset'::text, 'verify_email'::text]))),
    CONSTRAINT auth_tokens_token_hash_check CHECK ((octet_length(token_hash) = 32))
);


ALTER TABLE public.auth_tokens OWNER TO retroscope_owner;

--
-- Name: blobs; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.blobs (
    id uuid NOT NULL,
    purpose text NOT NULL,
    board_id uuid,
    content_type text NOT NULL,
    byte_size integer NOT NULL,
    sha256 bytea NOT NULL,
    bytes bytea NOT NULL,
    created_by uuid,
    attached_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT blobs_byte_size_check CHECK (((byte_size >= 1) AND (byte_size <= 2097152))),
    CONSTRAINT blobs_check CHECK (((purpose = 'board_image'::text) = (board_id IS NOT NULL))),
    CONSTRAINT blobs_check1 CHECK (((purpose <> 'avatar'::text) OR (byte_size <= 262144))),
    CONSTRAINT blobs_content_type_check CHECK ((content_type = ANY (ARRAY['image/png'::text, 'image/jpeg'::text, 'image/gif'::text, 'image/webp'::text]))),
    CONSTRAINT blobs_purpose_check CHECK ((purpose = ANY (ARRAY['avatar'::text, 'board_image'::text]))),
    CONSTRAINT blobs_sha256_check CHECK ((octet_length(sha256) = 32))
);
ALTER TABLE ONLY public.blobs ALTER COLUMN bytes SET STORAGE EXTERNAL;


ALTER TABLE public.blobs OWNER TO retroscope_owner;

--
-- Name: board_access_grants; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.board_access_grants (
    board_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    password_epoch timestamp with time zone NOT NULL,
    granted_at timestamp with time zone DEFAULT now() NOT NULL
);


ALTER TABLE public.board_access_grants OWNER TO retroscope_owner;

--
-- Name: board_templates; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.board_templates (
    id uuid NOT NULL,
    team_id uuid NOT NULL,
    name text NOT NULL,
    is_default boolean DEFAULT false NOT NULL,
    allow_anonymous boolean DEFAULT true NOT NULL,
    voting_enabled boolean DEFAULT true NOT NULL,
    max_votes_per_user smallint,
    show_author_names boolean DEFAULT true NOT NULL,
    stages_enabled boolean DEFAULT false NOT NULL,
    enforce_readiness boolean DEFAULT false NOT NULL,
    allow_self_votes boolean DEFAULT true NOT NULL,
    vote_emoji text DEFAULT '👍'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT board_templates_max_votes_per_user_check CHECK (((max_votes_per_user >= 1) AND (max_votes_per_user <= 99))),
    CONSTRAINT board_templates_name_check CHECK (((char_length(name) >= 1) AND (char_length(name) <= 100))),
    CONSTRAINT board_templates_vote_emoji_check CHECK (((char_length(vote_emoji) >= 1) AND (char_length(vote_emoji) <= 8)))
);


ALTER TABLE public.board_templates OWNER TO retroscope_owner;

--
-- Name: client_cursors; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.client_cursors (
    room_key text NOT NULL,
    client_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    last_cseq bigint NOT NULL,
    updated_on date DEFAULT CURRENT_DATE NOT NULL,
    CONSTRAINT client_cursors_last_cseq_check CHECK ((last_cseq > 0))
);


ALTER TABLE public.client_cursors OWNER TO retroscope_owner;

--
-- Name: email_outbox; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.email_outbox (
    id bigint NOT NULL,
    to_address text NOT NULL,
    subject text NOT NULL,
    body_text text,
    attempts integer DEFAULT 0 NOT NULL,
    next_attempt_at timestamp with time zone DEFAULT now() NOT NULL,
    last_error text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT email_outbox_subject_check CHECK ((subject !~ '[\r\n]'::text))
);


ALTER TABLE public.email_outbox OWNER TO retroscope_owner;

--
-- Name: email_outbox_id_seq; Type: SEQUENCE; Schema: public; Owner: retroscope_owner
--

ALTER TABLE public.email_outbox ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.email_outbox_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: endorsement_types; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.endorsement_types (
    id uuid NOT NULL,
    team_id uuid NOT NULL,
    name text NOT NULL,
    description text,
    emoji text NOT NULL,
    sort_key integer NOT NULL,
    is_default boolean DEFAULT false NOT NULL,
    archived_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT endorsement_types_description_check CHECK ((char_length(description) <= 200)),
    CONSTRAINT endorsement_types_emoji_check CHECK (((char_length(emoji) >= 1) AND (char_length(emoji) <= 8))),
    CONSTRAINT endorsement_types_name_check CHECK (((char_length(name) >= 1) AND (char_length(name) <= 60)))
);


ALTER TABLE public.endorsement_types OWNER TO retroscope_owner;

--
-- Name: endorsements; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.endorsements (
    id uuid NOT NULL,
    board_id uuid NOT NULL,
    team_id uuid NOT NULL,
    type_id uuid NOT NULL,
    from_user_id uuid NOT NULL,
    to_user_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT endorsements_check CHECK ((from_user_id <> to_user_id))
);


ALTER TABLE public.endorsements OWNER TO retroscope_owner;

--
-- Name: external_identities; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.external_identities (
    provider text NOT NULL,
    subject text NOT NULL,
    user_id uuid NOT NULL,
    linked_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT external_identities_provider_check CHECK ((provider = ANY (ARRAY['google'::text, 'entra'::text]))),
    CONSTRAINT external_identities_subject_check CHECK ((char_length(subject) <= 200))
);


ALTER TABLE public.external_identities OWNER TO retroscope_owner;

--
-- Name: guest_tokens; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.guest_tokens (
    token_hash bytea NOT NULL,
    principal_id uuid NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    CONSTRAINT guest_tokens_token_hash_check CHECK ((octet_length(token_hash) = 32))
);


ALTER TABLE public.guest_tokens OWNER TO retroscope_owner;

--
-- Name: mentions; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.mentions (
    source_kind text NOT NULL,
    source_id uuid NOT NULL,
    board_id uuid NOT NULL,
    user_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT mentions_source_kind_check CHECK ((source_kind = ANY (ARRAY['card'::text, 'comment'::text])))
);


ALTER TABLE public.mentions OWNER TO retroscope_owner;

--
-- Name: poker_observers; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.poker_observers (
    session_id uuid NOT NULL,
    principal_id uuid NOT NULL
);


ALTER TABLE public.poker_observers OWNER TO retroscope_owner;

--
-- Name: poker_rounds; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.poker_rounds (
    id uuid NOT NULL,
    session_id uuid NOT NULL,
    number integer NOT NULL,
    sort_key integer NOT NULL,
    ticket_key text,
    parent_key text,
    state text DEFAULT 'voting'::text NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    is_pending boolean DEFAULT false NOT NULL,
    pending_owner_id uuid,
    version integer DEFAULT 1 NOT NULL,
    auto_reveal boolean DEFAULT true NOT NULL,
    winner_points smallint,
    revealed_at timestamp with time zone,
    revealed_by uuid,
    jira_points numeric(6,2),
    jira_points_at timestamp with time zone,
    jira_points_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT poker_rounds_check CHECK (((state = 'revealed'::text) OR ((winner_points IS NULL) AND (revealed_at IS NULL)))),
    CONSTRAINT poker_rounds_check1 CHECK (((NOT is_pending) OR (pending_owner_id IS NOT NULL))),
    CONSTRAINT poker_rounds_jira_points_check CHECK (((jira_points >= (0)::numeric) AND (jira_points <= (1000)::numeric))),
    CONSTRAINT poker_rounds_number_check CHECK ((number >= 1)),
    CONSTRAINT poker_rounds_parent_key_check CHECK ((parent_key ~ '^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,8}$'::text)),
    CONSTRAINT poker_rounds_state_check CHECK ((state = ANY (ARRAY['voting'::text, 'revealed'::text]))),
    CONSTRAINT poker_rounds_ticket_key_check CHECK ((ticket_key ~ '^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,8}$'::text)),
    CONSTRAINT poker_rounds_winner_points_check CHECK ((winner_points = ANY (ARRAY[0, 1, 2, 3, 5, 8, 13, 21])))
);


ALTER TABLE public.poker_rounds OWNER TO retroscope_owner;

--
-- Name: poker_sessions; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.poker_sessions (
    id uuid NOT NULL,
    code text NOT NULL,
    team_id uuid,
    created_by uuid,
    presence_enabled boolean DEFAULT true NOT NULL,
    follow_spotlight boolean DEFAULT true NOT NULL,
    spotlight_holder_id uuid,
    spotlight_client_id uuid,
    spotlight_round_id uuid,
    current_round_id uuid,
    next_round_number integer DEFAULT 1 NOT NULL,
    last_activity_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT poker_sessions_check CHECK (((spotlight_holder_id IS NULL) = (spotlight_round_id IS NULL))),
    CONSTRAINT poker_sessions_code_check CHECK ((code ~ '^[A-Za-z0-9-]{4,36}$'::text)),
    CONSTRAINT poker_sessions_next_round_number_check CHECK ((next_round_number >= 1))
);


ALTER TABLE public.poker_sessions OWNER TO retroscope_owner;

--
-- Name: poker_votes; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.poker_votes (
    round_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    points smallint,
    high_points smallint,
    locked boolean DEFAULT false NOT NULL,
    abstained boolean DEFAULT false NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT poker_votes_check CHECK ((abstained = (points IS NULL))),
    CONSTRAINT poker_votes_check1 CHECK (((NOT abstained) OR (locked AND (high_points IS NULL)))),
    CONSTRAINT poker_votes_check2 CHECK (((high_points IS NULL) OR (high_points > points))),
    CONSTRAINT poker_votes_high_points_check CHECK ((high_points = ANY (ARRAY[2, 3, 5, 8, 13, 21]))),
    CONSTRAINT poker_votes_points_check CHECK ((points = ANY (ARRAY[1, 2, 3, 5, 8, 13, 21])))
);


ALTER TABLE public.poker_votes OWNER TO retroscope_owner;

--
-- Name: principals; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.principals (
    id uuid NOT NULL,
    kind text NOT NULL,
    display_name text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT principals_check CHECK (((kind = 'user'::text) OR (display_name IS NOT NULL))),
    CONSTRAINT principals_display_name_check CHECK (((display_name IS NULL) OR ((char_length(display_name) >= 1) AND (char_length(display_name) <= 60)))),
    CONSTRAINT principals_kind_check CHECK ((kind = ANY (ARRAY['user'::text, 'guest'::text, 'external'::text])))
);


ALTER TABLE public.principals OWNER TO retroscope_owner;

--
-- Name: recent_activity; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.recent_activity (
    user_id uuid NOT NULL,
    entity_kind text NOT NULL,
    entity_id uuid NOT NULL,
    last_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT recent_activity_entity_kind_check CHECK ((entity_kind = ANY (ARRAY['team'::text, 'board'::text, 'poker'::text])))
);


ALTER TABLE public.recent_activity OWNER TO retroscope_owner;

--
-- Name: retro_boards; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_boards (
    id uuid NOT NULL,
    code text NOT NULL,
    team_id uuid,
    title text NOT NULL,
    created_by uuid,
    visibility text NOT NULL,
    password_hash text,
    password_set_at timestamp with time zone,
    stage text DEFAULT 'thinking'::text NOT NULL,
    focus_card_id uuid,
    allow_anonymous boolean DEFAULT true NOT NULL,
    voting_enabled boolean DEFAULT true NOT NULL,
    max_votes_per_user smallint,
    show_author_names boolean DEFAULT true NOT NULL,
    stages_enabled boolean DEFAULT false NOT NULL,
    enforce_readiness boolean DEFAULT false NOT NULL,
    allow_self_votes boolean DEFAULT true NOT NULL,
    vote_emoji text DEFAULT '👍'::text NOT NULL,
    sort_chronologically boolean DEFAULT false NOT NULL,
    timer_state text DEFAULT 'idle'::text NOT NULL,
    timer_duration_ms integer DEFAULT 900000 NOT NULL,
    timer_ends_at timestamp with time zone,
    timer_remaining_ms integer,
    timer_alarm boolean DEFAULT true NOT NULL,
    timer_music boolean DEFAULT false NOT NULL,
    archived_at timestamp with time zone,
    archived_by uuid,
    deleted_at timestamp with time zone,
    last_activity_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retro_boards_check CHECK (((visibility = 'password'::text) = (password_hash IS NOT NULL))),
    CONSTRAINT retro_boards_check1 CHECK (((password_hash IS NULL) = (password_set_at IS NULL))),
    CONSTRAINT retro_boards_check2 CHECK (((visibility <> 'team'::text) OR (team_id IS NOT NULL))),
    CONSTRAINT retro_boards_check3 CHECK (((timer_state = 'running'::text) = (timer_ends_at IS NOT NULL))),
    CONSTRAINT retro_boards_check4 CHECK (((timer_state = 'paused'::text) = (timer_remaining_ms IS NOT NULL))),
    CONSTRAINT retro_boards_code_check CHECK ((code ~ '^[A-Za-z0-9-]{3,64}$'::text)),
    CONSTRAINT retro_boards_max_votes_per_user_check CHECK (((max_votes_per_user >= 1) AND (max_votes_per_user <= 99))),
    CONSTRAINT retro_boards_password_hash_check CHECK ((password_hash ~ '^\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}$'::text)),
    CONSTRAINT retro_boards_stage_check CHECK ((stage = ANY (ARRAY['thinking'::text, 'voting'::text, 'discussing'::text, 'closed'::text]))),
    CONSTRAINT retro_boards_timer_duration_ms_check CHECK (((timer_duration_ms >= 1000) AND (timer_duration_ms <= 7200000))),
    CONSTRAINT retro_boards_timer_remaining_ms_check CHECK ((timer_remaining_ms >= 0)),
    CONSTRAINT retro_boards_timer_state_check CHECK ((timer_state = ANY (ARRAY['idle'::text, 'running'::text, 'paused'::text]))),
    CONSTRAINT retro_boards_title_check CHECK (((char_length(title) >= 1) AND (char_length(title) <= 200))),
    CONSTRAINT retro_boards_visibility_check CHECK ((visibility = ANY (ARRAY['link'::text, 'team'::text, 'password'::text]))),
    CONSTRAINT retro_boards_vote_emoji_check CHECK (((char_length(vote_emoji) >= 1) AND (char_length(vote_emoji) <= 8)))
);


ALTER TABLE public.retro_boards OWNER TO retroscope_owner;

--
-- Name: retro_cards; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_cards (
    id uuid NOT NULL,
    board_id uuid NOT NULL,
    column_id uuid NOT NULL,
    body text NOT NULL,
    author_id uuid,
    anon_tag bytea,
    legacy_author text,
    version integer DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retro_cards_anon_tag_check CHECK (((anon_tag IS NULL) OR (octet_length(anon_tag) = 32))),
    CONSTRAINT retro_cards_body_check CHECK (((char_length(body) >= 1) AND (char_length(body) <= 8000))),
    CONSTRAINT retro_cards_check CHECK ((num_nonnulls(author_id, anon_tag) <= 1)),
    CONSTRAINT retro_cards_legacy_author_check CHECK ((char_length(legacy_author) <= 100))
);


ALTER TABLE public.retro_cards OWNER TO retroscope_owner;

--
-- Name: retro_columns; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_columns (
    id uuid NOT NULL,
    board_id uuid NOT NULL,
    title text NOT NULL,
    color text NOT NULL,
    sort_key integer NOT NULL,
    is_action_items boolean DEFAULT false NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retro_columns_color_check CHECK ((color = ANY (ARRAY['green'::text, 'red'::text, 'blue'::text, 'yellow'::text, 'purple'::text, 'pink'::text, 'indigo'::text, 'orange'::text, 'gray'::text]))),
    CONSTRAINT retro_columns_title_check CHECK (((char_length(title) >= 1) AND (char_length(title) <= 100)))
);


ALTER TABLE public.retro_columns OWNER TO retroscope_owner;

--
-- Name: retro_comments; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_comments (
    id uuid NOT NULL,
    card_id uuid NOT NULL,
    board_id uuid NOT NULL,
    body text NOT NULL,
    author_id uuid,
    anon_tag bytea,
    legacy_author text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retro_comments_anon_tag_check CHECK (((anon_tag IS NULL) OR (octet_length(anon_tag) = 32))),
    CONSTRAINT retro_comments_body_check CHECK (((char_length(body) >= 1) AND (char_length(body) <= 4000))),
    CONSTRAINT retro_comments_check CHECK ((num_nonnulls(author_id, anon_tag) <= 1)),
    CONSTRAINT retro_comments_legacy_author_check CHECK ((char_length(legacy_author) <= 100))
);


ALTER TABLE public.retro_comments OWNER TO retroscope_owner;

--
-- Name: retro_readiness; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_readiness (
    board_id uuid NOT NULL,
    stage text NOT NULL,
    principal_id uuid NOT NULL,
    ready_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retro_readiness_stage_check CHECK ((stage = ANY (ARRAY['thinking'::text, 'voting'::text, 'discussing'::text, 'closed'::text])))
);


ALTER TABLE public.retro_readiness OWNER TO retroscope_owner;

--
-- Name: retro_votes; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.retro_votes (
    card_id uuid NOT NULL,
    voter_id uuid NOT NULL,
    board_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


ALTER TABLE public.retro_votes OWNER TO retroscope_owner;

--
-- Name: schema_migrations; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.schema_migrations (
    version integer NOT NULL,
    name text NOT NULL,
    sha256 bytea NOT NULL,
    applied_at timestamp with time zone DEFAULT now() NOT NULL
);


ALTER TABLE public.schema_migrations OWNER TO retroscope_owner;

--
-- Name: sessions; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.sessions (
    id_hash bytea NOT NULL,
    user_id uuid NOT NULL,
    auth_method text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    last_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    idle_expires_at timestamp with time zone NOT NULL,
    abs_expires_at timestamp with time zone NOT NULL,
    user_agent text,
    CONSTRAINT sessions_auth_method_check CHECK ((auth_method = ANY (ARRAY['password'::text, 'google'::text, 'entra'::text, 'magic_link'::text]))),
    CONSTRAINT sessions_id_hash_check CHECK ((octet_length(id_hash) = 32)),
    CONSTRAINT sessions_user_agent_check CHECK ((char_length(user_agent) <= 200))
);


ALTER TABLE public.sessions OWNER TO retroscope_owner;

--
-- Name: system_state; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.system_state (
    id boolean DEFAULT true NOT NULL,
    leader_epoch bigint DEFAULT 0 NOT NULL,
    leader_instance uuid,
    leader_since timestamp with time zone,
    cutover_at timestamp with time zone,
    CONSTRAINT system_state_id_check CHECK (id)
);


ALTER TABLE public.system_state OWNER TO retroscope_owner;

--
-- Name: team_action_items; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.team_action_items (
    id uuid NOT NULL,
    team_id uuid NOT NULL,
    text text NOT NULL,
    source_board_id uuid,
    source_card_id uuid,
    source_version integer DEFAULT 0 NOT NULL,
    assigned_to uuid,
    done_at timestamp with time zone,
    done_by uuid,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT team_action_items_text_check CHECK (((char_length(text) >= 1) AND (char_length(text) <= 8000)))
);


ALTER TABLE public.team_action_items OWNER TO retroscope_owner;

--
-- Name: team_favorites; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.team_favorites (
    user_id uuid NOT NULL,
    team_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


ALTER TABLE public.team_favorites OWNER TO retroscope_owner;

--
-- Name: team_invites; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.team_invites (
    id uuid NOT NULL,
    team_id uuid NOT NULL,
    kind text NOT NULL,
    email text,
    role text DEFAULT 'member'::text NOT NULL,
    token_hash bytea NOT NULL,
    token_box bytea,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    accepted_at timestamp with time zone,
    accepted_by uuid,
    use_count integer DEFAULT 0 NOT NULL,
    CONSTRAINT team_invites_check CHECK (((kind = 'email'::text) = (email IS NOT NULL))),
    CONSTRAINT team_invites_check1 CHECK (((kind = 'link'::text) = (token_box IS NOT NULL))),
    CONSTRAINT team_invites_check2 CHECK (((kind = 'email'::text) OR (role = 'member'::text))),
    CONSTRAINT team_invites_email_check CHECK ((email = lower(email))),
    CONSTRAINT team_invites_kind_check CHECK ((kind = ANY (ARRAY['email'::text, 'link'::text]))),
    CONSTRAINT team_invites_role_check CHECK ((role = ANY (ARRAY['member'::text, 'admin'::text]))),
    CONSTRAINT team_invites_token_hash_check CHECK ((octet_length(token_hash) = 32))
);


ALTER TABLE public.team_invites OWNER TO retroscope_owner;

--
-- Name: team_jira_config; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.team_jira_config (
    team_id uuid NOT NULL,
    site_host text NOT NULL,
    credentials_box bytea NOT NULL,
    key_id text NOT NULL,
    token_last4 text NOT NULL,
    email_masked text NOT NULL,
    project_key text,
    board_id bigint,
    ticket_prefix text,
    points_field_id text,
    points_field_overridden boolean DEFAULT false NOT NULL,
    sprint_field_id text,
    status text DEFAULT 'needs_test'::text NOT NULL,
    members_can_edit boolean DEFAULT true NOT NULL,
    last_checked_at timestamp with time zone,
    updated_by uuid,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT team_jira_config_board_id_check CHECK ((board_id > 0)),
    CONSTRAINT team_jira_config_points_field_id_check CHECK ((points_field_id ~ '^customfield_[0-9]{1,8}$'::text)),
    CONSTRAINT team_jira_config_project_key_check CHECK ((project_key ~ '^[A-Z][A-Z0-9_]{0,19}$'::text)),
    CONSTRAINT team_jira_config_site_host_check CHECK ((site_host ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'::text)),
    CONSTRAINT team_jira_config_sprint_field_id_check CHECK ((sprint_field_id ~ '^customfield_[0-9]{1,8}$'::text)),
    CONSTRAINT team_jira_config_status_check CHECK ((status = ANY (ARRAY['ok'::text, 'needs_test'::text, 'auth_failed'::text, 'unreachable'::text, 'rotate_required'::text]))),
    CONSTRAINT team_jira_config_ticket_prefix_check CHECK ((ticket_prefix ~ '^[A-Z][A-Z0-9_]{0,19}$'::text)),
    CONSTRAINT team_jira_config_token_last4_check CHECK ((char_length(token_last4) = 4))
);


ALTER TABLE public.team_jira_config OWNER TO retroscope_owner;

--
-- Name: team_members; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.team_members (
    team_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role text NOT NULL,
    joined_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT team_members_role_check CHECK ((role = ANY (ARRAY['owner'::text, 'admin'::text, 'member'::text])))
);


ALTER TABLE public.team_members OWNER TO retroscope_owner;

--
-- Name: teams; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.teams (
    id uuid NOT NULL,
    name text NOT NULL,
    description text,
    created_by uuid,
    point_descriptions jsonb DEFAULT '{}'::jsonb NOT NULL,
    max_endorsements_per_board smallint DEFAULT 3 NOT NULL,
    deleted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT teams_description_check CHECK ((char_length(description) <= 500)),
    CONSTRAINT teams_max_endorsements_per_board_check CHECK (((max_endorsements_per_board >= 0) AND (max_endorsements_per_board <= 50))),
    CONSTRAINT teams_name_check CHECK (((char_length(name) >= 1) AND (char_length(name) <= 100))),
    CONSTRAINT teams_point_descriptions_check CHECK ((jsonb_typeof(point_descriptions) = 'object'::text))
);


ALTER TABLE public.teams OWNER TO retroscope_owner;

--
-- Name: template_columns; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.template_columns (
    id uuid NOT NULL,
    template_id uuid NOT NULL,
    title text NOT NULL,
    color text NOT NULL,
    sort_key integer NOT NULL,
    is_action_items boolean DEFAULT false NOT NULL,
    CONSTRAINT template_columns_color_check CHECK ((color = ANY (ARRAY['green'::text, 'red'::text, 'blue'::text, 'yellow'::text, 'purple'::text, 'pink'::text, 'indigo'::text, 'orange'::text, 'gray'::text]))),
    CONSTRAINT template_columns_title_check CHECK (((char_length(title) >= 1) AND (char_length(title) <= 100)))
);


ALTER TABLE public.template_columns OWNER TO retroscope_owner;

--
-- Name: users; Type: TABLE; Schema: public; Owner: retroscope_owner
--

CREATE TABLE public.users (
    id uuid NOT NULL,
    email text NOT NULL,
    email_verified_at timestamp with time zone,
    password_hash text,
    full_name text DEFAULT ''::text NOT NULL,
    nickname text,
    avatar_blob_id uuid,
    theme text DEFAULT 'system'::text NOT NULL,
    is_admin boolean DEFAULT false NOT NULL,
    disabled_at timestamp with time zone,
    mentions_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT users_email_check CHECK (((email = lower(email)) AND ((char_length(email) >= 3) AND (char_length(email) <= 254)) AND (POSITION(('@'::text) IN (email)) > 1))),
    CONSTRAINT users_full_name_check CHECK ((char_length(full_name) <= 100)),
    CONSTRAINT users_nickname_check CHECK (((nickname IS NULL) OR ((char_length(nickname) >= 1) AND (char_length(nickname) <= 40)))),
    CONSTRAINT users_password_hash_check CHECK ((password_hash ~ '^\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}$'::text)),
    CONSTRAINT users_theme_check CHECK ((theme = ANY (ARRAY['light'::text, 'dark'::text, 'system'::text])))
);


ALTER TABLE public.users OWNER TO retroscope_owner;

--
-- Name: excluded_tables excluded_tables_pkey; Type: CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.excluded_tables
    ADD CONSTRAINT excluded_tables_pkey PRIMARY KEY (source_table);


--
-- Name: import_runs import_runs_pkey; Type: CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.import_runs
    ADD CONSTRAINT import_runs_pkey PRIMARY KEY (id);


--
-- Name: rows rows_pkey; Type: CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.rows
    ADD CONSTRAINT rows_pkey PRIMARY KEY (source_table, source_pk);


--
-- Name: storage_cache storage_cache_pkey; Type: CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.storage_cache
    ADD CONSTRAINT storage_cache_pkey PRIMARY KEY (bucket, path);


--
-- Name: storage_objects storage_objects_pkey; Type: CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.storage_objects
    ADD CONSTRAINT storage_objects_pkey PRIMARY KEY (bucket, path);


--
-- Name: audit_log audit_log_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.audit_log
    ADD CONSTRAINT audit_log_pkey PRIMARY KEY (id);


--
-- Name: auth_tokens auth_tokens_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT auth_tokens_pkey PRIMARY KEY (token_hash);


--
-- Name: blobs blobs_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.blobs
    ADD CONSTRAINT blobs_pkey PRIMARY KEY (id);


--
-- Name: board_access_grants board_access_grants_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.board_access_grants
    ADD CONSTRAINT board_access_grants_pkey PRIMARY KEY (board_id, principal_id);


--
-- Name: board_templates board_templates_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.board_templates
    ADD CONSTRAINT board_templates_pkey PRIMARY KEY (id);


--
-- Name: client_cursors client_cursors_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.client_cursors
    ADD CONSTRAINT client_cursors_pkey PRIMARY KEY (room_key, client_id);


--
-- Name: email_outbox email_outbox_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.email_outbox
    ADD CONSTRAINT email_outbox_pkey PRIMARY KEY (id);


--
-- Name: endorsement_types endorsement_types_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsement_types
    ADD CONSTRAINT endorsement_types_pkey PRIMARY KEY (id);


--
-- Name: endorsements endorsements_board_id_from_user_id_to_user_id_type_id_key; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_board_id_from_user_id_to_user_id_type_id_key UNIQUE (board_id, from_user_id, to_user_id, type_id);


--
-- Name: endorsements endorsements_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_pkey PRIMARY KEY (id);


--
-- Name: external_identities external_identities_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.external_identities
    ADD CONSTRAINT external_identities_pkey PRIMARY KEY (provider, subject);


--
-- Name: guest_tokens guest_tokens_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.guest_tokens
    ADD CONSTRAINT guest_tokens_pkey PRIMARY KEY (token_hash);


--
-- Name: mentions mentions_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.mentions
    ADD CONSTRAINT mentions_pkey PRIMARY KEY (source_kind, source_id, user_id);


--
-- Name: poker_observers poker_observers_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_observers
    ADD CONSTRAINT poker_observers_pkey PRIMARY KEY (session_id, principal_id);


--
-- Name: poker_rounds poker_rounds_number_uq; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_number_uq UNIQUE (session_id, number);


--
-- Name: poker_rounds poker_rounds_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_pkey PRIMARY KEY (id);


--
-- Name: poker_rounds poker_rounds_sort_uq; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_sort_uq UNIQUE (session_id, sort_key) DEFERRABLE INITIALLY DEFERRED;


--
-- Name: poker_sessions poker_sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_pkey PRIMARY KEY (id);


--
-- Name: poker_votes poker_votes_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_votes
    ADD CONSTRAINT poker_votes_pkey PRIMARY KEY (round_id, principal_id);


--
-- Name: principals principals_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.principals
    ADD CONSTRAINT principals_pkey PRIMARY KEY (id);


--
-- Name: recent_activity recent_activity_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.recent_activity
    ADD CONSTRAINT recent_activity_pkey PRIMARY KEY (user_id, entity_kind, entity_id);


--
-- Name: retro_boards retro_boards_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_boards
    ADD CONSTRAINT retro_boards_pkey PRIMARY KEY (id);


--
-- Name: retro_cards retro_cards_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_cards
    ADD CONSTRAINT retro_cards_pkey PRIMARY KEY (id);


--
-- Name: retro_columns retro_columns_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_columns
    ADD CONSTRAINT retro_columns_pkey PRIMARY KEY (id);


--
-- Name: retro_columns retro_columns_sort_uq; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_columns
    ADD CONSTRAINT retro_columns_sort_uq UNIQUE (board_id, sort_key) DEFERRABLE INITIALLY DEFERRED;


--
-- Name: retro_comments retro_comments_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_comments
    ADD CONSTRAINT retro_comments_pkey PRIMARY KEY (id);


--
-- Name: retro_readiness retro_readiness_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_readiness
    ADD CONSTRAINT retro_readiness_pkey PRIMARY KEY (board_id, stage, principal_id);


--
-- Name: retro_votes retro_votes_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_votes
    ADD CONSTRAINT retro_votes_pkey PRIMARY KEY (card_id, voter_id);


--
-- Name: schema_migrations schema_migrations_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.schema_migrations
    ADD CONSTRAINT schema_migrations_pkey PRIMARY KEY (version);


--
-- Name: sessions sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.sessions
    ADD CONSTRAINT sessions_pkey PRIMARY KEY (id_hash);


--
-- Name: system_state system_state_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.system_state
    ADD CONSTRAINT system_state_pkey PRIMARY KEY (id);


--
-- Name: team_action_items team_action_items_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_pkey PRIMARY KEY (id);


--
-- Name: team_favorites team_favorites_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_favorites
    ADD CONSTRAINT team_favorites_pkey PRIMARY KEY (user_id, team_id);


--
-- Name: team_invites team_invites_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_invites
    ADD CONSTRAINT team_invites_pkey PRIMARY KEY (id);


--
-- Name: team_invites team_invites_token_hash_key; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_invites
    ADD CONSTRAINT team_invites_token_hash_key UNIQUE (token_hash);


--
-- Name: team_jira_config team_jira_config_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_jira_config
    ADD CONSTRAINT team_jira_config_pkey PRIMARY KEY (team_id);


--
-- Name: team_members team_members_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_pkey PRIMARY KEY (team_id, user_id);


--
-- Name: teams teams_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.teams
    ADD CONSTRAINT teams_pkey PRIMARY KEY (id);


--
-- Name: template_columns template_columns_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.template_columns
    ADD CONSTRAINT template_columns_pkey PRIMARY KEY (id);


--
-- Name: template_columns template_columns_template_id_sort_key_key; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.template_columns
    ADD CONSTRAINT template_columns_template_id_sort_key_key UNIQUE (template_id, sort_key);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: audit_log_at_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX audit_log_at_ix ON public.audit_log USING btree (at);


--
-- Name: audit_log_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX audit_log_team_ix ON public.audit_log USING btree (team_id, at DESC) WHERE (team_id IS NOT NULL);


--
-- Name: auth_tokens_expiry_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX auth_tokens_expiry_ix ON public.auth_tokens USING btree (expires_at);


--
-- Name: blobs_board_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX blobs_board_ix ON public.blobs USING btree (board_id) WHERE (board_id IS NOT NULL);


--
-- Name: blobs_unattached_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX blobs_unattached_ix ON public.blobs USING btree (created_at) WHERE ((purpose = 'board_image'::text) AND (attached_at IS NULL));


--
-- Name: board_templates_one_default_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX board_templates_one_default_uq ON public.board_templates USING btree (team_id) WHERE is_default;


--
-- Name: board_templates_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX board_templates_team_ix ON public.board_templates USING btree (team_id);


--
-- Name: email_outbox_due_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX email_outbox_due_ix ON public.email_outbox USING btree (next_attempt_at);


--
-- Name: endorsement_types_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX endorsement_types_team_ix ON public.endorsement_types USING btree (team_id, sort_key);


--
-- Name: endorsements_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX endorsements_team_ix ON public.endorsements USING btree (team_id, created_at DESC);


--
-- Name: endorsements_to_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX endorsements_to_ix ON public.endorsements USING btree (to_user_id, created_at DESC);


--
-- Name: external_identities_user_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX external_identities_user_ix ON public.external_identities USING btree (user_id);


--
-- Name: guest_tokens_principal_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX guest_tokens_principal_ix ON public.guest_tokens USING btree (principal_id);


--
-- Name: mentions_user_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX mentions_user_ix ON public.mentions USING btree (user_id, created_at DESC);


--
-- Name: poker_sessions_code_ci_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX poker_sessions_code_ci_ix ON public.poker_sessions USING btree (upper(code));


--
-- Name: poker_sessions_code_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX poker_sessions_code_uq ON public.poker_sessions USING btree (code);


--
-- Name: poker_sessions_quick_idle_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX poker_sessions_quick_idle_ix ON public.poker_sessions USING btree (last_activity_at) WHERE (team_id IS NULL);


--
-- Name: poker_sessions_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX poker_sessions_team_ix ON public.poker_sessions USING btree (team_id, created_at DESC) WHERE (team_id IS NOT NULL);


--
-- Name: recent_activity_entity_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX recent_activity_entity_ix ON public.recent_activity USING btree (entity_kind, entity_id);


--
-- Name: recent_activity_user_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX recent_activity_user_ix ON public.recent_activity USING btree (user_id, last_at DESC);


--
-- Name: retro_boards_code_ci_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_boards_code_ci_ix ON public.retro_boards USING btree (upper(code));


--
-- Name: retro_boards_code_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX retro_boards_code_uq ON public.retro_boards USING btree (code);


--
-- Name: retro_boards_quick_idle_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_boards_quick_idle_ix ON public.retro_boards USING btree (last_activity_at) WHERE (team_id IS NULL);


--
-- Name: retro_boards_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_boards_team_ix ON public.retro_boards USING btree (team_id, created_at DESC) WHERE ((team_id IS NOT NULL) AND (deleted_at IS NULL));


--
-- Name: retro_cards_board_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_cards_board_ix ON public.retro_cards USING btree (board_id);


--
-- Name: retro_cards_column_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_cards_column_ix ON public.retro_cards USING btree (column_id);


--
-- Name: retro_columns_one_action_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX retro_columns_one_action_uq ON public.retro_columns USING btree (board_id) WHERE is_action_items;


--
-- Name: retro_comments_board_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_comments_board_ix ON public.retro_comments USING btree (board_id);


--
-- Name: retro_comments_card_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_comments_card_ix ON public.retro_comments USING btree (card_id, created_at);


--
-- Name: retro_votes_board_voter_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX retro_votes_board_voter_ix ON public.retro_votes USING btree (board_id, voter_id);


--
-- Name: sessions_expiry_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX sessions_expiry_ix ON public.sessions USING btree (idle_expires_at);


--
-- Name: sessions_user_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX sessions_user_ix ON public.sessions USING btree (user_id);


--
-- Name: team_action_items_assignee_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX team_action_items_assignee_ix ON public.team_action_items USING btree (assigned_to) WHERE (assigned_to IS NOT NULL);


--
-- Name: team_action_items_open_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX team_action_items_open_ix ON public.team_action_items USING btree (team_id, created_at DESC) WHERE (done_at IS NULL);


--
-- Name: team_action_items_source_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX team_action_items_source_uq ON public.team_action_items USING btree (source_card_id) WHERE (source_card_id IS NOT NULL);


--
-- Name: team_action_items_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX team_action_items_team_ix ON public.team_action_items USING btree (team_id, created_at DESC);


--
-- Name: team_invites_team_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX team_invites_team_ix ON public.team_invites USING btree (team_id) WHERE (revoked_at IS NULL);


--
-- Name: team_members_one_owner_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX team_members_one_owner_uq ON public.team_members USING btree (team_id) WHERE (role = 'owner'::text);


--
-- Name: team_members_user_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX team_members_user_ix ON public.team_members USING btree (user_id);


--
-- Name: template_columns_one_action_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX template_columns_one_action_uq ON public.template_columns USING btree (template_id) WHERE is_action_items;


--
-- Name: users_email_uq; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE UNIQUE INDEX users_email_uq ON public.users USING btree (email);


--
-- Name: users_name_prefix_ix; Type: INDEX; Schema: public; Owner: retroscope_owner
--

CREATE INDEX users_name_prefix_ix ON public.users USING btree (lower(full_name) text_pattern_ops);


--
-- Name: excluded_tables excluded_tables_import_run_fkey; Type: FK CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.excluded_tables
    ADD CONSTRAINT excluded_tables_import_run_fkey FOREIGN KEY (import_run) REFERENCES archive.import_runs(id);


--
-- Name: rows rows_import_run_fkey; Type: FK CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.rows
    ADD CONSTRAINT rows_import_run_fkey FOREIGN KEY (import_run) REFERENCES archive.import_runs(id);


--
-- Name: storage_objects storage_objects_import_run_fkey; Type: FK CONSTRAINT; Schema: archive; Owner: retroscope_owner
--

ALTER TABLE ONLY archive.storage_objects
    ADD CONSTRAINT storage_objects_import_run_fkey FOREIGN KEY (import_run) REFERENCES archive.import_runs(id);


--
-- Name: auth_tokens auth_tokens_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT auth_tokens_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: blobs blobs_board_fk; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.blobs
    ADD CONSTRAINT blobs_board_fk FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: blobs blobs_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.blobs
    ADD CONSTRAINT blobs_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: board_access_grants board_access_grants_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.board_access_grants
    ADD CONSTRAINT board_access_grants_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: board_access_grants board_access_grants_principal_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.board_access_grants
    ADD CONSTRAINT board_access_grants_principal_id_fkey FOREIGN KEY (principal_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: board_templates board_templates_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.board_templates
    ADD CONSTRAINT board_templates_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: endorsement_types endorsement_types_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsement_types
    ADD CONSTRAINT endorsement_types_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: endorsements endorsements_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: endorsements endorsements_from_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_from_user_id_fkey FOREIGN KEY (from_user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: endorsements endorsements_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: endorsements endorsements_to_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_to_user_id_fkey FOREIGN KEY (to_user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: endorsements endorsements_type_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.endorsements
    ADD CONSTRAINT endorsements_type_id_fkey FOREIGN KEY (type_id) REFERENCES public.endorsement_types(id) ON DELETE CASCADE;


--
-- Name: external_identities external_identities_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.external_identities
    ADD CONSTRAINT external_identities_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: guest_tokens guest_tokens_principal_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.guest_tokens
    ADD CONSTRAINT guest_tokens_principal_id_fkey FOREIGN KEY (principal_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: mentions mentions_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.mentions
    ADD CONSTRAINT mentions_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: mentions mentions_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.mentions
    ADD CONSTRAINT mentions_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: poker_observers poker_observers_principal_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_observers
    ADD CONSTRAINT poker_observers_principal_id_fkey FOREIGN KEY (principal_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: poker_observers poker_observers_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_observers
    ADD CONSTRAINT poker_observers_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.poker_sessions(id) ON DELETE CASCADE;


--
-- Name: poker_rounds poker_rounds_jira_points_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_jira_points_by_fkey FOREIGN KEY (jira_points_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: poker_rounds poker_rounds_pending_owner_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_pending_owner_id_fkey FOREIGN KEY (pending_owner_id) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: poker_rounds poker_rounds_revealed_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_revealed_by_fkey FOREIGN KEY (revealed_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: poker_rounds poker_rounds_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_rounds
    ADD CONSTRAINT poker_rounds_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.poker_sessions(id) ON DELETE CASCADE;


--
-- Name: poker_sessions poker_sessions_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: poker_sessions poker_sessions_current_fk; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_current_fk FOREIGN KEY (current_round_id) REFERENCES public.poker_rounds(id) ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED;


--
-- Name: poker_sessions poker_sessions_spot_fk; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_spot_fk FOREIGN KEY (spotlight_round_id) REFERENCES public.poker_rounds(id) DEFERRABLE INITIALLY DEFERRED;


--
-- Name: poker_sessions poker_sessions_spot_holder_fk; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_spot_holder_fk FOREIGN KEY (spotlight_holder_id) REFERENCES public.principals(id) DEFERRABLE INITIALLY DEFERRED;


--
-- Name: poker_sessions poker_sessions_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_sessions
    ADD CONSTRAINT poker_sessions_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: poker_votes poker_votes_principal_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_votes
    ADD CONSTRAINT poker_votes_principal_id_fkey FOREIGN KEY (principal_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: poker_votes poker_votes_round_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.poker_votes
    ADD CONSTRAINT poker_votes_round_id_fkey FOREIGN KEY (round_id) REFERENCES public.poker_rounds(id) ON DELETE CASCADE;


--
-- Name: recent_activity recent_activity_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.recent_activity
    ADD CONSTRAINT recent_activity_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: retro_boards retro_boards_archived_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_boards
    ADD CONSTRAINT retro_boards_archived_by_fkey FOREIGN KEY (archived_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: retro_boards retro_boards_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_boards
    ADD CONSTRAINT retro_boards_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: retro_boards retro_boards_focus_fk; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_boards
    ADD CONSTRAINT retro_boards_focus_fk FOREIGN KEY (focus_card_id) REFERENCES public.retro_cards(id) ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED;


--
-- Name: retro_boards retro_boards_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_boards
    ADD CONSTRAINT retro_boards_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: retro_cards retro_cards_author_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_cards
    ADD CONSTRAINT retro_cards_author_id_fkey FOREIGN KEY (author_id) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: retro_cards retro_cards_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_cards
    ADD CONSTRAINT retro_cards_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: retro_cards retro_cards_column_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_cards
    ADD CONSTRAINT retro_cards_column_id_fkey FOREIGN KEY (column_id) REFERENCES public.retro_columns(id) ON DELETE CASCADE;


--
-- Name: retro_columns retro_columns_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_columns
    ADD CONSTRAINT retro_columns_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: retro_comments retro_comments_author_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_comments
    ADD CONSTRAINT retro_comments_author_id_fkey FOREIGN KEY (author_id) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: retro_comments retro_comments_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_comments
    ADD CONSTRAINT retro_comments_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: retro_comments retro_comments_card_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_comments
    ADD CONSTRAINT retro_comments_card_id_fkey FOREIGN KEY (card_id) REFERENCES public.retro_cards(id) ON DELETE CASCADE;


--
-- Name: retro_readiness retro_readiness_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_readiness
    ADD CONSTRAINT retro_readiness_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: retro_readiness retro_readiness_principal_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_readiness
    ADD CONSTRAINT retro_readiness_principal_id_fkey FOREIGN KEY (principal_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: retro_votes retro_votes_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_votes
    ADD CONSTRAINT retro_votes_board_id_fkey FOREIGN KEY (board_id) REFERENCES public.retro_boards(id) ON DELETE CASCADE;


--
-- Name: retro_votes retro_votes_card_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_votes
    ADD CONSTRAINT retro_votes_card_id_fkey FOREIGN KEY (card_id) REFERENCES public.retro_cards(id) ON DELETE CASCADE;


--
-- Name: retro_votes retro_votes_voter_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.retro_votes
    ADD CONSTRAINT retro_votes_voter_id_fkey FOREIGN KEY (voter_id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: sessions sessions_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.sessions
    ADD CONSTRAINT sessions_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: team_action_items team_action_items_assigned_to_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_assigned_to_fkey FOREIGN KEY (assigned_to) REFERENCES public.users(id) ON DELETE SET NULL;


--
-- Name: team_action_items team_action_items_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: team_action_items team_action_items_done_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_done_by_fkey FOREIGN KEY (done_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: team_action_items team_action_items_source_board_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_source_board_id_fkey FOREIGN KEY (source_board_id) REFERENCES public.retro_boards(id) ON DELETE SET NULL;


--
-- Name: team_action_items team_action_items_source_card_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_source_card_id_fkey FOREIGN KEY (source_card_id) REFERENCES public.retro_cards(id) ON DELETE SET NULL;


--
-- Name: team_action_items team_action_items_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_action_items
    ADD CONSTRAINT team_action_items_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: team_favorites team_favorites_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_favorites
    ADD CONSTRAINT team_favorites_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: team_favorites team_favorites_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_favorites
    ADD CONSTRAINT team_favorites_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: team_invites team_invites_accepted_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_invites
    ADD CONSTRAINT team_invites_accepted_by_fkey FOREIGN KEY (accepted_by) REFERENCES public.users(id) ON DELETE SET NULL;


--
-- Name: team_invites team_invites_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_invites
    ADD CONSTRAINT team_invites_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: team_invites team_invites_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_invites
    ADD CONSTRAINT team_invites_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: team_jira_config team_jira_config_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_jira_config
    ADD CONSTRAINT team_jira_config_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: team_jira_config team_jira_config_updated_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_jira_config
    ADD CONSTRAINT team_jira_config_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: team_members team_members_team_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;


--
-- Name: team_members team_members_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;


--
-- Name: teams teams_created_by_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.teams
    ADD CONSTRAINT teams_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.principals(id) ON DELETE SET NULL;


--
-- Name: template_columns template_columns_template_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.template_columns
    ADD CONSTRAINT template_columns_template_id_fkey FOREIGN KEY (template_id) REFERENCES public.board_templates(id) ON DELETE CASCADE;


--
-- Name: users users_avatar_blob_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_avatar_blob_id_fkey FOREIGN KEY (avatar_blob_id) REFERENCES public.blobs(id) ON DELETE SET NULL;


--
-- Name: users users_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: retroscope_owner
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_id_fkey FOREIGN KEY (id) REFERENCES public.principals(id) ON DELETE CASCADE;


--
-- Name: SCHEMA public; Type: ACL; Schema: -; Owner: pg_database_owner
--

GRANT USAGE ON SCHEMA public TO retroscope_app;


--
-- Name: FUNCTION rs_fence(expected bigint); Type: ACL; Schema: public; Owner: retroscope_owner
--

REVOKE ALL ON FUNCTION public.rs_fence(expected bigint) FROM PUBLIC;
GRANT ALL ON FUNCTION public.rs_fence(expected bigint) TO retroscope_app;


--
-- Name: TABLE audit_log; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.audit_log TO retroscope_app;


--
-- Name: TABLE auth_tokens; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_tokens TO retroscope_app;


--
-- Name: TABLE blobs; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.blobs TO retroscope_app;


--
-- Name: TABLE board_access_grants; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.board_access_grants TO retroscope_app;


--
-- Name: TABLE board_templates; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.board_templates TO retroscope_app;


--
-- Name: TABLE client_cursors; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.client_cursors TO retroscope_app;


--
-- Name: TABLE email_outbox; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.email_outbox TO retroscope_app;


--
-- Name: TABLE endorsement_types; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.endorsement_types TO retroscope_app;


--
-- Name: TABLE endorsements; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.endorsements TO retroscope_app;


--
-- Name: TABLE external_identities; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.external_identities TO retroscope_app;


--
-- Name: TABLE guest_tokens; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.guest_tokens TO retroscope_app;


--
-- Name: TABLE mentions; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.mentions TO retroscope_app;


--
-- Name: TABLE poker_observers; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.poker_observers TO retroscope_app;


--
-- Name: TABLE poker_rounds; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.poker_rounds TO retroscope_app;


--
-- Name: TABLE poker_sessions; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.poker_sessions TO retroscope_app;


--
-- Name: TABLE poker_votes; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.poker_votes TO retroscope_app;


--
-- Name: TABLE principals; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.principals TO retroscope_app;


--
-- Name: TABLE recent_activity; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.recent_activity TO retroscope_app;


--
-- Name: TABLE retro_boards; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_boards TO retroscope_app;


--
-- Name: TABLE retro_cards; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_cards TO retroscope_app;


--
-- Name: TABLE retro_columns; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_columns TO retroscope_app;


--
-- Name: TABLE retro_comments; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_comments TO retroscope_app;


--
-- Name: TABLE retro_readiness; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_readiness TO retroscope_app;


--
-- Name: TABLE retro_votes; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retro_votes TO retroscope_app;


--
-- Name: TABLE schema_migrations; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT ON TABLE public.schema_migrations TO retroscope_app;


--
-- Name: TABLE sessions; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.sessions TO retroscope_app;


--
-- Name: TABLE system_state; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.system_state TO retroscope_app;


--
-- Name: TABLE team_action_items; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.team_action_items TO retroscope_app;


--
-- Name: TABLE team_favorites; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.team_favorites TO retroscope_app;


--
-- Name: TABLE team_invites; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.team_invites TO retroscope_app;


--
-- Name: TABLE team_jira_config; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.team_jira_config TO retroscope_app;


--
-- Name: TABLE team_members; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.team_members TO retroscope_app;


--
-- Name: TABLE teams; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.teams TO retroscope_app;


--
-- Name: TABLE template_columns; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.template_columns TO retroscope_app;


--
-- Name: TABLE users; Type: ACL; Schema: public; Owner: retroscope_owner
--

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.users TO retroscope_app;


--
-- PostgreSQL database dump complete
--


