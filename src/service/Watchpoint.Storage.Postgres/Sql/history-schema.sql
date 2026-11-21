-- History tables (spec HIS §4). Idempotent; run by PostgresHistoryStore.EnsureSchemaAsync, not by EF migrations.
-- /*PARTITION*/ is replaced by PARTITION BY RANGE (time) without TimescaleDB, and by nothing with it (hypertables follow).
CREATE SCHEMA IF NOT EXISTS wp_history;

CREATE TABLE IF NOT EXISTS wp_history.events (
  time           timestamptz NOT NULL,
  id             uuid        NOT NULL,
  seq            bigint      NOT NULL,
  kind           text        NOT NULL,
  category       smallint    NOT NULL,
  severity       smallint    NOT NULL,
  source_time    timestamptz NULL,
  entity_id      uuid        NULL,
  entity_kind    smallint    NULL,
  server_id      uuid        NULL,
  location_id    uuid        NULL,
  location_ids   uuid[]      NULL,
  type_id        text        NULL,
  tag_keys       text[]      NULL,
  origin_type    smallint    NOT NULL,
  origin_id      uuid        NULL,
  payload        jsonb       NOT NULL,
  personal       jsonb       NULL,
  person_refs    text[]      NULL,
  title_key      text        NOT NULL,
  title_args     jsonb       NOT NULL,
  source_name    text        NULL,
  location_names text[]      NULL,
  correlation    jsonb       NULL,
  UNIQUE (id, time)
) /*PARTITION*/;
CREATE INDEX IF NOT EXISTS events_time_id ON wp_history.events (time DESC, id DESC);
CREATE INDEX IF NOT EXISTS events_entity_time ON wp_history.events (entity_id, time DESC);
CREATE INDEX IF NOT EXISTS events_kind_time ON wp_history.events (kind, time DESC);
CREATE INDEX IF NOT EXISTS events_location_ids ON wp_history.events USING gin (location_ids);
CREATE INDEX IF NOT EXISTS events_person_refs ON wp_history.events USING gin (person_refs);

CREATE TABLE IF NOT EXISTS wp_history.alarms (
  time               timestamptz NOT NULL,
  id                 uuid        NOT NULL,
  seq                bigint      NOT NULL DEFAULT 0,
  rule_id            uuid        NOT NULL,
  rule_key           text        NOT NULL,
  rule_version       bigint      NOT NULL,
  source_id          uuid        NULL,
  source_kind        smallint    NULL,
  source_name        text        NULL,
  location_id        uuid        NULL,
  location_ids       uuid[]      NULL,
  type_id            text        NULL,
  tag_keys           text[]      NULL,
  state              smallint    NOT NULL,
  priority_initial   int         NOT NULL,
  priority_final     int         NOT NULL,
  count              int         NOT NULL,
  last_occurrence_at timestamptz NOT NULL,
  acknowledged_at    timestamptz NULL,
  acknowledged_by    uuid        NULL,
  resolved_at        timestamptz NULL,
  closed_at          timestamptz NULL,
  closed_by          uuid        NULL,
  report             text        NULL,
  "owner"            jsonb       NULL,
  escalation_level   int         NOT NULL,
  procedure_id       uuid        NULL,
  procedure_version  bigint      NULL,
  false_alarm        boolean     NULL,
  UNIQUE (id, time)
) /*PARTITION*/;
CREATE INDEX IF NOT EXISTS alarms_time_id ON wp_history.alarms (time DESC, id DESC);
CREATE INDEX IF NOT EXISTS alarms_rule_time ON wp_history.alarms (rule_id, time DESC);
CREATE INDEX IF NOT EXISTS alarms_source_time ON wp_history.alarms (source_id, time DESC);
CREATE INDEX IF NOT EXISTS alarms_location_ids ON wp_history.alarms USING gin (location_ids);

CREATE TABLE IF NOT EXISTS wp_history.alarm_timeline (
  time           timestamptz NOT NULL,
  alarm_id       uuid        NOT NULL,
  record_id      uuid        NOT NULL,
  kind           text        NOT NULL,
  actor_user     uuid        NULL,
  actor_session  uuid        NULL,
  client_kind    smallint    NULL,
  body           jsonb       NOT NULL,
  UNIQUE (record_id, time)
) /*PARTITION*/;
CREATE INDEX IF NOT EXISTS alarm_timeline_alarm_time ON wp_history.alarm_timeline (alarm_id, time);

CREATE TABLE IF NOT EXISTS wp_history.commands (
  time          timestamptz NOT NULL,
  id            uuid        NOT NULL,
  seq           bigint      NOT NULL DEFAULT 0,
  group_id      uuid        NULL,
  device_id     uuid        NOT NULL,
  device_name   text        NULL,
  location_ids  uuid[]      NULL,
  path          text        NOT NULL,
  arguments     jsonb       NOT NULL,
  issuer        jsonb       NOT NULL,
  issuer_user   uuid        NULL,
  reason        text        NULL,
  risk          smallint    NOT NULL,
  priority      smallint    NOT NULL,
  state         smallint    NOT NULL,
  failure       jsonb       NULL,
  queued_at     timestamptz NULL,
  dispatched_at timestamptz NULL,
  completed_at  timestamptz NULL,
  verified_at   timestamptz NULL,
  attempt       int         NOT NULL,
  approval      jsonb       NULL,
  correlation   jsonb       NULL,
  UNIQUE (id, time)
) /*PARTITION*/;
CREATE INDEX IF NOT EXISTS commands_time_id ON wp_history.commands (time DESC, id DESC);
CREATE INDEX IF NOT EXISTS commands_device_time ON wp_history.commands (device_id, time DESC);
CREATE INDEX IF NOT EXISTS commands_issuer_time ON wp_history.commands (issuer_user, time DESC);

CREATE TABLE IF NOT EXISTS wp_history.property_changes (
  time        timestamptz NOT NULL,
  device_id   uuid        NOT NULL,
  path        text        NOT NULL,
  v_num       numeric     NULL,
  v_text      text        NULL,
  v_bool      boolean     NULL,
  v_json      jsonb       NULL,
  quality     smallint    NOT NULL,
  source_time timestamptz NULL,
  record_id   uuid        NOT NULL,
  UNIQUE (record_id, time)
) /*PARTITION*/;
CREATE INDEX IF NOT EXISTS property_changes_device_path_time ON wp_history.property_changes (device_id, path, time DESC);

-- Policy overrides per device or type (spec HIS §6); written by configuration (CFG, M3).
CREATE TABLE IF NOT EXISTS wp_history.policy_overrides (
  scope  text  NOT NULL,
  key    text  NOT NULL,
  path   text  NOT NULL,
  policy jsonb NOT NULL,
  PRIMARY KEY (scope, key, path)
);
