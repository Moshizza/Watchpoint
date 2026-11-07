-- State store tables (spec STO §7.3). Idempotent; run by the provider bootstrap, not by EF migrations.
CREATE SCHEMA IF NOT EXISTS wp_state;

CREATE TABLE IF NOT EXISTS wp_state.items (
  kind        text        NOT NULL,
  key         uuid        NOT NULL,
  partition   smallint    NOT NULL,
  payload     jsonb       NOT NULL,
  sequence    bigint      NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (kind, key)
);
CREATE INDEX IF NOT EXISTS items_kind_partition ON wp_state.items (kind, partition);

CREATE TABLE IF NOT EXISTS wp_state.checkpoints (
  consumer    text        NOT NULL,
  partition   smallint    NOT NULL,
  sequence    bigint      NOT NULL,
  written_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (consumer, partition)
);
