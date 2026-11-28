-- Audit store (spec AUD §8). Idempotent; executed under an advisory lock.
CREATE SCHEMA IF NOT EXISTS wp_audit;

CREATE TABLE IF NOT EXISTS wp_audit.records (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    chain_id      text         NOT NULL,
    sequence      bigint       NOT NULL,
    occurred_at   timestamptz  NOT NULL,
    recorded_at   timestamptz  NOT NULL,
    action        text         NOT NULL,
    outcome       smallint     NOT NULL,
    severity      smallint     NOT NULL,
    actor         jsonb        NOT NULL,
    actor_id      uuid         NULL,
    objects       jsonb        NOT NULL,
    object_ids    uuid[]       NOT NULL,
    reason        text         NULL,
    details       jsonb        NULL,
    correlation_id uuid        NULL,
    prev_hash     bytea        NOT NULL,
    hash          bytea        NOT NULL,
    CONSTRAINT records_chain_sequence UNIQUE (chain_id, sequence)
);
CREATE INDEX IF NOT EXISTS records_occurred_at ON wp_audit.records (occurred_at, id);
CREATE INDEX IF NOT EXISTS records_actor ON wp_audit.records (actor_id, occurred_at);
CREATE INDEX IF NOT EXISTS records_object_ids ON wp_audit.records USING gin (object_ids);
CREATE INDEX IF NOT EXISTS records_action ON wp_audit.records (action text_pattern_ops);
CREATE INDEX IF NOT EXISTS records_correlation ON wp_audit.records (correlation_id) WHERE correlation_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS wp_audit.checkpoints (
    chain_id  text        NOT NULL,
    sequence  bigint      NOT NULL,
    hash      bytea       NOT NULL,
    at        timestamptz NOT NULL,
    signature bytea       NOT NULL,
    key_id    text        NOT NULL,
    PRIMARY KEY (chain_id, sequence)
);

CREATE TABLE IF NOT EXISTS wp_audit.verification_state (
    chain_id                 text PRIMARY KEY,
    last_verified_sequence   bigint      NOT NULL,
    last_full_verification_at timestamptz NULL,
    last_result              jsonb       NULL
);

-- Roles: the service writes with INSERT/SELECT only; retention deletes through the maintenance role.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wp_audit_writer') THEN
        CREATE ROLE wp_audit_writer NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wp_audit_maint') THEN
        CREATE ROLE wp_audit_maint NOLOGIN;
    END IF;
EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'wp_audit: cannot create roles; the installer must create wp_audit_writer and wp_audit_maint';
END
$$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wp_audit_writer') AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wp_audit_maint') THEN
        GRANT USAGE ON SCHEMA wp_audit TO wp_audit_writer, wp_audit_maint;
        REVOKE ALL ON wp_audit.records, wp_audit.checkpoints, wp_audit.verification_state FROM wp_audit_writer, wp_audit_maint;
        GRANT INSERT, SELECT ON wp_audit.records TO wp_audit_writer;
        GRANT INSERT, SELECT, UPDATE ON wp_audit.checkpoints, wp_audit.verification_state TO wp_audit_writer;
        GRANT SELECT, DELETE ON wp_audit.records TO wp_audit_maint;
        GRANT SELECT ON wp_audit.checkpoints TO wp_audit_maint;
        BEGIN
            EXECUTE format('GRANT wp_audit_maint TO %I', current_user);
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
    END IF;
EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'wp_audit: cannot grant privileges';
END
$$;

-- Defence in depth: updates are always rejected; deletes only by the maintenance role.
CREATE OR REPLACE FUNCTION wp_audit.reject_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'wp_audit.records is append-only' USING ERRCODE = 'insufficient_privilege';
END
$$;

CREATE OR REPLACE FUNCTION wp_audit.reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_user <> 'wp_audit_maint' THEN
        RAISE EXCEPTION 'wp_audit.records can only be deleted by the maintenance role' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN OLD;
END
$$;

DROP TRIGGER IF EXISTS records_no_update ON wp_audit.records;
CREATE TRIGGER records_no_update BEFORE UPDATE ON wp_audit.records FOR EACH ROW EXECUTE FUNCTION wp_audit.reject_update();
DROP TRIGGER IF EXISTS records_delete_guard ON wp_audit.records;
CREATE TRIGGER records_delete_guard BEFORE DELETE ON wp_audit.records FOR EACH ROW EXECUTE FUNCTION wp_audit.reject_delete();
